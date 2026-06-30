@echo off
setlocal enabledelayedexpansion

echo ============================================================
echo  RB.VideoTranslator - Python environment setup (CPU only)
echo ============================================================
echo.
echo  NOTE: Running on CPU is supported but significantly slower.
echo        Whisper transcription and Demucs separation both
echo        benefit greatly from a CUDA-capable NVIDIA GPU.
echo        Use install-cuda.bat if you have one.
echo.

:: ── Read WorkingFolderPath from appsettings.json ─────────────────────────────
set APPSETTINGS=%~dp0..\RB.VideoTranslator.CLI\appsettings.json
if not exist "%APPSETTINGS%" (
    echo ERROR: appsettings.json not found at:
    echo        %APPSETTINGS%
    echo.
    echo  Set WorkingFolderPath in that file before running this script.
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

echo [1/6] Installing FFmpeg ^(system dependency^)...
winget install -e --id Gyan.FFmpeg.Shared
if errorlevel 1 ( echo ERROR: Failed to install FFmpeg. Make sure winget is available. & exit /b 1 )

echo [2/6] Creating virtual environment "rb.video.translator" in working folder...
py -3.12 -m venv "!VENV_PATH!"
if errorlevel 1 ( echo ERROR: Failed to create virtual environment. & exit /b 1 )

echo [3/6] Activating environment...
call "!VENV_PATH!\Scripts\activate.bat"
if errorlevel 1 ( echo ERROR: Failed to activate virtual environment. & exit /b 1 )

echo [4/6] Upgrading pip...
python -m pip install --upgrade pip --quiet

echo [5/6] Installing PyTorch 2.5.1 ^(CPU^) + Demucs...
echo        ^(This can take several minutes - PyTorch is a large download^)
echo        ^(Pinned to 2.5.1 — 2.6+ requires torchcodec which has no Windows build^)
pip install torch==2.5.1 torchvision==0.20.1 torchaudio==2.5.1 --index-url https://download.pytorch.org/whl/cpu
if errorlevel 1 ( echo ERROR: Failed to install PyTorch. & exit /b 1 )
pip install soundfile
if errorlevel 1 ( echo ERROR: Failed to install soundfile. & exit /b 1 )
pip install demucs
if errorlevel 1 ( echo ERROR: Failed to install Demucs. & exit /b 1 )

echo [6/6] Installing faster-whisper...
pip install faster-whisper
if errorlevel 1 ( echo ERROR: Failed to install faster-whisper. & exit /b 1 )

:: ── Write VenvPath back to appsettings.json ───────────────────────────────────
echo.
echo  Updating VenvPath in appsettings.json...
powershell -NoProfile -Command ^
    "$f = '%APPSETTINGS%'; $j = Get-Content $f -Raw | ConvertFrom-Json; $j.RBVideoTranslator.VenvPath = '!VENV_PATH!'; $j | ConvertTo-Json -Depth 10 | Set-Content $f -Encoding UTF8"
if errorlevel 1 ( echo WARNING: Could not update VenvPath in appsettings.json — set it manually. ) else ( echo  VenvPath updated. )

echo.
echo ============================================================
echo  Done!
echo ============================================================
echo.
echo  Verify the installation:
echo    "!VENV_PATH!\Scripts\activate"
echo    python -c "import faster_whisper; print('faster-whisper OK')"
echo    python -c "import torch; print('PyTorch OK')"
echo    python -c "import demucs; print('Demucs OK')"
echo    ffmpeg -version
echo ============================================================

endlocal
