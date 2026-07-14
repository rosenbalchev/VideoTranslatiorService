#!/usr/bin/env bash
# RB.VideoTranslator - ONNX model export (optional, Linux/macOS)
#
# Separate, optional step run AFTER install.sh. Exports Whisper to ONNX so
# transcription can run natively in C# (RB.VideoTranslator.WhisperOnnx) instead
# of CTranslate2's CUDA-or-CPU-only backend. Diarization is unaffected - it
# keeps running via whisperx/torch either way.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
APPSETTINGS="$REPO_ROOT/RB.VideoTranslator.CLI/appsettings.json"
DEPSFILE="$SCRIPT_DIR/dependencies.json"

echo "============================================================"
echo " RB.VideoTranslator - ONNX model export (optional)"
echo "============================================================"
echo

if [ ! -f "$APPSETTINGS" ]; then
    echo "ERROR: appsettings.json not found at: $APPSETTINGS"
    exit 1
fi
if [ ! -f "$DEPSFILE" ]; then
    echo "ERROR: dependencies.json not found at: $DEPSFILE"
    exit 1
fi

VENV_PATH="$(python3 -c "
import json
print(json.load(open('$APPSETTINGS', encoding='utf-8-sig'))['RBVideoTranslator'].get('VenvPath', ''))
")"

if [ -z "$VENV_PATH" ]; then
    echo "ERROR: VenvPath is empty in appsettings.json - run install.sh first."
    exit 1
fi

VENV_PYTHON="$VENV_PATH/bin/python"
if [ ! -x "$VENV_PYTHON" ]; then
    echo "ERROR: Python not found in venv at: $VENV_PYTHON"
    echo "       Run install.sh first to create the venv."
    exit 1
fi

echo " Venv           : $VENV_PATH"
echo

"$VENV_PYTHON" "$SCRIPT_DIR/export_onnx_models.py" "$APPSETTINGS" "$DEPSFILE"

echo
echo "============================================================"
echo " Done!"
echo "============================================================"
