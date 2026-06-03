#!/bin/bash
set -e

REPO_ROOT="$(pwd)"
TOR_DIR="$REPO_ROOT/OnionHop/tor"
VPN_DIR="$REPO_ROOT/OnionHop/vpn"
ARTI_HOP_DIR="$REPO_ROOT/OnionHop/artihop"
SNOWFLAKE_DIR="$REPO_ROOT/OnionHop/snowflake"
PT_DIR="$TOR_DIR/pluggable_transports"
TEMP_DIR="$REPO_ROOT/temp_deps_linux"

# Versions
TOR_VERSION="15.0.14"
WEBTUNNEL_VERSION="v0.0.3"
SING_BOX_URL="https://github.com/SagerNet/sing-box/releases/download/v1.13.12/sing-box-1.13.12-linux-amd64.tar.gz"
XRAY_URL="https://github.com/XTLS/Xray-core/releases/download/v26.3.27/Xray-linux-64.zip"
ARTI_HOP_REPO_URL="https://github.com/center2055/ArtiHop.git"
SNOWFLAKE_PROXY_PACKAGE="gitlab.torproject.org/tpo/anti-censorship/pluggable-transports/snowflake/v2/proxy@latest"

mkdir -p "$TOR_DIR" "$VPN_DIR" "$ARTI_HOP_DIR" "$SNOWFLAKE_DIR" "$PT_DIR" "$TEMP_DIR"

echo "=== OnionHop Linux Dependency Downloader ==="

# 1. Tor Expert Bundle
# dist.torproject.org only serves the *current* releases, so an older pinned version gets rotated off
# it and the download silently returns a tiny error page (-> "gzip: stdin: not in gzip format"). The
# archive host keeps every version forever, so fall back to it. -f makes curl fail on HTTP errors
# instead of saving the error page, and gzip -t verifies we actually got a real tarball.
echo "Downloading Tor Expert Bundle ($TOR_VERSION)..."
TOR_ARCHIVE="$TEMP_DIR/tor.tar.gz"
TOR_REL="$TOR_VERSION/tor-expert-bundle-linux-x86_64-$TOR_VERSION.tar.gz"
curl -fL "https://dist.torproject.org/torbrowser/$TOR_REL" -o "$TOR_ARCHIVE" || true
if ! gzip -t "$TOR_ARCHIVE" 2>/dev/null; then
    echo "  dist.torproject.org did not serve $TOR_VERSION; falling back to archive.torproject.org..."
    curl -fL "https://archive.torproject.org/tor-package-archive/torbrowser/$TOR_REL" -o "$TOR_ARCHIVE"
fi
tar -xzf "$TOR_ARCHIVE" -C "$TEMP_DIR"

cp "$TEMP_DIR/tor/tor" "$TOR_DIR/"
cp "$TEMP_DIR/tor/tor-gencert" "$TOR_DIR/" 2>/dev/null || true
# geoip/geoip6 live under data/ in the Linux Tor expert bundle (verified on Linux).
cp "$TEMP_DIR/data/geoip" "$TOR_DIR/"
cp "$TEMP_DIR/data/geoip6" "$TOR_DIR/"

# Bundle the shared libraries the expert-bundle tor is built against (libssl/libcrypto/libevent).
# Without these, tor falls back to the system libs and can fail with errors like
# "undefined symbol: evutil_secure_rng_add_bytes" when the host libevent is older/incompatible.
# tor has an rpath of $ORIGIN, so libraries next to the binary are found automatically.
cp "$TEMP_DIR/tor/"*.so* "$TOR_DIR/" 2>/dev/null || true

echo "Installing Pluggable Transports..."
cp -r "$TEMP_DIR/tor/pluggable_transports/"* "$PT_DIR/"

# Handle renamed binaries
if [ ! -f "$PT_DIR/lyrebird" ] && [ -f "$PT_DIR/obfs4proxy" ]; then
    mv "$PT_DIR/obfs4proxy" "$PT_DIR/lyrebird"
fi

