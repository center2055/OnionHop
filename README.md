# OnionHop

<div align="center">
  <img src="logo.png" alt="OnionHop Logo" width="200"/>
</div>

<div align="center">
  <img src="screenshot.png" alt="OnionHop UI Screenshot" width="800"/>
</div>

<div align="center">
  <a href="https://github.com/center2055/OnionHop/releases">
    <img src="https://img.shields.io/badge/Download-Latest%20Release-blue?style=for-the-badge&logo=github" alt="Download Latest Release"/>
  </a>
</div>

**OnionHop** is a lightweight Windows WPF app that routes your traffic through **Tor** using either:

- **Proxy Mode (recommended):** sets the Windows proxy to Tor's local SOCKS5 endpoint.
- **TUN/VPN Mode (Admin):** starts a system-wide tunnel via **sing-box + Wintun**.

It includes a **Hybrid** option (browser-only via Tor in TUN mode) and an optional **Kill Switch** for leak prevention.

> **Disclaimer**
> OnionHop is provided "as-is". Tor usage can be illegal or restricted in some jurisdictions. You are responsible for complying with local laws and regulations.

---

## Getting Started (User)

1) Install  
   - Download the latest release from the [Releases](https://github.com/center2055/OnionHop/releases) section.
   - Run the Windows installer (`OnionHop-Setup-<version>.exe`).

2) Choose a mode  
   - **Proxy Mode (no admin):** Sets Windows proxy to Tor SOCKS (best compatibility).  
   - **TUN/VPN Mode (admin):** System-wide tunnel via sing-box + Wintun; required if apps ignore proxy settings.

3) Connect  
   - Optionally select an **Exit Location**.  
   - For TUN mode, toggle **Hybrid** if you want browsers via Tor and other apps direct.  
   - Click **Connect**. Use **Disconnect** to stop Tor/tunnel (run as Administrator to fully clear kill-switch rules).

Notes
- Kill Switch works only in strict TUN (Hybrid off) and needs admin rights to add/remove firewall rules.  
- Dark Mode currently affects UI only.  
- Bundled binaries live under `OnionHop/OnionHop/tor/` and `OnionHop/OnionHop/vpn/`. Unsigned binaries can trigger AV warnings — allow only if you trust the source.

---

## Features

- Tor routing (SOCKS5)
- System proxy mode (no admin required)
- TUN/VPN mode via sing-box + Wintun (admin required)
- Hybrid routing (in TUN mode: browsers via Tor, other apps direct)
- Kill Switch (strict TUN only)
  - If the tunnel drops unexpectedly, OnionHop blocks outbound traffic using Windows Firewall to prevent leaks.
  - Disconnect (as Administrator) to restore normal traffic.
- Persisted settings
  - Auto-Connect
  - Dark Mode
  - Kill Switch toggle
  - Exit Location
  - Connection mode + Hybrid
- Logs / About / Settings overlay panels

---

## Modes explained

### 1) Proxy Mode (Recommended)
- Starts Tor locally.
- Sets Windows proxy to `socks=127.0.0.1:9050`.
- Best compatibility; no admin required.

### 2) TUN/VPN Mode (Admin)
- Starts Tor + sing-box + Wintun.
- Can route traffic at OS level.
- Requires Administrator.

### Hybrid (browser via Tor)
- Only applies in **TUN/VPN Mode**.
- Routes common browsers (Edge/Chrome/Firefox) through Tor.
- Other traffic goes direct.

---

## Kill Switch

The Kill Switch is intentionally conservative:

- Only available in **TUN/VPN Mode** with **Hybrid disabled (strict)**.
- Requires **Administrator** to apply/clear firewall rules.
- If Tor/sing-box exits unexpectedly while connected in strict TUN, OnionHop adds an **Outbound Block** firewall rule.

If you ever lose internet after a crash:

1. Relaunch OnionHop **as Administrator**.
2. Click **Disconnect**.

---

## Settings storage

OnionHop stores settings here:

- `%AppData%\OnionHop\settings.json`

---

## Build & run (Developer)

### Requirements

- Windows 10/11
- .NET SDK **9.0** (project targets `net9.0-windows`)

### Build

```powershell
# from OnionHop/OnionHop
 dotnet build -c Release
```

### Publish

```powershell
# from OnionHop/OnionHop
 dotnet publish -c Release -r win-x64 --self-contained false
```

Output:

- `OnionHop/OnionHop/bin/Release/net9.0-windows/win-x64/publish/`

> If publish fails with "file is being used by another process", close any running `OnionHop.exe`.

---

## Create an installer (Setup.exe)

OnionHop can be packaged into a shareable Windows installer using **Inno Setup 6**.

### Requirements

- Install **Inno Setup 6** (adds `ISCC.exe`).

### Build

From the repo root:

```powershell
# Build a self-contained installer (recommended for sharing)
./installer/build-installer.ps1 -SelfContained
```

The installer will be created here:

- `installer/output/OnionHop-Setup-<version>.exe`

If you prefer framework-dependent (requires .NET runtime on the target machine):

```powershell
./installer/build-installer.ps1
```

---

## Bundled dependencies

This repo includes runtime binaries under:

- `OnionHop/OnionHop/tor/`
- `OnionHop/OnionHop/vpn/`

These are copied to output/publish via `OnionHop.csproj`.

---

## Privacy / Logging

- No telemetry is sent.
- Logs are local-only (app UI "Logs" panel).
- Proxy mode edits Windows proxy settings; TUN mode may add/remove Windows Firewall rules when Kill Switch is active.

---

## Troubleshooting

### Proxy mode doesn't affect some apps
Many apps ignore the Windows proxy settings. Use **TUN/VPN Mode** if you need system-wide routing.

### TUN/VPN mode fails to start
- Run OnionHop **as Administrator**.
- Ensure `vpn/wintun.dll` and `vpn/sing-box.exe` exist.

### Internet blocked after crash
This usually means the kill switch firewall rule is still present.
- Relaunch as **Administrator**
- Click **Disconnect**

### Tor bootstrap is slow
Some networks block or throttle Tor.
- Try a different Exit Location.
- Consider using Tor bridges (not currently implemented in OnionHop).

---

## Security notes

- OnionHop modifies **Windows proxy settings** in Proxy Mode.
- OnionHop may modify **Windows Firewall rules** when Kill Switch is enabled.
- Avoid running unknown binaries with elevated privileges.

---

## Roadmap / Ideas

- Separate services (TorService/VpnService/SettingsService) for cleaner architecture
- Optional Tor bridges / pluggable transports
- Better kill switch: allow-only rules (Tor + tunnel) instead of emergency global block
- More diagnostics and structured logging

---

## License

GPLv3. See `LICENSE`.

---

## Support / Issues

For support, please either:
- Open an issue on this repository
- Join the Discord server: https://discord.gg/sNsJzKBNUG
