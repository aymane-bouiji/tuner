@echo off
setlocal
cd /d "%~dp0"
title Building Tuner for Windows
echo.
echo   Building Tuner for Windows...
echo.
where dotnet >nul 2>nul
if errorlevel 1 goto nosdk
dotnet --list-sdks | findstr /b "8." >nul
if errorlevel 1 goto nosdk
goto build

:nosdk
echo   Microsoft .NET 8 SDK is needed once to build Tuner - free, about 250 MB.
where winget >nul 2>nul
if errorlevel 1 goto manual
echo   Installing it now...
echo.
winget install --id Microsoft.DotNet.SDK.8 -e --accept-source-agreements --accept-package-agreements
echo.
echo   Done. Close this window and double-click build.bat again.
pause
exit /b 0

:manual
echo   Download "SDK 8 - Windows x64" from the page that opens, install it,
echo   then double-click build.bat again.
start "" https://dotnet.microsoft.com/download/dotnet/8.0
pause
exit /b 1

:build
echo   This takes 1 to 3 minutes the first time - it downloads the VLC engine and WebView2.
echo.
dotnet publish desktop\Tuner.Desktop.csproj -c Release -r win-x64 --self-contained true -p:DebugType=none -o dist\Tuner > build-log.txt 2>&1
if errorlevel 1 goto failed
copy /y desktop\README-Windows.txt dist\Tuner\README.txt >nul
echo   Done! Tuner is in the folder dist\Tuner - starting it now.
start "" "dist\Tuner\Tuner.exe"
start "" explorer dist\Tuner
exit /b 0

:failed
echo   The build failed. The details are in build-log.txt - send that file to Claude.
start "" notepad build-log.txt
pause
exit /b 1
