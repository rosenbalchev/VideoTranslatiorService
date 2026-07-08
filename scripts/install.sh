#!/usr/bin/env bash
# RB.VideoTranslator - Python environment setup (Linux / macOS)
#
# Usage:
#   scripts/install.sh              # auto-detect CUDA hardware
#   scripts/install.sh --cuda       # force the CUDA build
#   scripts/install.sh --cpu        # force the CPU build
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
APPSETTINGS="$REPO_ROOT/RB.VideoTranslator.CLI/appsettings.json"
DEPSFILE="$SCRIPT_DIR/dependencies.json"

echo "============================================================"
echo " RB.VideoTranslator - Python environment setup"
echo "============================================================"
echo

if [ ! -f "$APPSETTINGS" ]; then
    echo "ERROR: appsettings.json not found at:"
    echo "       $APPSETTINGS"
    echo
    echo " Set WorkingFolderPath in that file before running this script."
    exit 1
fi
if [ ! -f "$DEPSFILE" ]; then
    echo "ERROR: dependencies.json not found at:"
    echo "       $DEPSFILE"
    exit 1
fi

# ── Find Python 3.12 ──────────────────────────────────────────────────────────
PYTHON_BIN=""
for candidate in python3.12 python3 python; do
    if command -v "$candidate" >/dev/null 2>&1; then
        ver="$("$candidate" -c 'import sys; print(f"{sys.version_info[0]}.{sys.version_info[1]}")' 2>/dev/null || true)"
        if [ "$ver" = "3.12" ]; then
            PYTHON_BIN="$candidate"
            break
        fi
    fi
done

if [ -z "$PYTHON_BIN" ]; then
    echo "ERROR: Python 3.12 not found."
    echo "       macOS:  brew install python@3.12"
    echo "       Ubuntu/Debian: sudo apt install python3.12 python3.12-venv"
    echo "         If that package isn't found (Ubuntu 22.04/20.04 don't ship 3.12"
    echo "         by default — only 24.04+ does), add the deadsnakes PPA first:"
    echo "           sudo apt install software-properties-common"
    echo "           sudo add-apt-repository ppa:deadsnakes/ppa"
    echo "           sudo apt update"
    echo "           sudo apt install python3.12 python3.12-venv"
    exit 1
fi
echo " Python         : $("$PYTHON_BIN" --version)"

# ── Read WorkingFolderPath / HfToken from appsettings.json ───────────────────
# Note: encoding='utf-8-sig' — appsettings.json is written by PowerShell's
# `Set-Content -Encoding UTF8`, which prepends a BOM that plain utf-8 chokes on.
WORK_FOLDER="$("$PYTHON_BIN" -c "
import json
print(json.load(open('$APPSETTINGS', encoding='utf-8-sig'))['RBVideoTranslator'].get('WorkingFolderPath', ''))
")"

if [ -z "$WORK_FOLDER" ]; then
    echo "ERROR: WorkingFolderPath is empty in appsettings.json."
    echo "       Edit the file and set RBVideoTranslator.WorkingFolderPath before running."
    exit 1
fi

HF_TOKEN_VALUE="$("$PYTHON_BIN" -c "
import json
print(json.load(open('$APPSETTINGS', encoding='utf-8-sig'))['RBVideoTranslator'].get('HfToken', ''))
")"

VENV_PATH="$WORK_FOLDER/rb.video.translator"
echo " Working folder : $WORK_FOLDER"
echo " Virtual env    : $VENV_PATH"
echo

# Create working folder structure
mkdir -p "$WORK_FOLDER/input" "$WORK_FOLDER/processing" "$WORK_FOLDER/output"

if [ -n "${VIRTUAL_ENV:-}" ]; then
    echo " Note: virtual env $VIRTUAL_ENV is active in this shell — it will be"
    echo "       superseded once the new venv below is activated."
fi

# ── Detect platform & CUDA hardware (pass --cuda or --cpu to override) ───────
OS_NAME="$(uname -s)"
MODE="cpu"
case "${1:-}" in
    --cuda) MODE="cuda" ;;
    --cpu)  MODE="cpu" ;;
    "")
        if [ "$OS_NAME" = "Linux" ] && command -v nvidia-smi >/dev/null 2>&1 && nvidia-smi >/dev/null 2>&1; then
            MODE="cuda"
        fi
        ;;
    *)
        echo "ERROR: Unknown argument '$1'. Use --cuda, --cpu, or no argument to auto-detect."
        exit 1
        ;;
