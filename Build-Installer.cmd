@echo off
rem Builds only the installer from an existing artifacts\publish folder (run Build-Exe.cmd first).
setlocal
cd /d "%~dp0"
set ISCC=
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe
if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe
if "%ISCC%"=="" (echo Inno Setup 6 is not installed. Run Build-Exe.cmd first. & pause & exit /b 1)
if not exist "artifacts\publish\WallpaperFlowPro.exe" (echo artifacts\publish is missing. Run Build-Exe.cmd first. & pause & exit /b 1)
"%ISCC%" /DAppVersion=1.0.0 installer\WallpaperFlow.iss
if errorlevel 1 (echo. & echo Installer build FAILED. Send the error above. & pause & exit /b 1)
echo.
echo Installer created: artifacts\installer\WallpaperFlowPro-Setup-1.0.0.exe
start "" explorer "%~dp0artifacts\installer"
pause
