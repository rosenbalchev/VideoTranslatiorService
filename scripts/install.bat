@echo off
setlocal enabledelayedexpansion

echo ============================================================
echo  RB.VideoTranslator - Python environment setup
echo ============================================================
echo.

:: ── Locate config files ──────────────────────────────────────────────────────
set APPSETTINGS=%~dp0..\RB.VideoTranslator.CLI\appsettings.json
set DEPSFILE=%~dp0dependencies.json

if not exist "%APPSETTINGS%" (
    echo ERROR: appsettings.json not found at:
    echo        %APPSETTINGS%
    echo.
    echo  Set WorkingFolderPath in that file before running this script.
    exit /b 1
)
if not exist "%DEPSFILE%" (
    echo ERROR: dependencies.json not found at:
    echo        %DEPSFILE%
    exit /b 1
)

for /f "delims=" %%i in ('powershell -NoProfile -Command ^
    "(Get-Content '%APPSETTINGS%' -Raw | ConvertFrom-Json).RBVideoTranslator.WorkingFolderPath"') do (
    set WORK_FOLDER=%%i
)

if "!WORK_FOLDER!"=="" (
    echo ERROR: WorkingFolderPath is empty in appsettings.json.
    echo        Edit the file and set RBVideoTranslator.WorkingFolderPath before running.
    exit /b 1
)

:: ── Read HfToken from appsettings.json (optional — needed for diarization) ──
set HF_TOKEN_VALUE=
for /f "delims=" %%i in ('powershell -NoProfile -Command ^
    "(Get-Content '%APPSETTINGS%' -Raw | ConvertFrom-Json).RBVideoTranslator.HfToken"') do (
    set HF_TOKEN_VALUE=%%i
)

echo  Working folder : !WORK_FOLDER!
set VENV_PATH=!WORK_FOLDER!\rb.video.translator
echo  Virtual env    : !VENV_PATH!
echo.

:: Create working folder structure
if not exist "!WORK_FOLDER!\input"      mkdir "!WORK_FOLDER!\input"
if not exist "!WORK_FOLDER!\processing" mkdir "!WORK_FOLDER!\processing"
if not exist "!WORK_FOLDER!\output"     mkdir "!WORK_FOLDER!\output"

:: ── Deactivate any active venv ────────────────────────────────────────────────
if defined VIRTUAL_ENV (
    echo  Deactivating active virtual environment: %VIRTUAL_ENV%
    call deactivate
)

:: ── Check Python 3.12 ─────────────────────────────────────────────────────────
py -3.12 --version >nul 2>&1
if errorlevel 1 (
    echo ERROR: Python 3.12 not found.
    echo        Install it with: winget install -e --id Python.Python.3.12
    exit /b 1
)

:: ── Reject an ARM64 Python interpreter ────────────────────────────────────────
:: torch has had native win_arm64 wheels since 2.7.0, but torchaudio (also
:: required here) has never published win_arm64 wheels — only win_amd64. An
:: ARM64 Python interpreter can therefore never satisfy our pinned dependencies;
:: pip would fail deep into the install with a cryptic "No matching distribution
:: found for torch==2.5.1". Catch it here instead, before that happens.
for /f "delims=" %%i in ('py -3.12 -c "import platform; print(platform.machine())" 2^>nul') do set PY_ARCH=%%i
if /i "!PY_ARCH!"=="ARM64" (
    echo ERROR: Python 3.12 is the ARM64 build — torchaudio has no win_arm64 wheels,
    echo        so this venv can never install successfully on it.
    echo        Install the x64 build instead ^(runs fine via Windows' built-in x64
    echo        emulation^):
    echo          winget uninstall -e --id Python.Python.3.12
    echo          winget install -e --id Python.Python.3.12 --architecture x64
    echo        Then delete any existing venv ^(scripts\uninstall.bat^) and re-run this script.
    exit /b 1
)

:: ── Detect NVIDIA GPU / CUDA (pass --cuda or --cpu to override) ──────────────
set MODE=cpu
if /i "%~1"=="--cuda" set MODE=cuda
if /i "%~1"=="--cpu"  set MODE=cpu
if "%~1"=="" (
    where nvidia-smi >nul 2>&1
    if not errorlevel 1 (
        nvidia-smi >nul 2>&1
        if not errorlevel 1 set MODE=cuda
    )
)

if "!MODE!"=="cuda" (
    echo  Hardware       : NVIDIA GPU detected ^(nvidia-smi^) — installing CUDA build
) else (
    echo  Hardware       : No CUDA GPU detected — installing CPU build
    echo                   ^(pass --cuda to force the CUDA build, e.g. after a driver install^)
)
echo.

