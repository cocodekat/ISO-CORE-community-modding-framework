#!/bin/sh
# Builds the macOS release zip: dist/macos/ICCMF-<version>-macos.zip
#
# The release contains only the CLI (plus the SDK it uses to create new projects).
# Example projects are NOT bundled or built.
#
# Usage:
#   ./build-macos-release.sh
#   ./build-macos-release.sh --output /some/dir
set -eu

VERSION="0.5.2"
ROOT=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
OUTPUT_DIR="$ROOT/dist/macos"

while [ $# -gt 0 ]; do
  case "$1" in
    --output)
      shift
      [ $# -gt 0 ] || { printf '%s\n' "--output needs a directory." >&2; exit 64; }
      OUTPUT_DIR="$1"
      ;;
    *)
      printf '%s\n' "Unknown option: $1" >&2
      exit 64
      ;;
  esac
  shift
done

# ---- Find dotnet ----
if [ -n "${DOTNET_BIN:-}" ]; then
  : # Use the caller-provided executable.
elif command -v dotnet >/dev/null 2>&1; then
  DOTNET_BIN=$(command -v dotnet)
elif [ -x "$HOME/.dotnet/dotnet" ]; then
  DOTNET_BIN="$HOME/.dotnet/dotnet"
else
  printf '%s\n' "Could not find the .NET SDK. Install .NET 8 or run: DOTNET_BIN=/path/to/dotnet $0" >&2
  exit 127
fi
printf '%s\n' "Using .NET: $DOTNET_BIN"

# ---- Pack the CLI ----
"$DOTNET_BIN" pack "$ROOT/cli/ICCMF.Cli.csproj" -c Release --nologo
NUPKG="$ROOT/dist/ICCMF.Cli.$VERSION.nupkg"
[ -f "$NUPKG" ] || { printf '%s\n' "Expected package not found: $NUPKG" >&2; exit 2; }

# Sanity check: is the SDK packed inside the CLI package? (A .nupkg is a zip file.)
if unzip -l "$NUPKG" 2>/dev/null | grep -q "SoundApi.cs"; then
  printf '%s\n' "Check: SDK (including SoundApi.cs) is inside the CLI package."
else
  printf '%s\n' "WARNING: SoundApi.cs was not found inside $NUPKG." >&2
  printf '%s\n' "         Either the master Sdk/ is missing it, or the CLI project does not pack Sdk/." >&2
fi

# ---- Stage the release folder ----
STAGE="$OUTPUT_DIR/ICCMF-$VERSION-macos"
ZIP="$OUTPUT_DIR/ICCMF-$VERSION-macos.zip"
rm -rf "$STAGE"
rm -f "$ZIP"
mkdir -p "$STAGE"

cp "$ROOT/install-cli.sh" "$STAGE/"
cp "$ROOT/build-macos-release.sh" "$STAGE/"
cp "$ROOT/README.md" "$STAGE/"
chmod +x "$STAGE/install-cli.sh" "$STAGE/build-macos-release.sh"

# install-cli.sh installs from <folder>/dist, so ship the package there.
mkdir -p "$STAGE/dist"
cp "$NUPKG" "$STAGE/dist/"

# Master SDK: the CLI uses it to set up new projects (and install-cli.sh packs it from source).
rsync -a --exclude '.DS_Store' "$ROOT/Sdk/" "$STAGE/Sdk/"

# CLI source, without build output (install-cli.sh runs `dotnet pack` on it).
rsync -a --exclude 'bin/' --exclude 'obj/' --exclude '.DS_Store' "$ROOT/cli/" "$STAGE/cli/"

# ---- Zip it ----
(cd "$OUTPUT_DIR" && zip -qr "$ZIP" "ICCMF-$VERSION-macos" -x '*.DS_Store')

printf '%s\n' "macOS release created: $ZIP"