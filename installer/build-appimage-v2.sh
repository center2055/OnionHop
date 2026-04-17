#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/.." && pwd)"
solution_root="${repo_root}/OnionHop"
app_project="${solution_root}/src/OnionHopV2.App/OnionHopV2.App.csproj"
cli_project="${solution_root}/src/OnionHopV2.Cli/OnionHopV2.Cli.csproj"
app_assets_dir="${solution_root}/src/OnionHopV2.App/Assets"
linux_assets_dir="${script_dir}/linux"

configuration="Release"
runtime="linux-x64"
self_contained="true"
skip_dependencies="false"
output_dir="${script_dir}/output"
appimage_tool_path=""

usage() {
  cat <<'EOF'
Build OnionHop V2 as a Linux AppImage.
Default output is self-contained and bundles the .NET runtime plus OnionHop's Linux Tor/VPN binaries.

Usage:
  installer/build-appimage-v2.sh [options]

Options:
  -c, --configuration <name>   Build configuration (default: Release)
  -r, --runtime <rid>          Runtime identifier (default: linux-x64)
      --framework-dependent    Publish as framework-dependent instead of the default self-contained build
      --skip-deps              Skip runtime dependency staging into the AppImage
      --appimagetool <path>    Use an existing appimagetool AppImage
      --output <dir>           Output directory (default: installer/output)
  -h, --help                   Show this help
EOF
}

require_command() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Missing required command: $1" >&2
    exit 1
  fi
}

download_file() {
  local url="$1"
  local destination="$2"

  if command -v curl >/dev/null 2>&1; then
    curl -L --fail --output "$destination" "$url"
    return
  fi

  if command -v wget >/dev/null 2>&1; then
    wget -O "$destination" "$url"
    return
  fi

  echo "Missing required download tool: curl or wget" >&2
  exit 1
}

require_file() {
  if [[ ! -f "$1" ]]; then
    echo "Missing required file: $1" >&2
    exit 1
  fi
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    -c|--configuration)
      configuration="${2:-}"
      shift 2
      ;;
    -r|--runtime)
      runtime="${2:-}"
      shift 2
      ;;
    --framework-dependent)
      self_contained="false"
      shift
      ;;
    --skip-deps)
      skip_dependencies="true"
      shift
      ;;
    --appimagetool)
      appimage_tool_path="${2:-}"
      shift 2
      ;;
    --output)
      output_dir="${2:-}"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage >&2
      exit 1
      ;;
  esac
done

if [[ "$(uname -s)" != "Linux" ]]; then
  echo "AppImage builds must run on Linux." >&2
  exit 1
fi

if [[ "$runtime" != "linux-x64" ]]; then
  echo "Only linux-x64 is supported by this AppImage build script today." >&2
  exit 1
fi

require_command dotnet
require_command grep
require_command install

require_file "$app_project"
require_file "$cli_project"
require_file "${linux_assets_dir}/AppRun"
require_file "${linux_assets_dir}/onionhop.desktop"
require_file "${app_assets_dir}/logo.png"

mkdir -p "$output_dir"

version="$(grep -oPm1 '(?<=<Version>)[^<]+' "$app_project")"
publish_root="${solution_root}/src/OnionHopV2.App/bin/${configuration}/net9.0/${runtime}"
publish_dir="${publish_root}/publish"
appdir="${output_dir}/OnionHopV2.AppDir"
appimage_output="${output_dir}/OnionHopV2-${version}-linux-x86_64.AppImage"

rm -rf "$publish_root" "$appdir"
rm -f "$appimage_output"

dotnet publish "$app_project" \
  -c "$configuration" \
  -r "$runtime" \
  --self-contained "$self_contained" \
  /p:PublishSingleFile=false \
  /p:PublishReadyToRun=false

printf 'Publish mode: %s\n' "$([[ "$self_contained" == "true" ]] && echo "self-contained" || echo "framework-dependent")"

if [[ "$skip_dependencies" != "true" ]]; then
  dotnet run \
    --project "$cli_project" \
    -c "$configuration" \
    --framework net9.0 \
    -- \
    --base-dir "$publish_dir" \
    deps
fi

required_runtime_files=(
  "${publish_dir}/tor/tor"
  "${publish_dir}/tor/geoip"
  "${publish_dir}/tor/geoip6"
  "${publish_dir}/tor/pluggable_transports/pt_config.json"
  "${publish_dir}/tor/pluggable_transports/lyrebird"
  "${publish_dir}/vpn/sing-box"
  "${publish_dir}/vpn/xray"
)

optional_runtime_files=(
  "${publish_dir}/tor/pluggable_transports/conjure-client"
  "${publish_dir}/tor/pluggable_transports/snowflake-client"
  "${publish_dir}/tor/pluggable_transports/webtunnel-client"
)

missing_runtime_files=()
for file_path in "${required_runtime_files[@]}"; do
  if [[ ! -f "$file_path" ]]; then
    missing_runtime_files+=("$file_path")
  fi
done

if [[ ${#missing_runtime_files[@]} -gt 0 ]]; then
  printf 'Missing required Linux runtime files:\n' >&2
  printf ' - %s\n' "${missing_runtime_files[@]}" >&2
  exit 1
fi

missing_optional_runtime_files=()
for file_path in "${optional_runtime_files[@]}"; do
  if [[ ! -f "$file_path" ]]; then
    missing_optional_runtime_files+=("$file_path")
  fi
done

if [[ ${#missing_optional_runtime_files[@]} -gt 0 ]]; then
  printf 'Warning: optional transport binaries were not bundled:\n' >&2
  printf ' - %s\n' "${missing_optional_runtime_files[@]}" >&2
fi

mkdir -p "${appdir}/usr/bin"
cp -a "${publish_dir}/." "${appdir}/usr/bin/"
find "${appdir}/usr/bin" -name '*.pdb' -type f -delete

install -Dm755 "${linux_assets_dir}/AppRun" "${appdir}/AppRun"
install -Dm644 "${linux_assets_dir}/onionhop.desktop" "${appdir}/onionhop.desktop"
install -Dm644 "${linux_assets_dir}/onionhop.desktop" "${appdir}/usr/share/applications/onionhop.desktop"
install -Dm644 "${app_assets_dir}/logo.png" "${appdir}/onionhop.png"
install -Dm644 "${app_assets_dir}/logo.png" "${appdir}/usr/share/icons/hicolor/512x512/apps/onionhop.png"

if [[ -z "$appimage_tool_path" ]]; then
  appimage_tool_path="${output_dir}/appimagetool-x86_64.AppImage"
  if [[ ! -f "$appimage_tool_path" ]]; then
    download_file \
      "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage" \
      "$appimage_tool_path"
    chmod +x "$appimage_tool_path"
  fi
fi

require_file "$appimage_tool_path"
chmod +x "$appimage_tool_path"

APPIMAGE_EXTRACT_AND_RUN=1 ARCH=x86_64 "$appimage_tool_path" "$appdir" "$appimage_output"
chmod +x "$appimage_output"

printf 'AppImage created: %s\n' "$appimage_output"
