#!/bin/bash
# Builds/downloads the macOS native runtime (tor, pluggable transports, sing-box, xray,
# ArtiHop, snowflake proxy) as UNIVERSAL (arm64 + x86_64) binaries, staged into the same
# OnionHop/{tor,vpn,artihop,snowflake} folders the dotnet publish glob copies from.
# Mirrors download-deps.sh (Linux); must run on macOS (uses lipo + BSD sed).
set -e

REPO_ROOT="$(pwd)"
TOR_DIR="$REPO_ROOT/OnionHop/tor"
VPN_DIR="$REPO_ROOT/OnionHop/vpn"
ARTI_HOP_DIR="$REPO_ROOT/OnionHop/artihop"
SNOWFLAKE_DIR="$REPO_ROOT/OnionHop/snowflake"
PT_DIR="$TOR_DIR/pluggable_transports"
TEMP_DIR="$REPO_ROOT/temp_deps_macos"

# Versions (keep in sync with download-deps.sh)
TOR_VERSION="15.0.14"
WEBTUNNEL_VERSION="v0.0.3"
SING_BOX_VERSION="1.13.12"
XRAY_VERSION="v26.3.27"
ARTI_HOP_REPO_URL="https://github.com/center2055/ArtiHop.git"
SNOWFLAKE_REPO_URL="https://gitlab.torproject.org/tpo/anti-censorship/pluggable-transports/snowflake.git"

mkdir -p "$TOR_DIR" "$VPN_DIR" "$ARTI_HOP_DIR" "$SNOWFLAKE_DIR" "$PT_DIR" "$TEMP_DIR"

echo "=== OnionHop macOS Dependency Downloader (universal arm64 + x86_64) ==="

# Merge an x86_64 and an arm64 copy of a binary into a universal one at $3.
# Falls back to whichever copy exists if the other is missing.
make_universal() {
    local x64="$1" arm="$2" dest="$3"
    if [ -f "$x64" ] && [ -f "$arm" ]; then
        lipo -create "$x64" "$arm" -output "$dest"
    elif [ -f "$arm" ]; then
        cp "$arm" "$dest"
    elif [ -f "$x64" ]; then
        cp "$x64" "$dest"
    else
        echo "  WARNING: neither $x64 nor $arm exists; skipping $(basename "$dest")"
        return 1
    fi
}

# Build a Go package for both darwin architectures and lipo the results together.
go_build_universal() {
    local name="$1" srcdir="$2" pkg="$3" dest="$4"
    ( cd "$srcdir" && CGO_ENABLED=0 GOOS=darwin GOARCH=arm64 go build -trimpath -ldflags "-s -w" -o "$TEMP_DIR/${name}-arm64" "$pkg" )
    ( cd "$srcdir" && CGO_ENABLED=0 GOOS=darwin GOARCH=amd64 go build -trimpath -ldflags "-s -w" -o "$TEMP_DIR/${name}-amd64" "$pkg" )
    lipo -create "$TEMP_DIR/${name}-arm64" "$TEMP_DIR/${name}-amd64" -output "$dest"
    chmod +x "$dest"
    lipo -archs "$dest"
}

# 1. Tor Expert Bundle (both architectures).
# dist.torproject.org only serves the *current* releases, so an older pinned version gets rotated
# off it and the download silently returns a tiny error page. The archive host keeps every version
# forever, so fall back to it. gzip -t verifies we actually got a real tarball.
fetch_tor_bundle() {
    local arch="$1" dest_dir="$2"
    local rel="$TOR_VERSION/tor-expert-bundle-macos-${arch}-$TOR_VERSION.tar.gz"
    local archive="$TEMP_DIR/tor-${arch}.tar.gz"
    echo "Downloading Tor Expert Bundle ($TOR_VERSION, macos-${arch})..."
    curl -fL "https://dist.torproject.org/torbrowser/$rel" -o "$archive" || true
    if ! gzip -t "$archive" 2>/dev/null; then
        echo "  dist.torproject.org did not serve $TOR_VERSION; falling back to archive.torproject.org..."
        curl -fL "https://archive.torproject.org/tor-package-archive/torbrowser/$rel" -o "$archive"
    fi
    mkdir -p "$dest_dir"
    tar -xzf "$archive" -C "$dest_dir"
}

X64_BUNDLE="$TEMP_DIR/bundle-x64"
ARM_BUNDLE="$TEMP_DIR/bundle-arm64"
fetch_tor_bundle "x86_64" "$X64_BUNDLE"
fetch_tor_bundle "aarch64" "$ARM_BUNDLE"

