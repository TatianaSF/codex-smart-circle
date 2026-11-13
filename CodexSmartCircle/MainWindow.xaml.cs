using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using CodexSmartCircle.Core;
using Forms = System.Windows.Forms;

namespace CodexSmartCircle;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _usageRefreshTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private readonly UsageClient _client = new();
    private bool _disposed;
    private bool _usageMenuOpen;
    private int _offsetX;
    private int _offsetY;
    private bool _isDragging;
    private double _dragOffsetX;
    private double _dragOffsetY;
    private double _dragOriginX;
    private double _dragOriginY;
    private WidgetSettings _settings = WidgetSettingsStore.Load();
    private WidgetStyle _style;
    private bool _wasCodexForeground;
    private DateTimeOffset? _lastUsageSuccessUtc;
    private string? _lastUsageTrigger;
    private string _connectionState = "Connecting";
    private string? _lastUsageError;
    private bool _settingsDialogOpen;
    private double _widgetScale = WidgetLayout.DefaultScale;
    private UsageSnapshot? _currentSnapshot;
    private readonly ForecastGenerationGate _forecastGeneration = new();
    private readonly UsageHistoryStore _usageHistory = new();
    public UsageForecast CurrentForecast { get; private set; } = UsageForecast.Unavailable(ForecastUnavailableReason.InsufficientHistory);
    private Action? _refreshSettingsControls;
    public MainWindow()
    {
        InitializeComponent(); ShowActivated = false;
        _style = _settings.SelectedStyle;
        if (_settings.Positions.TryGetValue(_style, out var saved)) { _dragOffsetX = saved.X; _dragOffsetY = saved.Y; }
        ApplyStyle();
        Root.ToolTip = new System.Windows.Controls.ToolTip { Content = "Waiting for rate-limit data.", StaysOpen = true, Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse, Focusable = false };
        Root.PreviewMouseLeftButtonDown += OnDragStart;
        Root.PreviewMouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (GetCursorPos(out var cursor) && System.Windows.Application.Current is App app) app.ShowWidgetContextMenu(cursor.X, cursor.Y);
        };
        _timer.Tick += (_, _) => UpdateOverlay(); _timer.Start();
        _usageRefreshTimer.Tick += (_, _) => { if (_wasCodexForeground) _client.RefreshUsage("periodic"); };
        _usageRefreshTimer.Start();
        Loaded += async (_, _) => await _client.StartAsync(ApplySnapshot);
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | 0x08000000 | 0x00000080);
    }
    private void ApplySnapshot(UsageSnapshot data)
    {
        var forecastGeneration = _forecastGeneration.Advance();
        CurrentForecast = UsageForecast.Unavailable(ForecastUnavailableReason.InsufficientHistory);
        _currentSnapshot = data;
        if (data.UnavailableReason is null && data.QuotaScope is { } quotaScope &&
            _client.Identity.Status == AccountIdentityStatus.Verified && _client.Identity.QuotaScopeAvailable &&
            string.Equals(_client.Identity.PrivateScopeKey, quotaScope, StringComparison.Ordinal) &&
            data.FiveHour?.ResetsAtUtc is { } reset5 && data.SevenDay?.ResetsAtUtc is { } resetWeek)
            _ = Task.Run(async () =>
            {
                var now = DateTimeOffset.UtcNow;
                var observation = new ForecastObservation(now, 100 - data.FiveHour.RemainingPercent, 100 - data.SevenDay.RemainingPercent, reset5.ToUniversalTime(), resetWeek.ToUniversalTime(), quotaScope);
                await _usageHistory.AppendAsync(observation, now);
                var history = await _usageHistory.LoadForScopeAsync(quotaScope, now);
                var forecast = UsageForecaster.Calculate(history, now);
                if (!_forecastGeneration.IsCurrent(forecastGeneration)) return;
                CurrentForecast = forecast;
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_forecastGeneration.IsCurrent(forecastGeneration)) UpdateForecastLabels(DateTimeOffset.UtcNow);
                });
            });
        Dispatcher.Invoke(() =>
        {
            _lastUsageTrigger = _client.CurrentTrigger;
            var fiveText = data.FiveHour is null ? "--" : $"{data.FiveHour.RemainingPercent}%";
            var weekText = data.SevenDay is null ? "--" : $"{data.SevenDay.RemainingPercent}%";
            var fiveReset = data.FiveHour?.ResetsAtUtc;
            var resetText = fiveReset is null ? "--" : RateLimitParser.FormatReset(fiveReset);
            ResetText.Text = OtherResetText.Text = resetText;
            FiveText.Text = OtherFiveText.Text = fiveText;
            WeekText.Text = OtherWeekText.Text = weekText;
            ApplyCircleBottomTypography();
            UpdateForecastLabels(DateTimeOffset.UtcNow);
        var radius = Math.Min(Width, Height) * 0.46;
        SetArc(InnerTrack, radius * (24d / 28d), 100); SetArc(OuterTrack, radius, 100);
        SetArc(InnerArc, radius * (24d / 28d), data.FiveHour?.RemainingPercent ?? 0); SetArc(OuterArc, radius, data.SevenDay?.RemainingPercent ?? 0);
            if (data.UnavailableReason is null) { _lastUsageSuccessUtc = DateTimeOffset.UtcNow; _connectionState = "Connected"; _lastUsageError = null; }
            else { _lastUsageError = data.UnavailableReason; if (data.FiveHour is null && data.SevenDay is null) _connectionState = "Unavailable"; }
            var diagnostic = new RefreshDiagnostics(_lastUsageSuccessUtc, _lastUsageTrigger, _connectionState, _lastUsageError);
            var stale = _lastUsageSuccessUtc is { } last && DateTimeOffset.UtcNow - last > TimeSpan.FromMinutes(2);
            var forecastTip = CurrentForecast.Availability == ForecastAvailability.Available ? $"Forecast: ~{CurrentForecast.RemainingFullCycles} full 5-hour budgets; confidence {CurrentForecast.Confidence}" : $"Forecast unavailable: {_client.Identity.Status}; insufficient verified account/quota history";
            var tip = (data.UnavailableReason ?? $"5-hour remaining: {FiveText.Text}; resets {RateLimitParser.FormatReset(data.FiveHour?.ResetsAtUtc)}\n7-day remaining: {WeekText.Text}; resets {RateLimitParser.FormatReset(data.SevenDay?.ResetsAtUtc)}") + $"\n{forecastTip}\n{diagnostic.FormatStatus(DateTimeOffset.UtcNow)}" + (stale ? " (STALE)" : string.Empty);
            ((System.Windows.Controls.ToolTip)Root.ToolTip).Content = tip;
        });
    }
    private void SetArc(System.Windows.Shapes.Path path, double radius, double percent)
    {
        var sweep = Math.Clamp(percent, 0, 100) * 3.598;
        var start = -90d; var end = start + sweep;
        var centerX = Width / 2; var centerY = Height / 2;
        var a = new System.Windows.Point(centerX + Math.Cos(start * Math.PI / 180) * radius, centerY + Math.Sin(start * Math.PI / 180) * radius);
        var b = new System.Windows.Point(centerX + Math.Cos(end * Math.PI / 180) * radius, centerY + Math.Sin(end * Math.PI / 180) * radius);
        path.Data = new PathGeometry([new PathFigure(a, [new ArcSegment(b, new System.Windows.Size(radius, radius), 0, sweep > 180, SweepDirection.Clockwise, true)], false)]);
    }
    private void UpdateOverlay()
    {
        UpdateForecastLabels(DateTimeOffset.UtcNow);
        if (_isDragging) return;
        if (_settingsDialogOpen) { if (!IsVisible) Show(); return; }
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !IsCodexHandle(hwnd) || IsIconic(hwnd) || !GetWindowRect(hwnd, out var rect)) { _wasCodexForeground = false; Hide(); return; }
        if (!_wasCodexForeground) { _wasCodexForeground = true; _lastUsageTrigger = "foreground"; _client.RefreshUsage("foreground"); }
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (GetCursorPos(out var cursor))
        {
            var onUsageButton = cursor.X < rect.Left + 68 * dpi && cursor.Y >= rect.Bottom - 190 * dpi;
            var onUsageMenu = cursor.X >= rect.Left + 60 * dpi && cursor.X <= rect.Left + 420 * dpi &&
                              cursor.Y >= rect.Bottom - 340 * dpi && cursor.Y <= rect.Bottom - 55 * dpi;
            if (onUsageButton) _usageMenuOpen = true;
            else if (!onUsageMenu) _usageMenuOpen = false;
        }
        if (_usageMenuOpen) { Hide(); return; }
        if (!IsVisible) Show();
        var pos = OverlayPlacement.Calculate(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, dpi, Width);
        var leftBound = rect.Left / dpi; var topBound = rect.Top / dpi;
        var maxLeft = Math.Max(leftBound, leftBound + (rect.Right - rect.Left) / dpi - Width);
        var maxTop = Math.Max(topBound, topBound + (rect.Bottom - rect.Top) / dpi - Height);
        Left = Math.Clamp(pos.Left + _offsetX + _dragOffsetX, leftBound, maxLeft);
        Top = Math.Clamp(pos.Top + _offsetY + _dragOffsetY, topBound, maxTop);
    }
    private void OnDragStart(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!WidgetInteractionPolicy.CanStartDrag(e.ChangedButton == System.Windows.Input.MouseButton.Left) || e.ButtonState != System.Windows.Input.MouseButtonState.Pressed) return;
        e.Handled = true;
        _dragOriginX = Left; _dragOriginY = Top;
        _isDragging = true;
        ((System.Windows.Controls.ToolTip)Root.ToolTip).IsOpen = false;
        try { DragMove(); }
        catch (InvalidOperationException) { }
        finally
        {
            _isDragging = false;
            ClampToCurrentMonitor();
            _dragOffsetX += Left - _dragOriginX;
            _dragOffsetY += Top - _dragOriginY;
            if (!_disposed) { SaveCurrentPosition(); UpdateOverlay(); }
        }
    }
    private void CancelDrag()
    {
        if (!_isDragging) return;
        _isDragging = false;
        Left = _dragOriginX; Top = _dragOriginY;
        if (!_disposed) UpdateOverlay();
    }
    private void SaveCurrentPosition() { _settings.Positions[_style] = new WidgetPosition { X = _dragOffsetX, Y = _dragOffsetY }; _settings.SelectedStyle = _style; WidgetSettingsStore.Save(_settings); }
    public void ResetPosition() { if (_isDragging) CancelDrag(); _dragOffsetX = 0; _dragOffsetY = 0; _offsetX = 0; _offsetY = 0; _settings.ResetPosition(_style); _settings.Select(_style); WidgetSettingsStore.Save(_settings); UpdateOverlay(); }
    private void SelectStyle(WidgetStyle style)
    {
        var oldOffsetX = _dragOffsetX; var oldOffsetY = _dragOffsetY;
        SaveCurrentPosition(); _style = style; _settings.SelectedStyle = style;
        if (_settings.Positions.TryGetValue(style, out var saved)) { _dragOffsetX = saved.X; _dragOffsetY = saved.Y; }
        else { _dragOffsetX = 0; _dragOffsetY = 0; }
        ApplyStyle();
        if (_settingsDialogOpen) { Left += _dragOffsetX - oldOffsetX; Top += _dragOffsetY - oldOffsetY; ClampToCurrentMonitor(); }
        SaveCurrentPosition(); UpdateOverlay();
        _refreshSettingsControls?.Invoke();
    }
    private void ApplyStyle()
    {
        _widgetScale = _settings.GetScale(_style);
        var size = WidgetLayout.DimensionsFor(_style, _widgetScale);
        Width = Root.Width = size.Width;
        Height = Root.Height = size.Height;
        var fontScale = _widgetScale * (_style == WidgetStyle.Circle ? 1.5 : 1.0);
        ResetText.FontSize = 8 * fontScale; FiveText.FontSize = 13.2 * fontScale; WeekText.FontSize = 8.2 * fontScale;
        ForecastText.FontSize = 8.2 * fontScale;
        OtherResetText.FontSize = 8 * _widgetScale; OtherFiveText.FontSize = 14 * _widgetScale;
        OtherWeekText.FontSize = 6 * _widgetScale; OtherForecastText.FontSize = 6 * _widgetScale;
        var corner = _style switch { WidgetStyle.Circle => 45, WidgetStyle.VerticalPill => 30, WidgetStyle.RoundedSquare => 12, _ => 20 };
        StyleShape.CornerRadius = new CornerRadius(corner * _widgetScale);
        StyleShape.BorderThickness = new Thickness(_widgetScale);
        var ringThickness = _widgetScale;
        OuterTrack.StrokeThickness = OuterArc.StrokeThickness = 1.6 * ringThickness;
        InnerTrack.StrokeThickness = InnerArc.StrokeThickness = 2.2 * ringThickness;
        var minSize = Math.Min(Width, Height);
        var isCircle = _style == WidgetStyle.Circle;
        CircleLabels.Visibility = isCircle ? Visibility.Visible : Visibility.Collapsed;
        OtherLabels.Visibility = isCircle ? Visibility.Collapsed : Visibility.Visible;
        if (isCircle)
        {
            CircleLabels.Margin = new Thickness(0);
            ResetText.Margin = new Thickness(0, minSize * 0.147, 0, 0);
            FiveText.Margin = new Thickness(0, -minSize * 0.047, 0, 0);
            WeekText.Margin = new Thickness(minSize * 0.30, 0, 0, minSize * 0.1973);
            ForecastText.Margin = new Thickness(0, 0, minSize * 0.29, minSize * 0.1973);
        }
        else
        {
            OtherLabels.Margin = new Thickness(minSize * 0.2);
        }
        var margin = minSize * (1.5 / 60);
        Ellipse.Margin = new Thickness(margin);
        Ellipse.StrokeThickness = _widgetScale;
        Ellipse.Visibility = _style == WidgetStyle.Circle ? Visibility.Visible : Visibility.Collapsed;
        ApplyCircleBottomTypography();
        ApplyCurrentUsageArcs();
    }
    private void UpdateForecastLabels(DateTimeOffset nowUtc)
    {
        var text = GetForecastIndicatorText(CurrentForecast, nowUtc);
        ForecastText.Text = text;
        OtherForecastText.Text = text;
        ApplyCircleBottomTypography();
    }
    private void ApplyCircleBottomTypography()
    {
        if (_style != WidgetStyle.Circle) return;
        var weekIsWide = WeekText.Text.Length >= 4;
        var forecastIsWide = ForecastText.Text.Length >= 2;
        var baseSize = weekIsWide && forecastIsWide ? 6d : weekIsWide || forecastIsWide ? 7.3d : 8.2d;
        var fontScale = _widgetScale * 1.5;
        WeekText.FontSize = ForecastText.FontSize = baseSize * fontScale;
    }
    private static string GetForecastIndicatorText(UsageForecast forecast, DateTimeOffset nowUtc)
    {
        if (forecast.Availability != ForecastAvailability.Available || forecast.RemainingFullCycles is not { } cycles ||
            forecast.LastObservationUtc is not { } observedAt || observedAt.Offset != TimeSpan.Zero ||
            observedAt > nowUtc.AddMinutes(1) || nowUtc - observedAt > TimeSpan.FromMinutes(2)) return "-";
        return cycles.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    private void ApplyCurrentUsageArcs()
    {
        var radius = Math.Min(Width, Height) * (_style == WidgetStyle.Circle ? 0.46 : 0.33);
        SetArc(InnerTrack, radius * (24d / 28d), 100); SetArc(OuterTrack, radius, 100);
        SetArc(InnerArc, radius * (24d / 28d), _currentSnapshot?.FiveHour?.RemainingPercent ?? 0);
        SetArc(OuterArc, radius, _currentSnapshot?.SevenDay?.RemainingPercent ?? 0);
    }
    private void ClampToCurrentMonitor()
    {
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)(Left * VisualTreeHelper.GetDpi(this).DpiScaleX), (int)(Top * VisualTreeHelper.GetDpi(this).DpiScaleY)));
        var dpiX = VisualTreeHelper.GetDpi(this).DpiScaleX; var dpiY = VisualTreeHelper.GetDpi(this).DpiScaleY;
        var area = screen.WorkingArea;
        var left = area.Left / dpiX; var top = area.Top / dpiY; var right = area.Right / dpiX; var bottom = area.Bottom / dpiY;
        Left = Math.Clamp(Left, left, Math.Max(left, right - Width)); Top = Math.Clamp(Top, top, Math.Max(top, bottom - Height));
    }
    private static bool IsCodexHandle(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        try { using var p = Process.GetProcessById((int)pid); return WindowClassification.IsCodex(p.ProcessName, GetTitle(hwnd)); } catch { return false; }
    }
    private static string GetTitle(IntPtr hwnd) { var b = new StringBuilder(512); GetWindowText(hwnd, b, b.Capacity); return b.ToString(); }
    public void RefreshUsage() => _client.RefreshUsage("manual");
    public void ShowSettings()
    {
        var dialog = new Window { Title = "Codex Smart Circle Settings", Width = 520, Height = 390, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen, Topmost = true, ShowInTaskbar = false };
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Widget Style", FontWeight = FontWeights.Bold, Margin = new Thickness(0,0,0,12) });
        var cards = new System.Windows.Controls.WrapPanel();
        var styleCards = new Dictionary<WidgetStyle, System.Windows.Controls.Border>();
        foreach (var style in Enum.GetValues<WidgetStyle>())
        {
            var selected = style == _style;
            var label = style switch { WidgetStyle.RoundedSquare => "Rounded Square", WidgetStyle.VerticalPill => "Vertical Pill", WidgetStyle.HorizontalBar => "Horizontal Bar", _ => "Circle" };
            var preview = new System.Windows.Controls.Border { Width = style == WidgetStyle.HorizontalBar ? 76 : style == WidgetStyle.VerticalPill ? 38 : 54, Height = style == WidgetStyle.HorizontalBar ? 34 : style == WidgetStyle.VerticalPill ? 62 : 54, CornerRadius = style switch { WidgetStyle.Circle => new CornerRadius(27), WidgetStyle.RoundedSquare => new CornerRadius(10), WidgetStyle.VerticalPill => new CornerRadius(19), _ => new CornerRadius(16) }, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(17,23,32)), BorderBrush = selected ? System.Windows.Media.Brushes.DeepSkyBlue : System.Windows.Media.Brushes.Gray, BorderThickness = new Thickness(selected ? 2 : 1), Margin = new Thickness(5), Child = new System.Windows.Controls.TextBlock { Text = label, Foreground = System.Windows.Media.Brushes.White, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = System.Windows.VerticalAlignment.Center, FontSize = 10 } };
            var button = new System.Windows.Controls.Button { Content = preview, Padding = new Thickness(2), Margin = new Thickness(3) };
            button.Click += (_, _) => SelectStyle(style);
            styleCards[style] = preview;
            cards.Children.Add(button);
        }
        panel.Children.Add(cards);
        var sizeHeader = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 18, 0, 3) };
        sizeHeader.Children.Add(new System.Windows.Controls.TextBlock { Text = "Widget Size", FontWeight = FontWeights.Bold });
        var percent = new System.Windows.Controls.TextBlock { HorizontalAlignment = System.Windows.HorizontalAlignment.Right, TextAlignment = TextAlignment.Right };
        System.Windows.Controls.DockPanel.SetDock(percent, System.Windows.Controls.Dock.Right); sizeHeader.Children.Add(percent); panel.Children.Add(sizeHeader);
        var dimensions = new System.Windows.Controls.TextBlock { Foreground = System.Windows.Media.Brushes.Gray, Margin = new Thickness(0,0,0,6) };
        var slider = new System.Windows.Controls.Slider { Minimum = 50, Maximum = 250, TickFrequency = 10, IsSnapToTickEnabled = true, LargeChange = 10, SmallChange = 10, Value = _widgetScale * 100, MinWidth = 450 };
        void UpdateSizeLabel()
        {
            percent.Text = $"{slider.Value:0}%";
            var baseSize = WidgetLayout.SizeFor(_style);
            dimensions.Text = $"{baseSize.Width * slider.Value / 100:0} x {baseSize.Height * slider.Value / 100:0} DIP";
        }
        _refreshSettingsControls = () => { slider.Value = _widgetScale * 100; UpdateSizeLabel(); foreach(var item in styleCards) item.Value.BorderBrush = item.Key == _style ? System.Windows.Media.Brushes.DeepSkyBlue : System.Windows.Media.Brushes.Gray; };
        slider.ValueChanged += (_, _) =>
        {
            _settings.SetScale(_style, slider.Value / 100); _widgetScale = _settings.GetScale(_style);
            ApplyStyle(); ClampToCurrentMonitor(); UpdateOverlay(); UpdateSizeLabel();
        };
        slider.PreviewKeyUp += (_, _) => { SaveCurrentPosition(); };
        slider.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent, new System.Windows.Controls.Primitives.DragCompletedEventHandler((_, _) => SaveCurrentPosition()));
        panel.Children.Add(slider); panel.Children.Add(dimensions); UpdateSizeLabel();
        var done = new System.Windows.Controls.Button { Content = "Done", Width = 86, Height = 30, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        done.Click += (_, _) => { SaveCurrentPosition(); dialog.Close(); };
        var version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "unknown";
        var footer = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
        footer.Children.Add(new System.Windows.Controls.TextBlock { Text = $"Version {version}", Foreground = System.Windows.Media.Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 });
        System.Windows.Controls.DockPanel.SetDock(done, System.Windows.Controls.Dock.Right); footer.Children.Add(done); panel.Children.Add(footer);
        dialog.Content = panel; _settingsDialogOpen = true;
        try { dialog.ShowDialog(); } finally { _settingsDialogOpen = false; _refreshSettingsControls = null; UpdateOverlay(); }
    }
    public void CloseOwnedResources() { if (_disposed) return; if (_isDragging) CancelDrag(); SaveCurrentPosition(); _disposed = true; _timer.Stop(); _usageRefreshTimer.Stop(); _client.Dispose(); Close(); }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
}

