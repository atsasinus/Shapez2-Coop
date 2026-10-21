@echo off
setlocal
echo Starting two isolated Shapez 2 Co-op windows...
echo First window: create a factory, then Esc - Multiplayer - HOST.
echo Second window: Multiplayer - connect to 127.0.0.1.
echo Each launch creates fresh test profiles; previous sessions stay in local\qa.
echo.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0mod\tools\start-local-play.ps1"
if errorlevel 1 (
    echo.
    echo Launch failed. See the error above. Close existing test windows before retrying.
    pause
    exit /b 1
)
echo.
echo Both windows started. You can close this console.
timeout /t 8 >nul