echo "Installing universal tor..."
make_universal "$X64_BUNDLE/tor/tor" "$ARM_BUNDLE/tor/tor" "$TOR_DIR/tor"
make_universal "$X64_BUNDLE/tor/tor-gencert" "$ARM_BUNDLE/tor/tor-gencert" "$TOR_DIR/tor-gencert" || true

# Bundle the dylibs tor links against (libevent etc.); tor's rpath finds them next to the binary.
for lib in "$ARM_BUNDLE/tor/"*.dylib; do
    [ -f "$lib" ] || continue
    base="$(basename "$lib")"
    make_universal "$X64_BUNDLE/tor/$base" "$lib" "$TOR_DIR/$base" || true
done

# geoip/geoip6 live under data/ in the expert bundle (identical content in both arch bundles).
cp "$ARM_BUNDLE/data/geoip" "$TOR_DIR/"
cp "$ARM_BUNDLE/data/geoip6" "$TOR_DIR/"

echo "Installing Pluggable Transports (universal)..."
for f in "$ARM_BUNDLE/tor/pluggable_transports/"*; do
    base="$(basename "$f")"
    if file "$f" | grep -q "Mach-O"; then
        make_universal "$X64_BUNDLE/tor/pluggable_transports/$base" "$f" "$PT_DIR/$base" || true
    else
        cp "$f" "$PT_DIR/$base"
    fi
done

# Handle renamed binaries (older bundles shipped obfs4proxy instead of lyrebird).
if [ ! -f "$PT_DIR/lyrebird" ] && [ -f "$PT_DIR/obfs4proxy" ]; then
    mv "$PT_DIR/obfs4proxy" "$PT_DIR/lyrebird"
fi

# 2. Webtunnel client (not part of the expert bundle; standalone Go program).
if command -v go >/dev/null 2>&1; then
    echo "Building webtunnel-client from source ($WEBTUNNEL_VERSION, universal)..."
    WEBTUNNEL_ARCHIVE="$TEMP_DIR/webtunnel.tar.gz"
    curl -fL "https://gitlab.torproject.org/tpo/anti-censorship/pluggable-transports/webtunnel/-/archive/$WEBTUNNEL_VERSION/webtunnel-$WEBTUNNEL_VERSION.tar.gz" -o "$WEBTUNNEL_ARCHIVE"
    tar -xzf "$WEBTUNNEL_ARCHIVE" -C "$TEMP_DIR"
    WEBTUNNEL_SRC_DIR=$(find "$TEMP_DIR" -maxdepth 1 -type d -name "webtunnel-*")
    go_build_universal "webtunnel-client" "$WEBTUNNEL_SRC_DIR" "./main/client" "$PT_DIR/webtunnel-client"
else
    echo "Go not found, skipping webtunnel-client build."
fi

# 3. Dnstt client (full clone - its dumb-HTTP git server rejects shallow fetches).
if command -v go >/dev/null 2>&1; then
    echo "Building dnstt-client from source (universal)..."
    DNSTT_CLONE="$TEMP_DIR/dnstt"
    git clone https://www.bamsoftware.com/git/dnstt.git "$DNSTT_CLONE"
    go_build_universal "dnstt-client" "$DNSTT_CLONE/dnstt-client" "." "$PT_DIR/dnstt-client"
else
    echo "Go not found, skipping dnstt-client build."
fi

# 4. Sing-box (SagerNet publishes separate darwin builds; lipo them together).
echo "Downloading Sing-box ($SING_BOX_VERSION, universal)..."
for arch in amd64 arm64; do
    curl -fL "https://github.com/SagerNet/sing-box/releases/download/v${SING_BOX_VERSION}/sing-box-${SING_BOX_VERSION}-darwin-${arch}.tar.gz" -o "$TEMP_DIR/sing-box-${arch}.tar.gz"
    mkdir -p "$TEMP_DIR/sb-${arch}"
    tar -xzf "$TEMP_DIR/sing-box-${arch}.tar.gz" -C "$TEMP_DIR/sb-${arch}"
done
SB_AMD64=$(find "$TEMP_DIR/sb-amd64" -type f -name sing-box | head -1)
SB_ARM64=$(find "$TEMP_DIR/sb-arm64" -type f -name sing-box | head -1)
make_universal "$SB_AMD64" "$SB_ARM64" "$VPN_DIR/sing-box"