internal sealed class UsageClient : IDisposable
{
    private Process? _process;
    private StreamWriter? _input;
    private Action<UsageSnapshot>? _callback;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Dictionary<int, TaskCompletionSource<string>> _pending = [];
    private readonly object _refreshSync = new();
    private readonly object _scopeSync = new();
    private readonly QuotaScopeVerifier _scopeVerifier = new();
    private long _accountContextGeneration;
    private string? _queuedTrigger;
    private bool _refreshWorkerRunning;
    private string _currentTrigger = "startup";
    public AccountIdentity Identity { get; private set; } = AccountIdentity.Unknown();
    private int _identityRequestId = 100000;
    public string CurrentTrigger => _currentTrigger;
    private int _id = 1;
    public Task StartAsync(Action<UsageSnapshot> callback) { _callback = callback; _ = Task.Run(RunLoopAsync); return Task.CompletedTask; }
    private async Task RunLoopAsync()
    {
        var retrySeconds = 2;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var cmd = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "codex.cmd");
                var psi = new ProcessStartInfo("cmd.exe", $"/d /s /c \"\"{cmd}\" app-server --stdio\"") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                _process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Codex App Server.");
                _input = _process.StandardInput;
                var readerTask = ReadLoopAsync(_process.StandardOutput, _stop.Token);
                _ = _process.StandardError.ReadToEndAsync();
                await SendAsync(new { method = "initialize", id = 1, @params = new { clientInfo = new { name = "codex-smart-circle", title = "Codex Smart Circle", version = "1.0.0" }, capabilities = new { experimentalApi = false } } });
                await WaitForAsync(1, TimeSpan.FromSeconds(20));
                await SendAsync(new { method = "initialized" });
                _ = RefreshIdentityAsync();
                retrySeconds = 2;
                RefreshUsage("startup/reconnect");
                await Task.WhenAny(_process.WaitForExitAsync(_stop.Token), readerTask);
                if (_stop.IsCancellationRequested) break;
                throw new InvalidOperationException("Codex App Server exited.");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _callback?.Invoke(UsageSnapshot.Unavailable($"Usage unavailable ({ex.GetType().Name}).")); }
            Cleanup();
            try { await Task.Delay(TimeSpan.FromSeconds(retrySeconds), _stop.Token); retrySeconds = Math.Min(retrySeconds * 2, 30); } catch { break; }
        }
    }
    private async Task ReadLoopAsync(StreamReader reader, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(token); if (line is null) return;
            try
            {
                using var d = JsonDocument.Parse(line); var root = d.RootElement;
                if (root.TryGetProperty("id", out var id) && id.TryGetInt32(out var n))
                { lock (_pending) if (_pending.Remove(n, out var t)) t.TrySetResult(line); }
                else if (root.TryGetProperty("method", out var method) && method.GetString() == "account/rateLimits/updated") RefreshUsage("notification");
                else if (root.TryGetProperty("method", out method) && method.GetString() == "account/updated") _ = HandleAccountUpdatedAsync();
            }
            catch (JsonException) { _callback?.Invoke(UsageSnapshot.Unavailable("App Server returned invalid JSON.")); }
            catch (Exception ex) { _callback?.Invoke(UsageSnapshot.Unavailable($"App Server read error ({ex.GetType().Name}).")); }
        }
    }
    private async Task SendAsync(object value)
    {
        await _write.WaitAsync(_stop.Token);
        try { await _input!.WriteLineAsync(JsonSerializer.Serialize(value)); await _input.FlushAsync(_stop.Token); }
        finally { _write.Release(); }
    }
    private async Task<string> WaitForAsync(int id, TimeSpan timeout)
    {
        var t = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); lock (_pending) _pending[id] = t;
        return await t.Task.WaitAsync(timeout, _stop.Token);
    }
    public void RefreshUsage(string trigger)
    {
        lock (_refreshSync)
        {
            _queuedTrigger = trigger;
            if (_refreshWorkerRunning) return;
            _refreshWorkerRunning = true;
            _ = ProcessRefreshQueueAsync();
        }
    }
    private async Task ProcessRefreshQueueAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            string trigger;
            lock (_refreshSync)
            {
                if (_queuedTrigger is null) { _refreshWorkerRunning = false; return; }
                trigger = _queuedTrigger; _queuedTrigger = null;
            }
            _currentTrigger = trigger;
            for (var attempt = 0; attempt < 3 && !_stop.IsCancellationRequested; attempt++)
            {
                try { await RequestLimitsAsync(); break; }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    if (attempt == 2) _callback?.Invoke(UsageSnapshot.Unavailable($"Usage refresh failed ({ex.GetType().Name}) after retries."));
                    else try { await Task.Delay(TimeSpan.FromSeconds(2 << attempt), _stop.Token); } catch (OperationCanceledException) { break; }
                }
            }
        }
        lock (_refreshSync) _refreshWorkerRunning = false;
    }
    private async Task RequestLimitsAsync()
    {
        var accountContextGeneration = Volatile.Read(ref _accountContextGeneration);
        var id = Interlocked.Increment(ref _id) + 1; var response = WaitForAsync(id, TimeSpan.FromSeconds(25));
        await SendAsync(new { method = "account/rateLimits/read", id, @params = new { } });
        var raw = await response;
        var diag = QuotaDiagnostics.Parse(raw);
        if (accountContextGeneration != Volatile.Read(ref _accountContextGeneration)) { RefreshUsage("account-context-changed"); return; }

        var identity = Identity;
        QuotaScopeVerification verification;
        lock (_scopeSync) verification = _scopeVerifier.Observe(identity.AccountType, raw);
        if (accountContextGeneration != Volatile.Read(ref _accountContextGeneration)) { RefreshUsage("account-context-changed"); return; }
        if (verification.Verified && verification.ScopeKey is { } scopeKey)
            Identity = new(AccountIdentityStatus.Verified, scopeKey, identity.AccountType, false, true, DateTimeOffset.UtcNow);
        else
            Identity = new(identity.AccountType == "chatgpt" ? AccountIdentityStatus.PendingQuotaScope : identity.Status,
                null, identity.AccountType, false, false, null);

        var snapshot = RateLimitParser.Parse(raw);
        if (verification.Verified && snapshot.UnavailableReason is null) snapshot = snapshot with { QuotaScope = verification.ScopeKey };
        var allowed = verification.Verified && snapshot.UnavailableReason is null && snapshot.QuotaScope is not null;
        SafeDiagnosticStore.Save(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            accountRead = Identity.Status.ToString(),
            identitySource = Identity.AccountType == "chatgpt" ? "account.type plus repeated rate-limit accountId and limitId" : "unverified",
            accountType = Identity.AccountType,
            userId = "not required by current account/read schema",
            quotaRpcSucceeded = diag.RpcSucceeded,
            diag.AccountIdState,
            diag.BucketsAvailable,
            diag.LimitIdsState,
            diag.FiveHourState,
            diag.WeeklyState,
            quotaScope = allowed ? "verified" : diag.Classification,
            stableQuotaObservations = verification.ConsecutiveObservations,
            historyRecordingAllowed = allowed,
            historyBlockReason = allowed ? null : verification.State
        });
        _callback?.Invoke(snapshot);
    }

    private async Task HandleAccountUpdatedAsync()
    {
        Interlocked.Increment(ref _accountContextGeneration);
        lock (_scopeSync) _scopeVerifier.Reset();
        Identity = AccountIdentity.Unknown();
        _callback?.Invoke(UsageSnapshot.Unavailable("Account context changed; revalidating quota scope."));
        await RefreshIdentityAsync();
        if (!_stop.IsCancellationRequested) RefreshUsage("account-updated");
    }
    private async Task RefreshIdentityAsync()
    {
        var id = Interlocked.Increment(ref _identityRequestId);
        var accountContextGeneration = Volatile.Read(ref _accountContextGeneration);
        try
        {
            var response = WaitForAsync(id, TimeSpan.FromSeconds(8));
            await SendAsync(new { method = "account/read", id, @params = new { refreshToken = false } });
            var raw = await response;
            var parsed = AccountIdentity.Parse(raw, DateTimeOffset.UtcNow);
            if (id == Volatile.Read(ref _identityRequestId) && accountContextGeneration == Volatile.Read(ref _accountContextGeneration))
            {
                Identity = parsed;
                SafeDiagnosticStore.Save(new { timestampUtc = DateTimeOffset.UtcNow, accountRead = parsed.Status.ToString(), identitySource = parsed.AccountType == "chatgpt" ? "account.type; quota scope pending" : "unverified", userId = "not required by current account/read schema", accountType = parsed.AccountType, quotaRpcSucceeded = false, quotaScope = "pending", stableQuotaObservations = 0, historyRecordingAllowed = false, historyBlockReason = "awaiting two matching fresh quota observations" });
            }
        }
        catch (Exception ex)
        {
            if (id == Volatile.Read(ref _identityRequestId)) Identity = AccountIdentity.Unknown(ex.Message.Contains("method", StringComparison.OrdinalIgnoreCase) ? AccountIdentityStatus.Unsupported : AccountIdentityStatus.Error);
        }
    }
    private void Cleanup() { try { if (_process is { HasExited: false }) _process.Kill(true); } catch { } _process?.Dispose(); _process = null; _input = null; }
    public void Dispose() { _stop.Cancel(); Cleanup(); lock (_refreshSync) _queuedTrigger = null; _stop.Dispose(); _write.Dispose(); }
}
