# OnionHop V3

OnionHop V3 is the Avalonia + FluentAvalonia desktop client and CLI for routing traffic through Tor, bridges, and TUN/VPN mode.

## Status

- GUI releases: Windows, Linux AppImage, and macOS from the dedicated macOS release repo.
- CLI releases: Windows and Linux.
- Target framework: .NET 9.

## Build

```powershell
dotnet build "OnionHop/OnionHopV3.sln" -c Release
```

## Run GUI

```powershell
dotnet run --project "OnionHop/src/OnionHopV3.App" -c Release
```

On first connect, the app ensures the required Tor/VPN dependencies are available for the active platform.

## Run CLI

```powershell
dotnet run --project "OnionHop/src/OnionHopV3.Cli" -c Release
```

CLI quick start:

```text
connect --smart on
countries
connect --smart off --exit us --entry nl
status
disconnect
```

## Packaging

Windows installer:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer/build-installer-v3.ps1
```

Windows GUI portable ZIP:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer/build-portable-v3.ps1
```

Windows CLI installer:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer/build-installer-cli.ps1
```

Windows CLI portable ZIP:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer/build-portable-cli.ps1
```