# 5. Xray.
echo "Downloading Xray ($XRAY_VERSION, universal)..."
curl -fL "https://github.com/XTLS/Xray-core/releases/download/$XRAY_VERSION/Xray-macos-64.zip" -o "$TEMP_DIR/xray-x64.zip"
curl -fL "https://github.com/XTLS/Xray-core/releases/download/$XRAY_VERSION/Xray-macos-arm64-v8a.zip" -o "$TEMP_DIR/xray-arm64.zip"
unzip -q -o "$TEMP_DIR/xray-x64.zip" -d "$TEMP_DIR/xray_x64"
unzip -q -o "$TEMP_DIR/xray-arm64.zip" -d "$TEMP_DIR/xray_arm64"
make_universal "$TEMP_DIR/xray_x64/xray" "$TEMP_DIR/xray_arm64/xray" "$VPN_DIR/xray"

# 6. ArtiHop (optional engine; needs a Rust toolchain). Non-fatal: the CLI runs fine without it,
# the artihop engine option is just unavailable.
if command -v cargo >/dev/null 2>&1; then
    echo "Building ArtiHop engine (universal)..."
    if (
        set -e
        ARTI_HOP_CLONE="$TEMP_DIR/ArtiHop"
        git clone --depth 1 "$ARTI_HOP_REPO_URL" "$ARTI_HOP_CLONE"
        cd "$ARTI_HOP_CLONE"
        rustup target add x86_64-apple-darwin aarch64-apple-darwin
        cargo build --release --target aarch64-apple-darwin
        cargo build --release --target x86_64-apple-darwin
        lipo -create "target/aarch64-apple-darwin/release/artihop" "target/x86_64-apple-darwin/release/artihop" -output "$ARTI_HOP_DIR/artihop"
    ); then
        echo "ArtiHop built."
    else
        echo "WARNING: ArtiHop build failed; the artihop engine will not be bundled."
    fi
else
    echo "Cargo not found, skipping ArtiHop build."
fi

# 7. Snowflake volunteer proxy (optional).
if command -v go >/dev/null 2>&1; then
    echo "Building Snowflake volunteer proxy (universal)..."
    SNOWFLAKE_CLONE="$TEMP_DIR/snowflake"
    git clone --depth 1 "$SNOWFLAKE_REPO_URL" "$SNOWFLAKE_CLONE"
    go_build_universal "snowflake-proxy" "$SNOWFLAKE_CLONE/proxy" "." "$SNOWFLAKE_DIR/snowflake-proxy"
else
    echo "Go not found, skipping Snowflake proxy build."
fi

# Update pt_config.json: the committed config references Windows binary names (.exe).
PT_CONFIG="$PT_DIR/pt_config.json"
if [ -f "$PT_CONFIG" ]; then
    echo "Updating pt_config.json for macOS..."
    sed -i '' 's/\.exe//g' "$PT_CONFIG"
fi

# Set executable permissions.
chmod +x "$TOR_DIR/tor" 2>/dev/null || true
chmod +x "$TOR_DIR/tor-gencert" 2>/dev/null || true
chmod +x "$PT_DIR/"* 2>/dev/null || true
chmod +x "$VPN_DIR/sing-box" "$VPN_DIR/xray" 2>/dev/null || true
# || true: these are optional builds, and under set -e a failed [ -f ] test would kill the script.
[ -f "$ARTI_HOP_DIR/artihop" ] && chmod +x "$ARTI_HOP_DIR/artihop" || true
[ -f "$SNOWFLAKE_DIR/snowflake-proxy" ] && chmod +x "$SNOWFLAKE_DIR/snowflake-proxy" || true

echo "=== Universal binary summary ==="
for f in "$TOR_DIR/tor" "$PT_DIR/lyrebird" "$PT_DIR/conjure-client" "$PT_DIR/webtunnel-client" "$PT_DIR/dnstt-client" "$VPN_DIR/sing-box" "$VPN_DIR/xray" "$ARTI_HOP_DIR/artihop" "$SNOWFLAKE_DIR/snowflake-proxy"; do
    [ -f "$f" ] && echo "  $(basename "$f"): $(lipo -archs "$f" 2>/dev/null || echo '?')" || true
done

# Cleanup temp downloads (mirrors the other platform scripts).
rm -rf "$TEMP_DIR"

echo "Done! macOS universal binaries staged."
