@echo off
setlocal enabledelayedexpansion

echo ============================================================
echo  RB.VideoTranslator - Remove Python virtual environment
echo ============================================================
echo.
echo  This deletes the Python virtual environment and all packages
echo  installed inside it. FFmpeg and system Python are NOT touched.
echo.

:: ── Read VenvPath (falling back to WorkingFolderPath) from appsettings.json ──
set APPSETTINGS=%~dp0..\RB.VideoTranslator.CLI\appsettings.json
if not exist "%APPSETTINGS%" (
    echo ERROR: appsettings.json not found at:
    echo        %APPSETTINGS%
    exit /b 1
)

for /f "delims=" %%i in ('powershell -NoProfile -Command ^
    "(Get-Content '%APPSETTINGS%' -Raw | ConvertFrom-Json).RBVideoTranslator.VenvPath"') do (
    set VENV_PATH=%%i
)

if "!VENV_PATH!"=="" (
    for /f "delims=" %%i in ('powershell -NoProfile -Command ^
        "(Get-Content '%APPSETTINGS%' -Raw | ConvertFrom-Json).RBVideoTranslator.WorkingFolderPath"') do (
        set WORK_FOLDER=%%i
    )
    if "!WORK_FOLDER!"=="" (
        echo ERROR: Both VenvPath and WorkingFolderPath are empty in appsettings.json.
        echo        Nothing to remove.
        exit /b 1
    )
    set VENV_PATH=!WORK_FOLDER!\rb.video.translator
    echo  VenvPath was empty — falling back to default: !VENV_PATH!
)

echo  Virtual env: !VENV_PATH!
echo.

if not exist "!VENV_PATH!" (
    echo  "!VENV_PATH!" not found - nothing to remove.
    goto :done
)

if defined VIRTUAL_ENV (
    echo ERROR: A virtual environment is currently activated ^(%VIRTUAL_ENV%^).
    echo        Run "deactivate" first, then re-run this script.
    exit /b 1
)

set /p CONFIRM=  Type YES to confirm deletion:
if /i not "%CONFIRM%"=="YES" (
    echo  Cancelled.
    exit /b 0
)

echo.
echo  Removing !VENV_PATH!...
rmdir /s /q "!VENV_PATH!"
if errorlevel 1 (
    echo ERROR: Could not delete "!VENV_PATH!".
    echo        Make sure no terminal has the environment activated.
    exit /b 1
)

echo  Clearing VenvPath in appsettings.json...
powershell -NoProfile -Command ^
    "$f = '%APPSETTINGS%'; $j = Get-Content $f -Raw | ConvertFrom-Json; $j.RBVideoTranslator.VenvPath = ''; $j | ConvertTo-Json -Depth 10 | Set-Content $f -Encoding UTF8"
if errorlevel 1 ( echo WARNING: Could not clear VenvPath in appsettings.json — clear it manually. )

echo  Done. Run install.bat to reinstall.

:done
echo.
endlocal