esac

if [ "$OS_NAME" = "Darwin" ] && [ "$MODE" = "cuda" ]; then
    echo " WARNING: --cuda was requested but macOS has no NVIDIA/CUDA support — using CPU."
    MODE="cpu"
fi

echo " Platform       : $OS_NAME"
if [ "$MODE" = "cuda" ]; then
    echo " Hardware       : NVIDIA GPU detected (nvidia-smi) — installing CUDA build"
else
    echo " Hardware       : No CUDA GPU detected — installing CPU build"
    echo "                  (pass --cuda to force the CUDA build, e.g. after a driver install)"
fi
echo

# ── [1/7] FFmpeg ───────────────────────────────────────────────────────────
echo "[1/7] Installing FFmpeg (system dependency)..."
if [ "$OS_NAME" = "Darwin" ]; then
    if ! command -v brew >/dev/null 2>&1; then
        echo "ERROR: Homebrew not found. Install it from https://brew.sh, then re-run."
        exit 1
    fi
    FFMPEG_PKG="$("$PYTHON_BIN" -c "import json; print(json.load(open('$DEPSFILE', encoding='utf-8-sig'))['system']['ffmpeg']['brew'])")"
    brew list "$FFMPEG_PKG" >/dev/null 2>&1 || brew install "$FFMPEG_PKG"
elif [ "$OS_NAME" = "Linux" ]; then
    FFMPEG_PKG="$("$PYTHON_BIN" -c "import json; print(json.load(open('$DEPSFILE', encoding='utf-8-sig'))['system']['ffmpeg']['apt'])")"
    if command -v apt-get >/dev/null 2>&1; then
        sudo apt-get update -y
        sudo apt-get install -y "$FFMPEG_PKG"
    else
        echo " WARNING: apt-get not found — install ffmpeg manually with your distro's package manager."
    fi
else
    echo " WARNING: Unrecognised platform '$OS_NAME' — install ffmpeg manually."
fi

# ── [2/7] venv ─────────────────────────────────────────────────────────────
echo "[2/7] Creating virtual environment \"rb.video.translator\" in working folder..."
"$PYTHON_BIN" -m venv "$VENV_PATH"

# ── [3/7] activate ───────────────────────────────────────────────────────────
echo "[3/7] Activating environment..."
# shellcheck disable=SC1091
source "$VENV_PATH/bin/activate"

# ── [4/7] pip ──────────────────────────────────────────────────────────────
echo "[4/7] Upgrading pip..."
python -m pip install --upgrade pip --quiet

# ── [5/7] PyTorch + Demucs ───────────────────────────────────────────────────
echo "[5/7] Installing PyTorch 2.5.1 ($MODE) + Demucs..."
echo "       (This can take several minutes - PyTorch is a large download)"
echo "       (Pinned to 2.5.1 — 2.6+ requires torchcodec, which has no Windows build"
echo "       and isn't installed here either)"
# Deliberately unquoted below: the space-joined package list is meant to word-split
# into separate pip arguments.
TORCH_PKGS="$("$PYTHON_BIN" -c "import json; print(' '.join(json.load(open('$DEPSFILE', encoding='utf-8-sig'))['$MODE']['torch']))")"
if [ "$MODE" = "cuda" ]; then
    INDEX_URL="$("$PYTHON_BIN" -c "import json; print(json.load(open('$DEPSFILE', encoding='utf-8-sig'))['cuda']['indexUrl'])")"
    pip install $TORCH_PKGS --index-url "$INDEX_URL"
elif [ "$OS_NAME" = "Darwin" ]; then
    # download.pytorch.org/whl/cpu has no macOS wheels — default PyPI already
    # ships the mac build (with MPS support) for these exact version pins.
    pip install $TORCH_PKGS
else
    INDEX_URL="$("$PYTHON_BIN" -c "import json; print(json.load(open('$DEPSFILE', encoding='utf-8-sig'))['cpu']['indexUrl'])")"
    pip install $TORCH_PKGS --index-url "$INDEX_URL"
