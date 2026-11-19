#!/bin/sh
# Builds the WINDOWS release zip from macOS/Linux: dist/windows/ICCMF-<version>-windows.zip
# Same contents as build-windows-release.ps1 produces.
#
# The release contains only the CLI (plus the SDK it uses to create new projects).
# Example projects are NOT bundled or built.
#
# Usage:
#   ./build-windows-release.sh
#   ./build-windows-release.sh --output /some/dir
set -eu

VERSION="0.5.2"
ROOT=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
OUTPUT_DIR="$ROOT/dist/windows"

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

# ---- Pack the CLI (the package is platform-independent) ----
"$DOTNET_BIN" pack "$ROOT/cli/ICCMF.Cli.csproj" -c Release --nologo
NUPKG="$ROOT/dist/ICCMF.Cli.$VERSION.nupkg"
[ -f "$NUPKG" ] || { printf '%s\n' "Expected package not found: $NUPKG" >&2; exit 2; }

# Sanity check: is the SDK packed inside the CLI package? (A .nupkg is a zip file.)
if unzip -p "$NUPKG" 'tools/*/any/ICCMF.Cli.dll' 2>/dev/null | grep -aq "ICCMF.Sdk.SoundApi.cs"; then
  printf '%s\n' "Check: SDK (including SoundApi.cs) is embedded in the CLI."
else
  printf '%s\n' "WARNING: SoundApi.cs is not embedded in the CLI package." >&2
  printf '%s\n' "         Add it to cli/ICCMF.Cli.csproj (EmbeddedResource) and to ExtractSdk in cli/Program.cs." >&2
fi

# ---- Stage the release folder ----
STAGE="$OUTPUT_DIR/ICCMF-$VERSION-windows"
ZIP="$OUTPUT_DIR/ICCMF-$VERSION-windows.zip"
rm -rf "$STAGE"
rm -f "$ZIP"
mkdir -p "$STAGE"

# install-cli.bat must have Windows (CRLF) line endings or cmd.exe can mis-handle goto labels.
awk '{ sub(/\r$/, ""); printf "%s\r\n", $0 }' "$ROOT/install-cli.bat" > "$STAGE/install-cli.bat"

cp "$ROOT/README.md" "$STAGE/"
[ -f "$ROOT/WINDOWS-GUIDE.md" ] && cp "$ROOT/WINDOWS-GUIDE.md" "$STAGE/"
[ -f "$ROOT/build-windows-release.ps1" ] && cp "$ROOT/build-windows-release.ps1" "$STAGE/"

# install-cli.bat looks for the package in <folder>\dist first, so ship it there.
mkdir -p "$STAGE/dist"
cp "$NUPKG" "$STAGE/dist/"

# Master SDK: the CLI uses it to set up new projects.
rsync -a --exclude '.DS_Store' "$ROOT/Sdk/" "$STAGE/Sdk/"

# CLI source, without build output.
rsync -a --exclude 'bin/' --exclude 'obj/' --exclude '.DS_Store' "$ROOT/cli/" "$STAGE/cli/"

# ---- Zip it ----
(cd "$OUTPUT_DIR" && zip -qr "$ZIP" "ICCMF-$VERSION-windows" -x '*.DS_Store')

printf '%s\n' "Windows release created: $ZIP"