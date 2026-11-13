using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;

namespace CodexSmartCircle;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private Forms.NotifyIcon? _tray;
    private Forms.ContextMenuStrip? _menu;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "CodexSmartCircle.SingleInstance", out var created);
        if (!created) { Shutdown(); return; }
        _window = new MainWindow();
        _window.Show();
        _menu = CreateContextMenu();
        _tray = new Forms.NotifyIcon { Text = "Codex Smart Circle", Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? System.Drawing.SystemIcons.Application, ContextMenuStrip = _menu, Visible = true };
        _tray.DoubleClick += (_, _) => _window.Show();
        base.OnStartup(e);
    }

    private Forms.ContextMenuStrip CreateContextMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => _window?.Show());
        menu.Items.Add("Refresh", null, (_, _) => _window?.RefreshUsage());
        menu.Items.Add("Settings", null, (_, _) => _window?.ShowSettings());
        menu.Items.Add("Reset Position", null, (_, _) => _window?.ResetPosition());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
        return menu;
    }

    public void ShowWidgetContextMenu(int screenX, int screenY) => _menu?.Show(screenX, screenY);

    protected override void OnExit(ExitEventArgs e)
    {
        _window?.CloseOwnedResources();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _menu?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

