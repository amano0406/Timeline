#!/usr/bin/env sh
set -eu
SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
if [ "$#" -eq 0 ]; then
    set -- open
fi
dotnet run --project "$SCRIPT_DIR/launcher/Timeline.Launcher.csproj" -- --root "$SCRIPT_DIR" "$@"
