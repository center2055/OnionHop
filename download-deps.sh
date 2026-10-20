#!/bin/bash
set -e

# Usage: bash download-deps.sh [x64|arm64|arm]
#
# x64 bundles the Tor Project's expert bundle. The Tor Project publishes no Linux ARM expert bundle in
# its stable channel, so arm64 and arm (32-bit ARMv7, e.g. a Raspberry Pi on 32-bit Raspberry Pi OS)
# build Tor and the transports from source instead (#89). That works from an x86_64 host with the
# cross compiler (gcc-aarch64-linux-gnu or gcc-arm-linux-gnueabihf), or natively on the ARM machine.
# Without an argument the architecture of this machine is used.
if [ -n "$1" ]; then
    TARGET_ARCH="$1"
else
    case "$(uname -m)" in
        aarch64|arm64) TARGET_ARCH="arm64" ;;
        armv7l|armv8l|armhf) TARGET_ARCH="arm" ;;
        *) TARGET_ARCH="x64" ;;
    esac
fi

case "$TARGET_ARCH" in
    x64|amd64|x86_64)
        TARGET_ARCH="x64"; GO_ARCH="amd64"; GO_ARM=""; CROSS=""; RUST_TARGET=""
        SING_BOX_ARCH="amd64"; XRAY_ARCH="64" ;;
    arm64|aarch64)
        TARGET_ARCH="arm64"; GO_ARCH="arm64"; GO_ARM=""; CROSS="aarch64-linux-gnu"; RUST_TARGET="aarch64-unknown-linux-gnu"
        OPENSSL_TARGET="linux-aarch64"; DEB_ARCH="arm64"; SING_BOX_ARCH="arm64"; XRAY_ARCH="arm64-v8a" ;;
    arm|armv7|armhf)
        TARGET_ARCH="arm"; GO_ARCH="arm"; GO_ARM="7"; CROSS="arm-linux-gnueabihf"; RUST_TARGET="armv7-unknown-linux-gnueabihf"
        OPENSSL_TARGET="linux-armv4"; DEB_ARCH="armhf"; SING_BOX_ARCH="armv7"; XRAY_ARCH="arm32-v7a" ;;
    *)
        echo "Unknown architecture '$TARGET_ARCH'. Use x64, arm64 or arm."
        exit 1 ;;
esac

if [ -n "$CROSS" ]; then
    if ! command -v "$CROSS-gcc" >/dev/null 2>&1; then
        echo "Building for $TARGET_ARCH needs $CROSS-gcc (Debian/Ubuntu: apt install gcc-$CROSS libc6-dev-$DEB_ARCH-cross make perl)."
        exit 1
    fi
    if ! command -v go >/dev/null 2>&1; then
        echo "Building for $TARGET_ARCH needs Go: lyrebird and conjure are built from source."
        exit 1
    fi
fi

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
SING_BOX_VERSION="1.13.12"
XRAY_VERSION="v26.3.27"
SING_BOX_URL="https://github.com/SagerNet/sing-box/releases/download/v$SING_BOX_VERSION/sing-box-$SING_BOX_VERSION-linux-$SING_BOX_ARCH.tar.gz"
XRAY_URL="https://github.com/XTLS/Xray-core/releases/download/$XRAY_VERSION/Xray-linux-$XRAY_ARCH.zip"
ARTI_HOP_REPO_URL="https://github.com/center2055/ArtiHop.git"
SNOWFLAKE_PROXY_PACKAGE="gitlab.torproject.org/tpo/anti-censorship/pluggable-transports/snowflake/v2/proxy@latest"
PT_GITLAB="https://gitlab.torproject.org/tpo/anti-censorship/pluggable-transports"

# Built from source for ARM. Tor and its libraries are pinned to these hashes; lyrebird and conjure
# match what expert bundle $TOR_VERSION ships for x64.
TOR_SRC_VERSION="0.4.9.14"
TOR_SRC_SHA256="182449ac1c8ff43278da27b69b02040e11d03d0327db6d9126e4190ec6235e4a"
OPENSSL_VERSION="3.5.9"
OPENSSL_SHA256="603f5602e2eef00d77fbd429d34dcd5822bb301757a1bc9cdb24c670f1eb859a"
LIBEVENT_VERSION="2.1.12-stable"
LIBEVENT_SHA256="92e6de1be9ec176428fd2367677e61ceffc2ee1cb119035037a27d346b0403bb"
ZLIB_VERSION="1.3.2"
ZLIB_SHA256="bb329a0a2cd0274d05519d61c667c062e06990d72e125ee2dfa8de64f0119d16"
LYREBIRD_TAG="lyrebird-0.8.1"
CONJURE_COMMIT="0090962226b82aa4a8fc38506f9b98de67d0781e"

