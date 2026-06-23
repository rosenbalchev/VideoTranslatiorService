@echo off
setlocal

echo ============================================================
echo  AI Video Translator - Python environment setup (CPU only)
echo ============================================================
echo.
echo  NOTE: Running on CPU is supported but significantly slower.
echo        Whisper transcription and Demucs separation both
echo        benefit greatly from a CUDA-capable NVIDIA GPU.
echo        Use install-cuda.bat if you have one.
echo.

where python >nul 2>&1
if errorlevel 1 (
    echo ERROR: Python not found on PATH.
    echo        Download it from https://www.python.org/downloads/
    echo        Make sure to check "Add Python to PATH" during install.
    exit /b 1
)

echo [1/5] Creating virtual environment "bgtts-env"...
python -m venv bgtts-env
if errorlevel 1 ( echo ERROR: Failed to create virtual environment. & exit /b 1 )

echo [2/5] Activating environment...
call bgtts-env\Scripts\activate.bat
if errorlevel 1 ( echo ERROR: Failed to activate virtual environment. & exit /b 1 )

echo [3/5] Upgrading pip...
python -m pip install --upgrade pip --quiet

echo [4/5] Installing Whisper...
pip install openai-whisper
if errorlevel 1 ( echo ERROR: Failed to install Whisper. & exit /b 1 )

echo [5/5] Installing PyTorch ^(CPU^) + Demucs...
pip install torch torchvision torchaudio
if errorlevel 1 ( echo ERROR: Failed to install PyTorch. & exit /b 1 )
pip install demucs
if errorlevel 1 ( echo ERROR: Failed to install Demucs. & exit /b 1 )

echo.
echo ============================================================
echo  Done!
echo ============================================================
echo.
echo  Verify the installation:
echo    bgtts-env\Scripts\activate
echo    python -c "import whisper; print('Whisper OK')"
echo    python -c "import torch; print('PyTorch OK')"
echo    python -c "import demucs; print('Demucs OK')"
echo.
echo  Pass --venv bgtts-env to the CLI to use this environment.
echo ============================================================

endlocal
