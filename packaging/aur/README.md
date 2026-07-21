# AUR packaging templates

Community packaging for the [Arch User Repository](https://aur.archlinux.org/) (Arch Linux,
Manjaro, EndeavourOS, ...). Requested in [#75](https://github.com/center2055/OnionHop/issues/75).

These are **starting templates**. They were written and reviewed but **not built with `makepkg`
on an Arch system**, so validate before uploading.

## Two variants

| Package | How | Best for |
| :--- | :--- | :--- |
| [`onionhop-bin`](onionhop-bin/PKGBUILD) | Repackages the released `OnionHop-x86_64.AppImage` | Everyone. No build toolchain, no `download-deps.sh`, trivial to keep current. Recommended. |
| [`onionhop-git`](onionhop-git/PKGBUILD) | Builds from `master` with `dotnet publish` + `download-deps.sh` | Users who want a from-source build. Needs `dotnet-sdk-9.0` + `go` + `rust` at build time. |

Only one may be installed at a time (they `provides=('onionhop')` and `conflicts` with each other).

## Per-release upkeep

- `onionhop-bin`: bump `pkgver` and replace `sha256sums` with the value from the release's
  `OnionHop-x86_64.AppImage.sha256.txt`, then `makepkg --printsrcinfo > .SRCINFO` and push.
- `onionhop-git`: builds `master`; `pkgver()` derives the version from git automatically.

## Publishing to the AUR (manual, needs an AUR account)

The AUR is a set of git repos; publishing is not something the app repo can automate.

1. Create an account at https://aur.archlinux.org and add your SSH public key.
2. Test locally on Arch: `makepkg -si` (fix anything that surfaces).
3. Generate metadata: `makepkg --printsrcinfo > .SRCINFO`.
4. Push to the AUR:
   ```sh
   git clone ssh://aur@aur.archlinux.org/onionhop-bin.git
   cp PKGBUILD .SRCINFO onionhop-bin/ && cd onionhop-bin
   git add PKGBUILD .SRCINFO && git commit -m "Initial import: onionhop-bin 3.7.5" && git push
   ```

Validation notes:
- `onionhop-git`: confirm the bundled native deps (tor, pluggable transports, sing-box, ...) end
  up next to the app after `dotnet publish`. See `.github/workflows/linux-appimage.yml` for the
  exact layout the AppImage build produces.
- TUN/VPN mode needs elevated privileges (the app requests them via `pkexec`/`sudo` at runtime);
  no special packaging is required for that, but it is why a sandboxed Flatpak is a separate, harder
  effort (see the #75 discussion).
