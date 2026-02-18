# Changelog

## v2.4 (2026-02-18)

Additions
- Added Smart Connect (enabled by default) in the Home view for one-click Tor setup.
- Added country-aware Smart Connect planning that blends OONI Tor stats, recent OONI measurements, and optional CSV baselines.
- Added automatic Smart Connect fallback sequencing (direct/bridge strategies) with retry-on-failure behavior.
- Added OnionHop CLI (`OnionHopV2.Cli`) with interactive mode and command mode (`connect`, `disconnect`, `status`, `ip`, `newnym`, `plan`, `deps`).
- Added SOCKS-only system proxy scope for better browser and `.onion` compatibility.
- Added connection elapsed timer display on the Home status card.
- Added CLI installer with PATH integration and `onionhop` terminal launcher.
- Added CLI portable packaging script (`installer/build-portable-cli.ps1`).
- Added test coverage for `SmartConnectAdvisor`, `SingBoxLogProcessor`, and `TorLogHelper`.

Fixes
- Fixed connection flow to allow Smart Connect to safely override bridge/censorship settings per strategy when needed.
- Fixed elevated-requirement handling so `.onion` DNS proxy no longer hard-fails Smart Connect attempts when admin rights are unavailable.
- Fixed proxy behavior for SOCKS-only system mode by avoiding forced HTTP proxy assignment.
- Improved runtime diagnostics by extracting Tor and sing-box log processing into dedicated helpers and preserving recent status lines.
- Improved cleanup diagnostics with explicit disposal error logging paths.

Packaging
- Bumped app/CLI/installer versioning to `2.4.0`.
- Included both installers and both portable packages in the release asset set:
- `OnionHop-Setup-2.4.0.exe`
- `OnionHop-CLI-Setup-2.4.0.exe`
- `OnionHopV2-Portable-2.4.0-win-x64.zip`
- `OnionHopCLI-Portable-2.4.0-win-x64.zip`

Notes
- Sorry again for the previous release missing full installer/portable coverage.
- Website/README content was improved with clearer CLI and packaging instructions.
