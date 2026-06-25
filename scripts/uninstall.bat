@echo off
setlocal

echo ============================================================
echo  AI Video Translator - Remove Python virtual environment
echo ============================================================
echo.
echo  This will delete the "bgtts-env" folder and all Python
echo  packages installed inside it.  FFmpeg and system Python
echo  are NOT touched.
echo.

if not exist bgtts-env (
    echo  "bgtts-env" not found - nothing to remove.
    goto :done
)

set /p CONFIRM=  Type YES to confirm deletion:
if /i not "%CONFIRM%"=="YES" (
    echo  Cancelled.
    exit /b 0
)

echo.
echo  Removing bgtts-env...
rmdir /s /q bgtts-env
if errorlevel 1 (
    echo ERROR: Could not delete bgtts-env.
    echo        Make sure no terminal has the environment activated.
    exit /b 1
)

echo  Done.  Run install-cuda.bat or install-cpu.bat to reinstall.

:done
echo.
endlocal