# 2. Webtunnel client
if command -v go >/dev/null 2>&1; then
    echo "Building webtunnel-client from source ($WEBTUNNEL_VERSION)..."
    WEBTUNNEL_ARCHIVE="$TEMP_DIR/webtunnel.tar.gz"
    curl -L "https://gitlab.torproject.org/tpo/anti-censorship/pluggable-transports/webtunnel/-/archive/$WEBTUNNEL_VERSION/webtunnel-$WEBTUNNEL_VERSION.tar.gz" -o "$WEBTUNNEL_ARCHIVE"
    tar -xzf "$WEBTUNNEL_ARCHIVE" -C "$TEMP_DIR"
    WEBTUNNEL_SRC_DIR=$(find "$TEMP_DIR" -maxdepth 1 -type d -name "webtunnel-*")
    pushd "$WEBTUNNEL_SRC_DIR"
    CGO_ENABLED=0 go build -ldflags "-s -w" -o "$PT_DIR/webtunnel-client" "./main/client"
    popd
else
    echo "Go not found, skipping webtunnel-client build."
fi

# 3. Dnstt client
if command -v go >/dev/null 2>&1; then
    echo "Building dnstt-client from source..."
    DNSTT_CLONE="$TEMP_DIR/dnstt"
    git clone https://www.bamsoftware.com/git/dnstt.git "$DNSTT_CLONE"
    pushd "$DNSTT_CLONE/dnstt-client"
    CGO_ENABLED=0 go build -ldflags "-s -w" -o "$PT_DIR/dnstt-client" "."
    popd
else
    echo "Go not found, skipping dnstt-client build."
fi

# 4. Sing-box
echo "Downloading Sing-box..."
SB_ARCHIVE="$TEMP_DIR/sing-box.tar.gz"
curl -L "$SING_BOX_URL" -o "$SB_ARCHIVE"
tar -xzf "$SB_ARCHIVE" -C "$TEMP_DIR"
SB_SRC_DIR=$(find "$TEMP_DIR" -maxdepth 1 -type d -name "sing-box-*")
cp "$SB_SRC_DIR/sing-box" "$VPN_DIR/"

# 5. Xray
echo "Downloading Xray..."
XRAY_ARCHIVE="$TEMP_DIR/xray.zip"
curl -L "$XRAY_URL" -o "$XRAY_ARCHIVE"
unzip -q -o "$XRAY_ARCHIVE" -d "$TEMP_DIR/xray_ext"
cp "$TEMP_DIR/xray_ext/xray" "$VPN_DIR/"

# 6. ArtiHop
if command -v cargo >/dev/null 2>&1; then
    echo "Building ArtiHop engine..."
    ARTI_HOP_CLONE="$TEMP_DIR/ArtiHop"
    git clone --depth 1 "$ARTI_HOP_REPO_URL" "$ARTI_HOP_CLONE"
    pushd "$ARTI_HOP_CLONE"
    cargo build --release
    cp "target/release/artihop" "$ARTI_HOP_DIR/"
    popd
else
    echo "Cargo not found, skipping ArtiHop build."
fi

# 7. Snowflake proxy
if command -v go >/dev/null 2>&1; then
    echo "Building Snowflake volunteer proxy..."
    GOBIN="$TEMP_DIR/gobin"
    mkdir -p "$GOBIN"
    CGO_ENABLED=0 GOBIN="$GOBIN" go install "$SNOWFLAKE_PROXY_PACKAGE"
    cp "$GOBIN/proxy" "$SNOWFLAKE_DIR/snowflake-proxy"
else
    echo "Go not found, skipping Snowflake proxy build."
fi

# Update pt_config.json
PT_CONFIG="$PT_DIR/pt_config.json"
if [ -f "$PT_CONFIG" ]; then
    echo "Updating pt_config.json for Linux..."
    # Replace .exe with empty string for Linux binaries in pt_config.json
    sed -i 's/\.exe//g' "$PT_CONFIG"
fi

# Set executable permissions
chmod +x "$TOR_DIR/tor" "$TOR_DIR/tor-gencert" 2>/dev/null || true
chmod +x "$PT_DIR/"*
chmod +x "$VPN_DIR/sing-box" "$VPN_DIR/xray"
[ -f "$ARTI_HOP_DIR/artihop" ] && chmod +x "$ARTI_HOP_DIR/artihop"
[ -f "$SNOWFLAKE_DIR/snowflake-proxy" ] && chmod +x "$SNOWFLAKE_DIR/snowflake-proxy"

# Cleanup temp downloads (mirrors the Windows script).
rm -rf "$TEMP_DIR"

echo "Done! Linux binaries updated."