fi
pip install soundfile
pip install demucs

echo "       Patching torchaudio to fall back to soundfile when torchcodec is absent..."
python "$SCRIPT_DIR/patch_torchaudio.py" || echo "       WARNING: torchaudio patch did not apply — continuing anyway."

# ── [6/7] WhisperX + common deps ─────────────────────────────────────────────
echo "[6/7] Installing WhisperX (transcription + speaker diarization)..."
echo "       (whisperx pinned to 3.4.2, pyannote-audio to 3.4.0, speechbrain to 1.0.3 —"
echo "       see scripts/dependencies.json \"commonNotes\" for why)"
COMMON_PKGS="$("$PYTHON_BIN" -c "import json; print(' '.join(json.load(open('$DEPSFILE', encoding='utf-8-sig'))['common']))")"
if [ "$MODE" = "cuda" ]; then
    echo "       (+ CUDA runtime libs: nvidia-cublas-cu12, nvidia-cudnn-cu12 — see"
    echo "       scripts/dependencies.json \"cuda.notes\" for why)"
    EXTRA_PKGS="$("$PYTHON_BIN" -c "import json; print(' '.join(json.load(open('$DEPSFILE', encoding='utf-8-sig'))['cuda'].get('extra', [])))")"
    pip install $COMMON_PKGS $EXTRA_PKGS
else
    pip install $COMMON_PKGS
fi

# ── [7/7] HuggingFace token ───────────────────────────────────────────────────
echo "[7/7] Caching HuggingFace token for speaker diarization..."
if [ -n "$HF_TOKEN_VALUE" ]; then
    if hf auth login --token "$HF_TOKEN_VALUE"; then
        echo " HuggingFace token cached."
    else
        echo " WARNING: HuggingFace login failed — check the token in appsettings.json."
    fi
else
    echo "       Skipped — RBVideoTranslator.HfToken is empty in appsettings.json."
    echo "       Set it and re-run this script, or set the HF_TOKEN env var manually."
fi

# ── Write VenvPath back to appsettings.json ───────────────────────────────────
echo
echo " Updating VenvPath in appsettings.json..."
"$PYTHON_BIN" -c "
import json
path, venv = '$APPSETTINGS', '$VENV_PATH'
with open(path, encoding='utf-8-sig') as f:
    data = json.load(f)
data['RBVideoTranslator']['VenvPath'] = venv
with open(path, 'w', encoding='utf-8') as f:
    json.dump(data, f, indent=4)
" && echo " VenvPath updated." || echo " WARNING: Could not update VenvPath in appsettings.json — set it manually."

echo
echo "============================================================"
echo " Done! (mode: $MODE)"
echo "============================================================"
echo
echo " Verify the installation:"
echo "   source \"$VENV_PATH/bin/activate\""
echo "   python -c \"import whisperx; print('WhisperX OK')\""
echo "   python -c \"import librosa; print('librosa OK')\""
echo "   python -c \"import torch; print('CUDA available:', torch.cuda.is_available())\""
echo "   python -c \"import demucs; print('Demucs OK')\""
echo "   ffmpeg -version"
echo
if [ "$MODE" = "cuda" ]; then
    echo " NOTE: cu124 requires CUDA 12.4+ drivers."
    echo "       Runs fine on newer runtimes (12.6, 12.8) — CUDA is backward-compatible."
    echo "       For older drivers visit https://pytorch.org/get-started/locally/"
    echo "       to get the correct --index-url for your driver version."
    echo
fi
echo " Speaker diarization needs a HuggingFace token (one-time setup):"
echo "   1. Accept terms at https://huggingface.co/pyannote/speaker-diarization-3.1"
echo "   2. Accept terms at https://huggingface.co/pyannote/segmentation-3.0"
echo "   3. Create a read-scoped token at https://huggingface.co/settings/tokens"
echo "   4. Put it in RBVideoTranslator.HfToken in appsettings.json, then re-run this script"
echo
echo " Force a specific build regardless of detected hardware:"
echo "   scripts/install.sh --cuda"
echo "   scripts/install.sh --cpu"
echo "============================================================"
