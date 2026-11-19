#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

if [ -n "${DOTNET_BIN:-}" ]; then
  : # Use the caller-provided executable.
elif command -v dotnet >/dev/null 2>&1; then
  DOTNET_BIN=$(command -v dotnet)
elif [ -x "$HOME/.dotnet/dotnet" ]; then
  DOTNET_BIN="$HOME/.dotnet/dotnet"
else
  printf '%s\n' "Could not find dotnet." >&2
  printf '%s\n' "Install .NET or run: DOTNET_BIN=/path/to/dotnet ./install-cli.sh" >&2
  exit 127
fi

printf '%s\n' "Using .NET: $DOTNET_BIN"

"$DOTNET_BIN" pack "$SCRIPT_DIR/cli/ICCMF.Cli.csproj" -c Release --nologo
"$DOTNET_BIN" tool update --global ICCMF.Cli --add-source "$SCRIPT_DIR/dist" --version 0.5.2 \
  || "$DOTNET_BIN" tool install --global ICCMF.Cli --add-source "$SCRIPT_DIR/dist" --version 0.5.2

printf '%s\n' "Installed iccmf 0.5.2."
printf '%s\n' "If 'iccmf' is not found, add \$HOME/.dotnet/tools to PATH."
