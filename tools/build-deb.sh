#!/usr/bin/env bash
# Builds the Linux release of Developer Island: tests, a self-contained publish, a portable
# tarball and a .deb package for Ubuntu/Debian.
#   tools/build-deb.sh [--version 0.3.0] [--runtime linux-x64|linux-arm64] [--skip-tests]
# Requirements: .NET 10 SDK (dotnet on PATH or in ~/.dotnet) and dpkg-deb.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version=""
runtime="linux-x64"
skip_tests=0
while [[ $# -gt 0 ]]; do
    case "$1" in
        --version) version="$2"; shift 2 ;;
        --runtime) runtime="$2"; shift 2 ;;
        --skip-tests) skip_tests=1; shift ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

if [[ -z "$version" ]]; then
    version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props" | head -n1)"
fi
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Version must be x.y.z" >&2; exit 2; }
case "$runtime" in
    linux-x64) arch="amd64" ;;
    linux-arm64) arch="arm64" ;;
    *) echo "Runtime must be linux-x64 or linux-arm64" >&2; exit 2 ;;
esac

dotnet="$(command -v dotnet || true)"
if [[ -z "$dotnet" && -x "$HOME/.dotnet/dotnet" ]]; then
    dotnet="$HOME/.dotnet/dotnet"
    export DOTNET_ROOT="$HOME/.dotnet"
fi
[[ -n "$dotnet" ]] || { echo "The .NET 10 SDK is required: https://dotnet.microsoft.com/download" >&2; exit 1; }
command -v dpkg-deb >/dev/null || { echo "dpkg-deb is required (sudo apt install dpkg-dev)" >&2; exit 1; }
export DOTNET_CLI_TELEMETRY_OPTOUT=1

echo "Developer Island $version ($runtime)"
artifacts="$root/artifacts"
publish="$artifacts/publish/$runtime"

if [[ $skip_tests -eq 0 ]]; then
    "$dotnet" test "$root/tests/DeveloperIsland.Tests" -c Release
fi

rm -rf "$publish"
"$dotnet" publish "$root/src/DeveloperIsland.Linux/DeveloperIsland.Linux.csproj" -c Release -r "$runtime" \
    --self-contained true -p:Version="$version" -p:PublishReadyToRun=true -o "$publish"
find "$publish" -name '*.pdb' -delete
cp "$root/LICENSE" "$publish/LICENSE.txt"
cp "$root/THIRD_PARTY_NOTICES.md" "$publish/THIRD_PARTY_NOTICES.md"

# Portable tarball: unpack anywhere and run ./developer-island.
tarball="$artifacts/DeveloperIsland-$version-$runtime.tar.gz"
tar -C "$artifacts/publish" -czf "$tarball" --transform "s:^$runtime:developer-island-$version:" "$runtime"

# Debian package: the app in /opt, a launcher on PATH, a menu entry and the icon.
stage="$artifacts/deb/developer-island_${version}_${arch}"
rm -rf "$stage"
mkdir -p "$stage/DEBIAN" "$stage/opt/developer-island" "$stage/usr/bin" \
    "$stage/usr/share/applications" "$stage/usr/share/icons/hicolor/256x256/apps" \
    "$stage/usr/share/doc/developer-island"
cp -a "$publish/." "$stage/opt/developer-island/"
ln -s /opt/developer-island/developer-island "$stage/usr/bin/developer-island"
install -m 644 "$root/packaging/linux/developer-island.desktop" "$stage/usr/share/applications/developer-island.desktop"
install -m 644 "$root/src/DeveloperIsland/Assets/DeveloperIsland-256.png" "$stage/usr/share/icons/hicolor/256x256/apps/developer-island.png"
install -m 644 "$root/LICENSE" "$stage/usr/share/doc/developer-island/copyright"
find "$stage/opt/developer-island" -type d -exec chmod 755 {} +
find "$stage/opt/developer-island" -type f -exec chmod 644 {} +
chmod 755 "$stage/opt/developer-island/developer-island"
find "$stage/opt/developer-island" -maxdepth 1 -name '*.so' -exec chmod 755 {} +

size="$(du -sk "$stage" --exclude=DEBIAN | cut -f1)"
sed -e "s/@VERSION@/$version/" -e "s/@ARCH@/$arch/" -e "s/@SIZE@/$size/" \
    "$root/packaging/linux/control.in" > "$stage/DEBIAN/control"
install -m 755 "$root/packaging/linux/postinst" "$stage/DEBIAN/postinst"
install -m 755 "$root/packaging/linux/postrm" "$stage/DEBIAN/postrm"

deb="$artifacts/developer-island_${version}_${arch}.deb"
dpkg-deb --root-owner-group -Zxz --build "$stage" "$deb"

echo
echo "Built:"
echo "  $deb"
echo "  $tarball"
echo "Install with: sudo apt install $deb"
