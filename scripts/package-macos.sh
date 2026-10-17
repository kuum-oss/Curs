#!/bin/sh
set -eu
task_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
if [ "$(uname -s)" != Darwin ]; then
    echo 'This helper packages the native macOS client.' >&2
    exit 1
fi
task_bundle="$task_root/TestResults/KanbanBoard.app"
mkdir -p "$task_bundle/Contents/MacOS" "$task_bundle/Contents/Resources"
cp "$task_root/Curs/Assets/kanbanboard.icns" "$task_bundle/Contents/Resources/kanbanboard.icns"
# Let Launch Services start the apphost itself. A shell launcher changes the
# window's process identity and prevents reliable native pointer routing.
if [ -x "$task_root/.tools/dotnet/dotnet" ]; then
    AppHostRelativeDotNet=../../../../.tools/dotnet
    export AppHostRelativeDotNet
fi
"$task_root/scripts/dotnet.sh" publish "$task_root/Curs/Curs.csproj" -c Release -o "$task_bundle/Contents/MacOS"
python3 - "$task_bundle/Contents/Info.plist" <<'PY'
from pathlib import Path
import plistlib
import sys
Path(sys.argv[1]).write_bytes(plistlib.dumps({
    'CFBundleExecutable': 'Curs',
    'CFBundleIdentifier': 'com.kuum.kanbanboard',
    'CFBundleName': 'KanbanBoard',
    'CFBundleIconFile': 'kanbanboard.icns',
    'CFBundlePackageType': 'APPL',
    'NSHighResolutionCapable': True,
}))
PY
echo "Native app: $task_bundle"