mkdir -p "$TOR_DIR" "$VPN_DIR" "$ARTI_HOP_DIR" "$SNOWFLAKE_DIR" "$PT_DIR" "$TEMP_DIR"

echo "=== OnionHop Linux Dependency Downloader ($TARGET_ARCH) ==="

# fetch_verified <url> <sha256> <file>: download, and refuse anything that does not match the hash.
fetch_verified() {
    curl -fL "$1" -o "$3"
    echo "$2  $3" | sha256sum -c -
}

# go_build <source dir> <package> <output> [extra ldflags]: a static build for the target architecture.
go_build() {
    (cd "$1" && CGO_ENABLED=0 GOOS=linux GOARCH="$GO_ARCH" GOARM="$GO_ARM" go build -trimpath -ldflags "-s -w $4" -o "$3" "$2")
}

# gitlab_source <project> <ref>: unpack a pluggable transport's source archive; prints its directory.
# Chained with && because set -e does not reach into the $(...) this runs in.
gitlab_source() {
    local dir="$TEMP_DIR/$1-src"
    mkdir -p "$dir" \
        && curl -fL "$PT_GITLAB/$1/-/archive/$2/$1-$2.tar.gz" -o "$TEMP_DIR/$1-src.tar.gz" \
        && tar -xzf "$TEMP_DIR/$1-src.tar.gz" -C "$dir" --strip-components=1 \
        && echo "$dir"
}

# Cross-compiles tor with OpenSSL, libevent and zlib linked in statically, so the binary needs nothing
# but the C library, like the expert bundle's tor.
build_tor_from_source() {
    local prefix="$TEMP_DIR/sysroot" jobs time_flags=""
    jobs="$(nproc 2>/dev/null || echo 2)"
    # 32-bit ARM: a 64-bit time_t, so dates past 2038 work (Debian 13 builds its armhf packages so).
    if [ "$TARGET_ARCH" = "arm" ]; then
        time_flags="-D_FILE_OFFSET_BITS=64 -D_TIME_BITS=64"
    fi
    mkdir -p "$prefix"

    echo "Building zlib $ZLIB_VERSION..."
    fetch_verified "https://github.com/madler/zlib/releases/download/v$ZLIB_VERSION/zlib-$ZLIB_VERSION.tar.gz" "$ZLIB_SHA256" "$TEMP_DIR/zlib.tar.gz"
    tar -xzf "$TEMP_DIR/zlib.tar.gz" -C "$TEMP_DIR"
    (cd "$TEMP_DIR/zlib-$ZLIB_VERSION" \
        && CHOST="$CROSS" CFLAGS="-O2 -fPIC $time_flags" ./configure --static --prefix="$prefix" \
        && make -j"$jobs" && make install)

    echo "Building OpenSSL $OPENSSL_VERSION..."
    fetch_verified "https://github.com/openssl/openssl/releases/download/openssl-$OPENSSL_VERSION/openssl-$OPENSSL_VERSION.tar.gz" "$OPENSSL_SHA256" "$TEMP_DIR/openssl.tar.gz"
    tar -xzf "$TEMP_DIR/openssl.tar.gz" -C "$TEMP_DIR"
    # shellcheck disable=SC2086 # time_flags holds separate compiler flags
    (cd "$TEMP_DIR/openssl-$OPENSSL_VERSION" \
        && ./Configure "$OPENSSL_TARGET" no-shared no-tests no-docs no-apps \
            --cross-compile-prefix="$CROSS-" --prefix="$prefix" --libdir=lib $time_flags \
        && make -j"$jobs" && make install_dev)

    echo "Building libevent $LIBEVENT_VERSION..."
    fetch_verified "https://github.com/libevent/libevent/releases/download/release-$LIBEVENT_VERSION/libevent-$LIBEVENT_VERSION.tar.gz" "$LIBEVENT_SHA256" "$TEMP_DIR/libevent.tar.gz"
    tar -xzf "$TEMP_DIR/libevent.tar.gz" -C "$TEMP_DIR"
    (cd "$TEMP_DIR/libevent-$LIBEVENT_VERSION" \
        && ./configure --host="$CROSS" --prefix="$prefix" --disable-shared --enable-static --with-pic \
            --disable-openssl --disable-samples --disable-libevent-regress --disable-debug-mode \
            CFLAGS="-O2 $time_flags" \
        && make -j"$jobs" && make install)

    echo "Building tor $TOR_SRC_VERSION..."
    local tor_archive="$TEMP_DIR/tor-src.tar.gz"
    # dist.torproject.org only keeps current releases; older ones move to the archive.
    if ! fetch_verified "https://dist.torproject.org/tor-$TOR_SRC_VERSION.tar.gz" "$TOR_SRC_SHA256" "$tor_archive"; then
        fetch_verified "https://archive.torproject.org/tor-package-archive/tor-$TOR_SRC_VERSION.tar.gz" "$TOR_SRC_SHA256" "$tor_archive"
    fi
    tar -xzf "$tor_archive" -C "$TEMP_DIR"
    (cd "$TEMP_DIR/tor-$TOR_SRC_VERSION" \
        && ./configure --host="$CROSS" --prefix="$prefix" \
            --enable-static-libevent --with-libevent-dir="$prefix" \
            --enable-static-openssl --with-openssl-dir="$prefix" \
            --enable-static-zlib --with-zlib-dir="$prefix" \
            --disable-asciidoc --disable-manpage --disable-html-manual \
            --disable-lzma --disable-zstd --disable-systemd --disable-seccomp --disable-libscrypt \
            --disable-unittests --disable-module-dirauth \
            CFLAGS="-O2 $time_flags" \
        && make -j"$jobs" \
        && "$CROSS-strip" -o "$TOR_DIR/tor" src/app/tor)

    echo "tor links against:"
    "$CROSS-readelf" -d "$TOR_DIR/tor" | grep NEEDED || true
}

