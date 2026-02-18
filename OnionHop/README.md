# OnionHop V2 (Avalonia + SukiUI)

This is the **V2 UI** for OnionHop, rebuilt with **Avalonia** + **SukiUI** for a cleaner, modern, cross-platform UI.

## Status

- UI: **Windows / Linux / macOS compatible**
- Networking/routing: **Windows-only for now** (Linux/macOS integration will be added later)

## Build

```powershell
dotnet build "OnionHop/OnionHopV2.sln" -c Release
```

## Run

```powershell
dotnet run --project "OnionHop/src/OnionHopV2.App" -c Release
```

On first connect, the app will ensure Tor + sing-box/Wintun dependencies in its output directory (Windows).

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
powershell -NoProfile -ExecutionPolicy Bypass -File installer/build-portable-v2.ps1
```

CLI portable ZIP:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer/build-portable-cli.ps1
```
