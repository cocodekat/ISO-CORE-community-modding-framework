#!/bin/sh
# ISO-CORE modding setup for macOS.
# Installs BepInEx 5 into the game folder, installs the ICCMF command-line tool,
# and prints the Steam launch option macOS needs.
#
# Usage:  sh install.sh [--force]
#   --force   reinstall BepInEx even if it is already installed
set -eu

BEPINEX_VERSION="5.4.23.6"
ICCMF_VERSION="0.5.2"
REPO="cocodekat/ISO-CORE-community-modding-framework"
ICCMF_URL="https://github.com/$REPO/raw/main/dist/macos/ICCMF-$ICCMF_VERSION-macos.zip"
DEFAULT_GAME_DIR="$HOME/Library/Application Support/Steam/steamapps/common/ISO-CORE Playtest"

say() { printf '%s\n' "$*"; }
die() { printf 'Error: %s\n' "$*" >&2; exit 1; }

FORCE=0
for arg in "$@"; do
  case "$arg" in
    --force) FORCE=1 ;;
    *) die "Unknown option: $arg" ;;
  esac
done

# ---- 1. Detect the operating system ----
case "$(uname -s)" in
  Darwin) ;;
  MINGW*|MSYS*|CYGWIN*)
    die "This is the macOS installer. On Windows, run install.ps1 in PowerShell." ;;
  *)
    die "Unsupported operating system: $(uname -s). Only macOS and Windows are supported." ;;
esac

# ---- 2. Ask where ISO-CORE is ----
say "Enter the location of ISO-CORE: the game's .app (for example 'ISO-CORE MacOS Build.app'),"
say "or the folder that contains it. Tip: drag it from Finder into this window."
printf 'Location: '
INPUT=""
if [ -r /dev/tty ]; then read -r INPUT < /dev/tty || INPUT=""; else read -r INPUT || INPUT=""; fi

# Trim whitespace and quotes, undo Finder's backslash-escaped spaces, expand ~.
INPUT=$(printf '%s' "$INPUT" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' \
  -e "s/^['\"]//" -e "s/['\"]$//" -e 's/\\ / /g')
case "$INPUT" in
  "~"|"~/"*) INPUT="$HOME${INPUT#\~}" ;;
esac
INPUT=${INPUT%/}
[ -n "$INPUT" ] || die "No location given. Nothing was installed."