# 1. Tor Expert Bundle
# dist.torproject.org only serves the *current* releases, so an older pinned version gets rotated off
# it and the download silently returns a tiny error page (-> "gzip: stdin: not in gzip format"). The
# archive host keeps every version forever, so fall back to it. -f makes curl fail on HTTP errors
# instead of saving the error page, and gzip -t verifies we actually got a real tarball.
# On ARM only its architecture-independent files are used: geoip, geoip6 and pt_config.json.
echo "Downloading Tor Expert Bundle ($TOR_VERSION)..."
TOR_ARCHIVE="$TEMP_DIR/tor.tar.gz"
TOR_REL="$TOR_VERSION/tor-expert-bundle-linux-x86_64-$TOR_VERSION.tar.gz"
curl -fL "https://dist.torproject.org/torbrowser/$TOR_REL" -o "$TOR_ARCHIVE" || true
if ! gzip -t "$TOR_ARCHIVE" 2>/dev/null; then
    echo "  dist.torproject.org did not serve $TOR_VERSION; falling back to archive.torproject.org..."
    curl -fL "https://archive.torproject.org/tor-package-archive/torbrowser/$TOR_REL" -o "$TOR_ARCHIVE"
fi
tar -xzf "$TOR_ARCHIVE" -C "$TEMP_DIR"

# geoip/geoip6 live under data/ in the Linux Tor expert bundle (verified on Linux).
cp "$TEMP_DIR/data/geoip" "$TOR_DIR/"
cp "$TEMP_DIR/data/geoip6" "$TOR_DIR/"

if [ "$TARGET_ARCH" = "x64" ]; then
    cp "$TEMP_DIR/tor/tor" "$TOR_DIR/"
    cp "$TEMP_DIR/tor/tor-gencert" "$TOR_DIR/" 2>/dev/null || true

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
else
    build_tor_from_source

    cp "$TEMP_DIR/tor/pluggable_transports/pt_config.json" "$PT_DIR/"
    cp "$TEMP_DIR/tor/pluggable_transports/"README* "$PT_DIR/" 2>/dev/null || true

    # lyrebird also carries Snowflake and webtunnel, as in the expert bundle.
    echo "Building lyrebird ($LYREBIRD_TAG)..."
    LYREBIRD_SRC=$(gitlab_source lyrebird "$LYREBIRD_TAG")
    go_build "$LYREBIRD_SRC" ./cmd/lyrebird "$PT_DIR/lyrebird" "-X main.lyrebirdVersion=${LYREBIRD_TAG#lyrebird-}"

    echo "Building conjure-client..."
    CONJURE_SRC=$(gitlab_source conjure "$CONJURE_COMMIT")
    go_build "$CONJURE_SRC" ./client "$PT_DIR/conjure-client"
