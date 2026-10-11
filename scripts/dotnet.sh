#!/bin/sh
set -eu
task_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
if [ -x "$task_root/.tools/dotnet/dotnet" ]; then
    DOTNET_ROOT="$task_root/.tools/dotnet"
    PATH="$DOTNET_ROOT:$PATH"
    export DOTNET_ROOT PATH
    exec "$DOTNET_ROOT/dotnet" "$@"
fi
exec dotnet "$@"
