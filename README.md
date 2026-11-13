# Codex Smart Circle

Codex Smart Circle is an unofficial Windows desktop overlay that displays the current five-hour and seven-day usage limits reported by the local Codex App Server. It can also show a usage-history forecast when enough verified local observations are available.

This project is not affiliated with, endorsed by, or an official product of OpenAI. “Codex” is used only to identify the application this companion reads from.

## Project status

This repository contains source version `0.1.0-rc.9`. It is a release-candidate snapshot. Live visual acceptance in the running Codex Desktop UI is still pending; automated WPF layout tests and offscreen renders do not replace that manual check. No executable is included in this repository.

## Requirements

- Windows x64.
- .NET 10 SDK. The clean build was verified with SDK `10.0.404`.
- Codex CLI installed at `%APPDATA%\npm\codex.cmd`, which is the local App Server entry point used by this source. Runtime testing in the development environment used Codex CLI `0.167.2`.
- Codex Desktop available when you want to see the overlay and live usage values.

The application communicates with the local Codex App Server over standard input/output. It keeps settings and verified quota-history snapshots in `%LOCALAPPDATA%\CodexSmartCircle\`.

## Build on Windows

From PowerShell in the repository root:

```powershell
dotnet --version
dotnet build .\CodexSmartCircle.slnx --configuration Release
```

## Run the automated tests

```powershell
dotnet run --project .\CodexSmartCircle.Tests\CodexSmartCircle.Tests.csproj --configuration Release
```

The test executable reports its individual checks and a final passed/failed count. WPF tests use an STA thread and offscreen rendering; they do not verify live Codex Desktop placement, hover, dragging, or tray interactions.

## License

The project source and included icon are distributed under the MIT License. See [LICENSE](LICENSE).
