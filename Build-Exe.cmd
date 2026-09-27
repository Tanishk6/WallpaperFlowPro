@echo off
rem Double-click to build WallpaperFlow Pro on Windows.
rem Produces artifacts\installer\WallpaperFlowPro-Setup-1.0.0.exe, artifacts\WallpaperFlowPro-Portable-1.0.0.zip
rem and the runnable app in artifacts\publish. The RESULT box at the end says what was created.
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\build-exe.ps1" %*
set RESULT=%errorlevel%
if exist "%~dp0artifacts" start "" explorer "%~dp0artifacts"
echo.
pause
exit /b %RESULT%
