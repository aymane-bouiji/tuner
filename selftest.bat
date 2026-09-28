@echo off
setlocal
cd /d "%~dp0"
title Tuner self-test
if not exist dist\Tuner\Tuner.exe goto nobuild
where node >nul 2>nul
if errorlevel 1 goto nonode
echo.
echo   Running Tuner's self-test - about one minute.
echo   It uses a fake IPTV server and a separate profile; your own sign-in is not touched.
echo   Please don't touch the mouse or keyboard until it finishes.
echo.
start "Tuner fake provider" /min node tests\mock\panel.js
timeout /t 2 /nobreak >nul
if exist selftest-results rmdir /s /q selftest-results
mkdir selftest-results
start "" /wait "dist\Tuner\Tuner.exe" --selftest --out "%cd%\selftest-results"
taskkill /fi "WINDOWTITLE eq Tuner fake provider*" /f >nul 2>nul
copy /y "%LOCALAPPDATA%\Tuner\selftest\tuner.log" selftest-results\ >nul 2>nul
echo   Finished. Results are in the selftest-results folder.
start "" explorer selftest-results
if exist selftest-results\result.json start "" notepad selftest-results\result.json
exit /b 0

:nobuild
echo   Build Tuner first: double-click build.bat.
pause
exit /b 1

:nonode
echo   The self-test needs Node.js - the same one the web version uses. Get it from https://nodejs.org
pause
exit /b 1