echo [1/7] Installing FFmpeg ^(system dependency^)...
for /f "delims=" %%i in ('powershell -NoProfile -Command ^
    "(Get-Content '%DEPSFILE%' -Raw | ConvertFrom-Json).system.ffmpeg.winget"') do set FFMPEG_PKG=%%i
winget install -e --id !FFMPEG_PKG!
if errorlevel 1 ( echo ERROR: Failed to install FFmpeg. Make sure winget is available. & exit /b 1 )

echo [2/7] Creating virtual environment "rb.video.translator" in working folder...
py -3.12 -m venv "!VENV_PATH!"
if errorlevel 1 ( echo ERROR: Failed to create virtual environment. & exit /b 1 )

echo [3/7] Activating environment...
call "!VENV_PATH!\Scripts\activate.bat"
if errorlevel 1 ( echo ERROR: Failed to activate virtual environment. & exit /b 1 )

echo [4/7] Upgrading pip...
python -m pip install --upgrade pip --quiet

echo [5/7] Installing PyTorch 2.5.1 ^(!MODE!^) + Demucs...
echo        ^(This can take several minutes - PyTorch is a large download^)
echo        ^(Pinned to 2.5.1 — 2.6+ requires torchcodec which has no Windows build^)
set TORCH_PKGS=
for /f "delims=" %%i in ('powershell -NoProfile -Command ^
    "(Get-Content '%DEPSFILE%' -Raw | ConvertFrom-Json).!MODE!.torch -join ' ' "') do set TORCH_PKGS=%%i
set TORCH_INDEX=
for /f "delims=" %%i in ('powershell -NoProfile -Command ^
    "(Get-Content '%DEPSFILE%' -Raw | ConvertFrom-Json).!MODE!.indexUrl"') do set TORCH_INDEX=%%i
pip install !TORCH_PKGS! --index-url !TORCH_INDEX!
if errorlevel 1 ( echo ERROR: Failed to install PyTorch. & exit /b 1 )
pip install soundfile
if errorlevel 1 ( echo ERROR: Failed to install soundfile. & exit /b 1 )
pip install demucs
if errorlevel 1 ( echo ERROR: Failed to install Demucs. & exit /b 1 )

echo        Patching torchaudio to fall back to soundfile ^(no torchcodec build for Windows^)...
python "%~dp0patch_torchaudio.py"
if errorlevel 1 echo        WARNING: torchaudio patch did not apply — continuing anyway.

echo [6/7] Installing WhisperX ^(transcription + speaker diarization^)...
echo        ^(whisperx pinned to 3.4.2, pyannote-audio to 3.4.0, speechbrain to 1.0.3 —
echo        see scripts\dependencies.json "commonNotes" for why^)
set COMMON_PKGS=
for /f "delims=" %%i in ('powershell -NoProfile -Command ^
    "(Get-Content '%DEPSFILE%' -Raw | ConvertFrom-Json).common -join ' ' "') do set COMMON_PKGS=%%i

if "!MODE!"=="cuda" (
    echo        ^(+ CUDA runtime libs: nvidia-cublas-cu12, nvidia-cudnn-cu12 — see
    echo        scripts\dependencies.json "cuda.notes" for why^)
    set EXTRA_PKGS=
    for /f "delims=" %%i in ('powershell -NoProfile -Command ^
        "(Get-Content '%DEPSFILE%' -Raw | ConvertFrom-Json).cuda.extra -join ' ' "') do set EXTRA_PKGS=%%i
    pip install !COMMON_PKGS! !EXTRA_PKGS!
) else (
    pip install !COMMON_PKGS!
)
if errorlevel 1 ( echo ERROR: Failed to install WhisperX. & exit /b 1 )

echo [7/7] Caching HuggingFace token for speaker diarization...
if not "!HF_TOKEN_VALUE!"=="" (
    :: huggingface-cli is deprecated in favour of `hf`, and its deprecation-notice
    :: emoji crashes with UnicodeEncodeError on the default cp1252 console — force UTF-8.
    set PYTHONUTF8=1
    hf auth login --token "!HF_TOKEN_VALUE!"
    if errorlevel 1 ( echo WARNING: HuggingFace login failed — check the token in appsettings.json. ) else ( echo  HuggingFace token cached. )
) else (
    echo        Skipped — RBVideoTranslator.HfToken is empty in appsettings.json.
    echo        Set it and re-run this script, or set the HF_TOKEN env var manually.
)

:: ── Write VenvPath back to appsettings.json ───────────────────────────────────
echo.
echo  Updating VenvPath in appsettings.json...
powershell -NoProfile -Command ^
    "$f = '%APPSETTINGS%'; $j = Get-Content $f -Raw | ConvertFrom-Json; $j.RBVideoTranslator.VenvPath = '!VENV_PATH!'; $j | ConvertTo-Json -Depth 10 | Set-Content $f -Encoding UTF8"
if errorlevel 1 ( echo WARNING: Could not update VenvPath in appsettings.json — set it manually. ) else ( echo  VenvPath updated. )

echo.
echo ============================================================
echo  Done! ^(mode: !MODE!^)
echo ============================================================
echo.
echo  Verify the installation:
echo    "!VENV_PATH!\Scripts\activate"
echo    python -c "import whisperx; print('WhisperX OK')"
echo    python -c "import librosa; print('librosa OK')"
echo    python -c "import torch; print('CUDA available:', torch.cuda.is_available())"
echo    python -c "import demucs; print('Demucs OK')"
echo    ffmpeg -version
echo.
if "!MODE!"=="cuda" (
    echo  NOTE: cu124 requires CUDA 12.4+ drivers ^(Game Ready 550+ / Studio 555+^).
    echo        Runs fine on newer hardware ^(12.6, 12.8^) — CUDA is backward-compatible.
    echo        For older drivers visit https://pytorch.org/get-started/locally/
    echo        to get the correct --index-url for your driver version.
    echo.
)
echo  Speaker diarization needs a HuggingFace token ^(one-time setup^):
echo    1. Accept terms at https://huggingface.co/pyannote/speaker-diarization-3.1
echo    2. Accept terms at https://huggingface.co/pyannote/segmentation-3.0
echo    3. Create a read-scoped token at https://huggingface.co/settings/tokens
echo    4. Put it in RBVideoTranslator.HfToken in appsettings.json, then re-run this script
echo.
echo  Force a specific build regardless of detected hardware:
echo    scripts\install.bat --cuda
echo    scripts\install.bat --cpu
echo ============================================================

endlocal
