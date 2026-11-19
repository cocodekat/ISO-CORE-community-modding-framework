#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
if [ -n "${DOTNET_BIN:-}" ]; then :
elif command -v dotnet >/dev/null 2>&1; then DOTNET_BIN=$(command -v dotnet)
elif [ -x "$HOME/.dotnet/dotnet" ]; then DOTNET_BIN="$HOME/.dotnet/dotnet"
else printf '%s\n' "Could not find dotnet." >&2; exit 127
fi
"$DOTNET_BIN" build "$SCRIPT_DIR/ZombieSurvival.csproj" -c Release "$@"