APP=""
if [ -d "$INPUT" ]; then
  case "$INPUT" in
    *.app) APP="$INPUT" ;;
    *)
      for candidate in "$INPUT"/*.app; do
        if [ -f "$candidate/Contents/Resources/Data/Managed/Assembly-CSharp.dll" ]; then
          APP="$candidate"
          break
        fi
      done
      ;;
  esac
fi
if [ -z "$APP" ] || [ ! -f "$APP/Contents/Resources/Data/Managed/Assembly-CSharp.dll" ]; then
  die "Could not find the ISO-CORE app at: $INPUT"
fi
APP_NAME=$(basename "$APP")
GAME_DIR=$(dirname "$APP")
say "Found ISO-CORE: $APP"

# ---- .NET SDK (needed for ICCMF); check now so nothing is half-installed ----
if [ -n "${DOTNET_BIN:-}" ]; then
  DOTNET="$DOTNET_BIN"
elif command -v dotnet >/dev/null 2>&1; then
  DOTNET=$(command -v dotnet)
elif [ -x "$HOME/.dotnet/dotnet" ]; then
  DOTNET="$HOME/.dotnet/dotnet"
else
  die ".NET SDK not found. Install the .NET 8 SDK from https://dotnet.microsoft.com/download and run this again."
fi
"$DOTNET" --list-sdks 2>/dev/null | grep -q . ||
  die "Found dotnet at $DOTNET, but no .NET SDK is installed (only a runtime). Install the .NET 8 SDK."
export DOTNET_BIN="$DOTNET"

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

# ---- 3. Install BepInEx ----
if [ -f "$GAME_DIR/BepInEx/core/BepInEx.dll" ] && [ "$FORCE" -ne 1 ]; then
  say "BepInEx is already installed in $GAME_DIR (use --force to reinstall). Skipping."
else
  say "Finding BepInEx $BEPINEX_VERSION for macOS..."
  API="https://api.github.com/repos/BepInEx/BepInEx/releases/tags/v$BEPINEX_VERSION"
  URLS=$(curl -fsSL -H 'User-Agent: iccmf-installer' "$API" 2>/dev/null |
    grep -o '"browser_download_url": *"[^"]*"' | sed 's/^[^:]*: *"//; s/"$//' || true)
  URL=$(printf '%s\n' "$URLS" | grep 'BepInEx_macos_universal_' | head -n 1 || true)
  [ -n "$URL" ] || URL=$(printf '%s\n' "$URLS" | grep 'BepInEx_macos_x64_' | head -n 1 || true)
  [ -n "$URL" ] ||
    URL="https://github.com/BepInEx/BepInEx/releases/download/v$BEPINEX_VERSION/BepInEx_macos_universal_$BEPINEX_VERSION.zip"

  say "Downloading $URL"
  curl -fL --progress-bar -o "$TMP/bepinex.zip" "$URL" || die "Could not download BepInEx from $URL"
  unzip -tq "$TMP/bepinex.zip" >/dev/null || die "The BepInEx download is not a valid zip file."
  unzip -qo "$TMP/bepinex.zip" -d "$GAME_DIR"

  [ -f "$GAME_DIR/BepInEx/core/BepInEx.dll" ] ||
    die "BepInEx did not extract correctly into $GAME_DIR."
  RUN="$GAME_DIR/run_bepinex.sh"
  [ -f "$RUN" ] || die "run_bepinex.sh is missing from the BepInEx download."

  # Point the launcher at the game's .app.
  if grep -q '^executable_name=""' "$RUN"; then
    ESCAPED=$(printf '%s' "$APP_NAME" | sed 's/[&|\\]/\\&/g')
    sed -i '' "s|^executable_name=\"\"|executable_name=\"$ESCAPED\"|" "$RUN"
  fi
  grep -qF "executable_name=\"$APP_NAME\"" "$RUN" ||
    say "Note: open run_bepinex.sh and make sure executable_name is \"$APP_NAME\"."

  chmod +x "$RUN"
  xattr -dr com.apple.quarantine "$RUN" "$GAME_DIR"/libdoorstop*.dylib 2>/dev/null || true
  say "BepInEx $BEPINEX_VERSION installed."
fi

# ---- 4. Install ICCMF ----
say "Downloading ICCMF $ICCMF_VERSION..."
curl -fL --progress-bar -o "$TMP/iccmf.zip" "$ICCMF_URL" || die "Could not download ICCMF from $ICCMF_URL"
unzip -tq "$TMP/iccmf.zip" >/dev/null || die "The ICCMF download is not a valid zip file."
mkdir -p "$TMP/iccmf"
unzip -qo "$TMP/iccmf.zip" -d "$TMP/iccmf"
INSTALLER=$(find "$TMP/iccmf" -name install-cli.sh | head -n 1)
[ -n "$INSTALLER" ] || die "install-cli.sh was not found inside the ICCMF download."
( cd "$(dirname "$INSTALLER")" && sh ./install-cli.sh ) || die "The ICCMF installer failed."

# ---- 5. Instructions ----
LAUNCH_OPTION="\"$GAME_DIR/run_bepinex.sh\" %command%"
if command -v pbcopy >/dev/null 2>&1; then printf '%s' "$LAUNCH_OPTION" | pbcopy; COPIED=1; else COPIED=0; fi

say ""
say "============================================================"
say " Almost done: set the Steam launch option (required on macOS)"
say "============================================================"
say "BepInEx only loads if Steam starts the game through run_bepinex.sh."
say ""
say "  1. Open Steam and go to your Library."
say "  2. Right-click ISO-CORE and choose Properties."
say "  3. On the General tab, find 'Launch Options'."
say "  4. Paste this exactly:"
say ""
say "     $LAUNCH_OPTION"
say ""
if [ "$COPIED" -eq 1 ]; then say "     (It is already on your clipboard: just press Cmd+V.)"; say ""; fi
say "  5. Close the window and launch the game from Steam."
say ""
say "After the first launch, BepInEx/LogOutput.log in the game folder should show BepInEx starting."
say "If the game does not start, remove the launch option to get back to the unmodded game."
say ""

if [ "$GAME_DIR" != "$DEFAULT_GAME_DIR" ]; then
  say "Your game is not in the default Steam location, so tell iccmf where it is."
  say "Add this line to ~/.zshrc, then open a new terminal:"
  say ""
  say "     export ISOCORE_GAME_DIR=\"$GAME_DIR\""
  say ""
fi
if ! command -v iccmf >/dev/null 2>&1; then
  say "If 'iccmf' is not found in a new terminal, add the .NET tools folder to your PATH:"
  say ""
  say "     echo 'export PATH=\"\$PATH:\$HOME/.dotnet/tools\"' >> ~/.zshrc"
  say ""
fi
say "Done. Create your first mod with:  iccmf create mod MyMod"