fi

# 2. Webtunnel client
if command -v go >/dev/null 2>&1; then
    echo "Building webtunnel-client from source ($WEBTUNNEL_VERSION)..."
    WEBTUNNEL_SRC=$(gitlab_source webtunnel "$WEBTUNNEL_VERSION")
    go_build "$WEBTUNNEL_SRC" ./main/client "$PT_DIR/webtunnel-client"
else
    echo "Go not found, skipping webtunnel-client build."
fi

# 3. Dnstt client
if command -v go >/dev/null 2>&1; then
    echo "Building dnstt-client from source..."
    DNSTT_CLONE="$TEMP_DIR/dnstt"
    git clone https://www.bamsoftware.com/git/dnstt.git "$DNSTT_CLONE"
    go_build "$DNSTT_CLONE/dnstt-client" . "$PT_DIR/dnstt-client"
else
    echo "Go not found, skipping dnstt-client build."
fi

# 4. Sing-box
echo "Downloading Sing-box..."
SB_ARCHIVE="$TEMP_DIR/sing-box.tar.gz"
curl -fL "$SING_BOX_URL" -o "$SB_ARCHIVE"
tar -xzf "$SB_ARCHIVE" -C "$TEMP_DIR"
SB_SRC_DIR=$(find "$TEMP_DIR" -maxdepth 1 -type d -name "sing-box-*")
cp "$SB_SRC_DIR/sing-box" "$VPN_DIR/"

# 5. Xray
echo "Downloading Xray..."
XRAY_ARCHIVE="$TEMP_DIR/xray.zip"
curl -fL "$XRAY_URL" -o "$XRAY_ARCHIVE"
unzip -q -o "$XRAY_ARCHIVE" -d "$TEMP_DIR/xray_ext"
cp "$TEMP_DIR/xray_ext/xray" "$VPN_DIR/"

# 6. ArtiHop
if command -v cargo >/dev/null 2>&1; then
    echo "Building ArtiHop engine..."
    ARTI_HOP_CLONE="$TEMP_DIR/ArtiHop"
    git clone --depth 1 "$ARTI_HOP_REPO_URL" "$ARTI_HOP_CLONE"
    pushd "$ARTI_HOP_CLONE"
    if [ -n "$RUST_TARGET" ]; then
        if command -v rustup >/dev/null 2>&1; then
            rustup target add "$RUST_TARGET"
        fi
        # Cargo and the cc crate (ring, sqlite, zstd) read the cross tools from these variables.
        RUST_TARGET_UPPER=$(echo "$RUST_TARGET" | tr 'a-z-' 'A-Z_')
        export "CARGO_TARGET_${RUST_TARGET_UPPER}_LINKER=$CROSS-gcc"
        export "CC_${RUST_TARGET//-/_}=$CROSS-gcc"
        export "AR_${RUST_TARGET//-/_}=$CROSS-ar"
        cargo build --release --target "$RUST_TARGET"
        cp "target/$RUST_TARGET/release/artihop" "$ARTI_HOP_DIR/"
    else
        cargo build --release
        cp "target/release/artihop" "$ARTI_HOP_DIR/"
    fi
    popd
else
    echo "Cargo not found, skipping ArtiHop build."
fi

# 7. Snowflake proxy
if command -v go >/dev/null 2>&1; then
    echo "Building Snowflake volunteer proxy..."
    # go install puts a cross-compiled binary in bin/linux_<arch>/, and refuses to when GOBIN is set.
    (unset GOBIN; CGO_ENABLED=0 GOOS=linux GOARCH="$GO_ARCH" GOARM="$GO_ARM" go install -trimpath -ldflags "-s -w" "$SNOWFLAKE_PROXY_PACKAGE")
    GO_BIN_DIR="$(go env GOPATH)/bin"
    if [ -f "$GO_BIN_DIR/linux_$GO_ARCH/proxy" ]; then
        cp "$GO_BIN_DIR/linux_$GO_ARCH/proxy" "$SNOWFLAKE_DIR/snowflake-proxy"
    else
        cp "$GO_BIN_DIR/proxy" "$SNOWFLAKE_DIR/snowflake-proxy"
    fi
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
