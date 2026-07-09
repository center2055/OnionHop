# Changelog

## v3.7 (2026-07-09)

Fixes
- Fixed Snowflake failing to start ("Managed proxy ... terminated with status code 2", connection bar stuck) when the AMP-cache rendezvous was enabled (#71). The AMP cache was passed as a `-ampcache` command-line flag to the transport binary, but lyrebird (the bundled Snowflake) rejects that flag and exits immediately. It is now set as an `ampcache=` parameter on the Snowflake bridge line, which lyrebird reads. This mainly affected heavily-censored networks (e.g. Iran) where Smart Connect forces Snowflake AMP.

Additions
- The SNI scanner now matches the bridge scanner's compact layout: a "Custom list" toggle keeps the candidate-domain box collapsed until you turn it on or load/import/request a list, so the controls take less space.
- SNI scanner: added an "Export Working" button to save the working SNI hosts to a text file, and a "Request SNI" button that fetches a per-country SNI candidate list from the OnionHop SNI-lists source (working SNIs are country-specific) and loads it for scanning. A country picker appears when the source is reachable.
- About page: each release in the changelog history now has a "View on GitHub" link that opens that release's page in your browser, and the sources section now lists the new OnionHop SNI Lists source (the per-country lists behind Request SNI).

## v3.6.3 (2026-07-08)

Additions
- The SNI scanner now has a "Load candidates" button that fills the box with a built-in starter list of common CDN/front domains, so you have something to scan without needing to know which domains to try (the SNI equivalent of the bridge scanner's "Load bridges"). You can still edit the list, import your own, or paste more.

## v3.6.2 (2026-07-08)

Additions
- The bridge scanner and SNI scanner gained an "Import file" button to load bridge lines / candidate domains from a text file, and the bridge scanner gained a "Load bridges" button that fetches the selected list into the input without scanning (so you can review or edit it first, then scan).
- The saved-bridges library now shows each entry's ping as a green (or amber, when slow) "✔ ms" badge instead of a plain number.

## v3.6.1 (2026-07-05)

Additions
- The saved-bridges library now shows the ping (latency in ms) of each saved entry, carried over from the scan that saved it.

Fixes
- Fixed the SNI scanner never finding any working host. Every probe failed with an internal ".NET SslStream" error because the certificate-validation callback was set in two places at once, so results always came back "blocked" and the scanner appeared to do nothing. It now completes handshakes and reports reachable SNI hosts correctly.
- The SNI scanner's status messages ("Ready.", "Scanning…", "Enter at least one domain to test.", etc.) are now localized instead of always showing in English.

## v3.6 (2026-07-05)

Additions
- Added an SNI scanner as a subtab of the Scanner page. It finds SNI/front hosts that work on your network via TLS handshakes: Domain mode tests a list of candidate domains as SNI on :443, and Range mode tests a single SNI across an IPv4 CIDR range (bounded). Working hosts can be applied as the app's custom SNI hosts (used by fronted webtunnel/meek/snowflake bridges) or saved to the library.
- Added a saved-bridges library as a subtab of the Scanner page. Bridges found by the bridge scanner and SNI hosts found by the SNI scanner can be saved to a persistent library (JSON in app data), then labelled, re-applied or removed later - previously scan results were lost as soon as the scan finished. The bridge scanner and SNI scanner both gained a "Save to Library" action.
- The Current Bridge tab gained a one-tap copy button on each row (copies just that bridge line) and sortable columns - click Type, Address or Status to sort, click again to reverse; the bridge in use always stays pinned to the top (#69).
- The Current Bridge tab's Copy and Export now focus on the bridge that matters (#56). Copy returns only the bridge(s) Tor is actually using (falling back to bridges seen in use this session, then to all when no live status is available), so you get the working bridge to carry to another device instead of the whole supplemented list. Export writes a CSV with Type, Address, Status, Fingerprint and the raw bridge line.
- Added `scan <type> [count] --use` to the CLI: after probing, the reachable bridges are saved as your custom list and selected as the bridge source, so the next `connect` uses exactly those - the command-line equivalent of the GUI scanner's Apply button. The CLI now also honors saved custom bridges and a saved bridge source on connect.

Fixes
- Fixed the Custom bridge source being ignored when Smart Connect was on (#70). Smart Connect (enabled by default) reset the bridge source to Auto and dropped the custom bridge list while racing its own transports, so a user who selected the Custom source and pasted, say, an obfs4 bridge could end up connected to a webtunnel bridge they never added. Smart Connect now honors an explicit Custom source and uses exactly those bridges.

## v3.5.1 (2026-07-03)

Additions
- Added a "Custom list" bridge source to the Home dropdown (#70). Saved custom bridge lines now apply only when this source is selected; previously a filled custom-bridges box silently overrode whichever source was chosen. Existing setups with saved custom bridges are migrated to the new source once, so nothing changes for them.
- The Current Bridge tab now shows which bridge Tor is actually connected to: a live "In use" badge per bridge (classic tor engine, via the control port), and the last-seen time once a bridge is no longer in use (#69).
- Geo rule-sets (country and domain-category routing) are now cached on disk and refreshed through Tor after connecting (#68). The TUN start references cached lists as local files, so once fetched they work with no dependency on raw.githubusercontent.com - which is blocked exactly where these rules matter most. A rule-set that cannot be verified or downloaded is skipped for that connection instead of breaking it, and applies automatically on a later connect.

## v3.5 (2026-07-02)

Additions
- Added a macOS CLI: CI now builds self-contained OnionHopCLI tarballs for Apple Silicon (arm64) and Intel (x64) Macs with a universal native runtime (tor, pluggable transports incl. webtunnel/dnstt, sing-box, xray, ArtiHop, snowflake proxy), matching the Linux CLI packaging.

Fixes
- Fixed the desktop window opening larger than the visible screen on Windows displays scaled above 100 percent, which pushed the title-bar buttons off screen with no way to move the window (#67). The window now clamps its size and minimum size to the current screen's work area and re-centers to stay fully reachable at any DPI.
- Fixed Smart Connect aborting the whole connect as "Canceled" when a network probe merely timed out (#65). HTTP timeouts surface as cancellation exceptions in .NET; those are now treated as probe failures that fall back to the generic connection plan, so connect works on networks where the geolocation/OONI endpoints hang or are blocked.
- Fixed a mistyped or unpublished geosite category (e.g. `ir`) making the sing-box start FATAL and taking down the whole TUN connection (#68). Remote geo rule-sets are now verified before the config is built: plain-name misses are automatically upgraded to SagerNet's `category-` variant when that exists (`ir` -> `category-ir`), and entries that cannot be found upstream are skipped with a warning in the log instead of breaking the start. Unknown country codes in country routing are handled the same way.
- Fixed the Current Bridge tab in Logs squeezing bridge lines through the log-line parser, which sliced the transport name into the Time column (#69). The tab now has its own Type, Address and Details columns, and severity filters (which do not apply to bridges) are hidden there.

## v3.4.5 (2026-06-30)

Additions
- Added country routing for TUN/VPN mode: keep whole countries direct (bypass Tor) or block them by IP, using auto-updating sing-box geoip rule-sets (#55).
- Added domain-category routing for TUN/VPN mode: keep or block whole categories of domains (e.g. `category-ads-all`) using auto-updating sing-box geosite rule-sets (#55).

Fixes
- Fixed Conjure bridges by pointing the transport at `conjure-client` (with its registration URL) instead of `lyrebird`, which does not speak Conjure (#64).
- Added an IPv6 kill switch (ip6tables) so IPv6 traffic is blocked alongside IPv4 in Proxy Mode.
- Fixed Windows admin-helper status probing so persistent-helper connection validation cannot deadlock or hang indefinitely.
- Fixed cancellation handling in dependency and bridge fetch paths.
- Fixed custom vanilla bridge lines so they are not rewritten as a fake `custom` transport.
- Fixed Arti/ArtiHop bridge transport arguments so Conjure and Snowflake launch with their required PT flags.
- Fixed Linux/Windows DNS and Linux kill-switch setup to report failure when system rules are not actually installed.
- Fixed relay, logs, bridge scanner, and custom DoH UI states.

Packaging
- CLI publish now includes optional ArtiHop and Snowflake runtime folders.
- Release workflows run tests before packaging and upload checksum files with release assets.

## v2.4.4 (2026-03-03)

Additions
- Added startup update checks when `Check for updates` is enabled.
- Added a Home header update badge with direct link to the latest OnionHop release page.
- Added EN/DE localization keys for update badge text and tooltip.

Fixes
- Fixed settings autosave threading (`Call from invalid thread`) by marshalling debounced saves back to Avalonia UI thread.
- Improved update detection state handling so turning update checks off clears pending badge state immediately.

Packaging
- Bumped app/CLI/installer versioning to 2.4.4.
- Release assets include GUI and CLI installers plus both portable bundles:
- OnionHop-Setup-2.4.4.exe
- OnionHop-CLI-Setup-2.4.4.exe
- OnionHopV2-Portable-2.4.4-win-x64.zip
- OnionHopCLI-Portable-2.4.4-win-x64.zip

## v2.4.3 (2026-02-23)

Additions
- Added new TUN Engine section in Advanced settings (issue #31) with stack selection, optional MTU, and strict-route control.
- Added localization keys (EN/DE) for the new TUN controls and stack labels.
- Kept the Home card focused on routing controls only for a cleaner default layout.

Fixes
- Reworked HTTP proxy handling for LAN/local clients (issue #28): OnionHop now runs an internal HTTP proxy bridge over Tor SOCKS for better client compatibility.
- HTTP proxy startup now logs explicit status and falls back to SOCKS-only mode if HTTP bridge startup fails.
- Extended settings/connect-option persistence and VPN config generation to include TUN stack/MTU/strict-route options.
- Added test coverage for TUN option parsing/serialization and VPN config output.

Packaging
- Bumped app/CLI/installer versioning to 2.4.3.
- Release assets should include GUI + CLI installers and both portable bundles:
- OnionHop-Setup-2.4.3.exe
- OnionHop-CLI-Setup-2.4.3.exe
- OnionHopV2-Portable-2.4.3-win-x64.zip
- OnionHopCLI-Portable-2.4.3-win-x64.zip

## v2.4.2 (2026-02-23)

Additions
- Added optional LAN proxy access for Tor SOCKS/HTTP listeners (advanced setting, disabled by default).
- Added configurable connection timeout (advanced setting): auto by default, custom seconds, or disable with `0`.
- Added grouping and collapsible sections in Advanced settings for better readability.
- Added localization keys for new advanced controls and manual exit fingerprint labels.

Fixes
- Improved local/manual proxy hint messaging when LAN bind mode is enabled.
- Wired Tor launch arguments to support explicit SOCKS/HTTP bind addresses.
- Updated settings persistence and connect-option plumbing for LAN access + timeout controls.
- Added/updated tests for timeout resolution and settings round-trip coverage.

Packaging
- Bumped app/CLI/installer versioning to 2.4.2.
- Release assets include GUI and CLI installers plus portable bundles:
- OnionHop-Setup-2.4.2.exe
- OnionHop-CLI-Setup-2.4.2.exe
- OnionHopV2-Portable-2.4.2-win-x64.zip
- OnionHopCLI-Portable-2.4.2-win-x64.zip

## v2.4.1 (2026-02-21)

Additions
- Added Update BridgeDB on Home to manually refresh bridge data after Tor is connected.
- Added BridgeDB last-update timestamp display on Home.
- Added Clear button in Logs with tab-aware behavior (App logs and DNS logs).

Fixes
- Improved BridgeDB refresh flow to force a fresh fetch (not stale runtime cache), with Tor-SOCKS routing when connected.
- Improved Home action layout so secondary controls render correctly and the BridgeDB action has dedicated space.

Packaging
- Bumped app/CLI/installer versioning to 2.4.1.
- Release assets include both GUI and CLI outputs so website latest-file lookup continues to work:
- OnionHop-Setup-2.4.1.exe
- OnionHop-CLI-Setup-2.4.1.exe
- OnionHopV2-Portable-2.4.1-win-x64.zip
- OnionHopCLI-Portable-2.4.1-win-x64.zip

## v2.4 (2026-02-18)

Additions
- Added Smart Connect (enabled by default) in the Home view for one-click Tor setup.
- Added country-aware Smart Connect planning that blends OONI Tor stats, recent OONI measurements, and optional CSV baselines.
- Added automatic Smart Connect fallback sequencing (direct/bridge strategies) with retry-on-failure behavior.
- Added OnionHop CLI (`OnionHopV2.Cli`) with interactive mode and command mode (`connect`, `disconnect`, `status`, `ip`, `newnym`, `plan`, `deps`).
- Added CLI country-selection support via `--exit` / `--entry` and a `countries` command to list available country codes.
- Added SOCKS-only system proxy scope for better browser and `.onion` compatibility.
- Added connection elapsed timer display on the Home status card.
- Added CLI installer with PATH integration and `onionhop` terminal launcher.
- Added CLI portable packaging script (`installer/build-portable-cli.ps1`).
- Added test coverage for `SmartConnectAdvisor`, `SingBoxLogProcessor`, and `TorLogHelper`.

Fixes
- Fixed connection flow to allow Smart Connect to safely override bridge/censorship settings per strategy when needed.
- Fixed elevated-requirement handling so `.onion` DNS proxy no longer hard-fails Smart Connect attempts when admin rights are unavailable.
- Fixed proxy behavior for SOCKS-only system mode by avoiding forced HTTP proxy assignment.
- Fixed CLI `status` to refresh missing direct IP when disconnected, and improved status event output when only IP/port fields change.
- Fixed CLI `ip` / `newnym` user feedback so commands always report a visible outcome (including unchanged IP or NEWNYM cooldown message).
- Fixed IP refresh behavior while connected to Tor so failed Tor-exit lookups no longer silently fall back to direct-IP reporting.
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
