# OnionHop V3 (Avalonia + SukiUI)

This is the **V3 UI** for OnionHop, rebuilt with **Avalonia** + **SukiUI** for a cleaner, modern, cross-platform UI.

## Status

- GUI releases: **Windows + macOS**
- Windows: **GUI + CLI**
- macOS: **GUI release available** (no CLI package yet)
- Linux: **source/build support is present**, packaged release still pending

## Build

```powershell
dotnet build "OnionHop/OnionHopV2.sln" -c Release
```

## Run

```powershell
dotnet run --project "OnionHop/src/OnionHopV2.App" -c Release
```

On first connect, the app will ensure the required Tor/VPN dependencies are available for the active platform.

## Run CLI

```powershell
dotnet run --project "OnionHop/src/OnionHopV2.Cli" -c Release
```

CLI quick start:

```powershell
connect --smart on
countries
connect --smart off --exit us --entry nl
status
disconnect
```

## Build CLI Installer (Windows)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer/build-installer-cli.ps1
```

## Build Portable Packages (Windows)

GUI portable ZIP:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer/build-portable-v3.ps1
```

CLI portable ZIP:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer/build-portable-cli.ps1
```
