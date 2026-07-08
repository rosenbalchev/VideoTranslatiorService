#!/usr/bin/env bash
# RB.VideoTranslator - Remove Python virtual environment (Linux / macOS)
#
# Deletes the Python virtual environment and all packages installed inside
# it. FFmpeg and system Python are NOT touched.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
APPSETTINGS="$REPO_ROOT/RB.VideoTranslator.CLI/appsettings.json"

echo "============================================================"
echo " RB.VideoTranslator - Remove Python virtual environment"
echo "============================================================"
echo
echo " This deletes the Python virtual environment and all packages"
echo " installed inside it. FFmpeg and system Python are NOT touched."
echo

if [ ! -f "$APPSETTINGS" ]; then
    echo "ERROR: appsettings.json not found at:"
    echo "       $APPSETTINGS"
    exit 1
fi

PYTHON_BIN=""
for candidate in python3.12 python3 python; do
    if command -v "$candidate" >/dev/null 2>&1; then
        PYTHON_BIN="$candidate"
        break
    fi
done
if [ -z "$PYTHON_BIN" ]; then
    echo "ERROR: No python interpreter found to read appsettings.json."
    exit 1
fi

VENV_PATH="$("$PYTHON_BIN" -c "
import json
print(json.load(open('$APPSETTINGS', encoding='utf-8-sig'))['RBVideoTranslator'].get('VenvPath', ''))
")"

if [ -z "$VENV_PATH" ]; then
    WORK_FOLDER="$("$PYTHON_BIN" -c "
import json
print(json.load(open('$APPSETTINGS', encoding='utf-8-sig'))['RBVideoTranslator'].get('WorkingFolderPath', ''))
")"
    if [ -z "$WORK_FOLDER" ]; then
        echo "ERROR: Both VenvPath and WorkingFolderPath are empty in appsettings.json."
        echo "       Nothing to remove."
        exit 1
    fi
    VENV_PATH="$WORK_FOLDER/rb.video.translator"
    echo " VenvPath was empty — falling back to default: $VENV_PATH"
fi

echo " Virtual env: $VENV_PATH"
echo

if [ ! -d "$VENV_PATH" ]; then
    echo " \"$VENV_PATH\" not found - nothing to remove."
    exit 0
fi

if [ -n "${VIRTUAL_ENV:-}" ] && [ "${VIRTUAL_ENV:-}" = "$VENV_PATH" ]; then
    echo "ERROR: This virtual environment is currently activated ($VIRTUAL_ENV)."
    echo "       Run \"deactivate\" first, then re-run this script."
    exit 1
fi

read -r -p "  Type YES to confirm deletion: " CONFIRM
if [ "$CONFIRM" != "YES" ]; then
    echo " Cancelled."
    exit 0
fi

echo
echo " Removing $VENV_PATH..."
rm -rf "$VENV_PATH"

echo " Clearing VenvPath in appsettings.json..."
"$PYTHON_BIN" -c "
import json
path = '$APPSETTINGS'
with open(path, encoding='utf-8-sig') as f:
    data = json.load(f)
data['RBVideoTranslator']['VenvPath'] = ''
with open(path, 'w', encoding='utf-8') as f:
    json.dump(data, f, indent=4)
" || echo " WARNING: Could not clear VenvPath in appsettings.json — clear it manually."

echo " Done. Run scripts/install.sh to reinstall."
echo
