@echo off
setlocal enabledelayedexpansion

echo ============================================================
echo  RB.VideoTranslator - ONNX model export (optional)
echo ============================================================
echo.
echo  This is a separate, optional step run AFTER install.bat. It exports
echo  Whisper to ONNX so transcription can run natively in C# (DirectML GPU,
echo  falling back to CPU) instead of CTranslate2's CUDA-or-CPU-only backend.
echo  Diarization is unaffected - it keeps running via whisperx/torch either way.
echo.

:: -- Locate config files and venv, same convention as install.bat ------------
set APPSETTINGS=%~dp0..\RB.VideoTranslator.CLI\appsettings.json
set DEPSFILE=%~dp0dependencies.json

if not exist "%APPSETTINGS%" (
    echo ERROR: appsettings.json not found at:
    echo        %APPSETTINGS%
    exit /b 1
)
if not exist "%DEPSFILE%" (
    echo ERROR: dependencies.json not found at:
    echo        %DEPSFILE%
    exit /b 1
)

for /f "delims=" %%i in ('powershell -NoProfile -Command ^
    "(Get-Content '%APPSETTINGS%' -Raw | ConvertFrom-Json).RBVideoTranslator.VenvPath"') do (
    set VENV_PATH=%%i
)

if "!VENV_PATH!"=="" (
    echo ERROR: VenvPath is empty in appsettings.json - run install.bat first.
    exit /b 1
)

set VENV_PYTHON=!VENV_PATH!\Scripts\python.exe
if not exist "!VENV_PYTHON!" (
    echo ERROR: Python not found in venv at:
    echo        !VENV_PYTHON!
    echo        Run install.bat first to create the venv.
    exit /b 1
)

echo  Venv           : !VENV_PATH!
echo.

"!VENV_PYTHON!" "%~dp0export_onnx_models.py" "%APPSETTINGS%" "%DEPSFILE%"
if errorlevel 1 ( echo ERROR: ONNX export failed. & exit /b 1 )

echo.
echo ============================================================
echo  Done!
echo ============================================================
