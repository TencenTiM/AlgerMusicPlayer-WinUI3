@echo off
setlocal
pushd "%~dp0"
if errorlevel 1 (
    echo Failed to open the project directory.
    pause
    exit /b 1
)
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-installer.ps1"
set "BuildExitCode=%ERRORLEVEL%"
popd
echo.
if "%BuildExitCode%"=="0" (
    echo Build completed. Output: dist
) else (
    echo Build failed. Exit code: %BuildExitCode%
)
pause
exit /b %BuildExitCode%
