@echo off
cd /d "%~dp0"
where node >nul 2>nul || (echo Node.js is not installed. Get it from https://nodejs.org & pause & exit /b)
where ffmpeg >nul 2>nul
if errorlevel 1 if not exist node_modules\ffmpeg-static (
  echo Downloading FFmpeg for built-in subtitles ^(first run only, about 80 MB^)...
  call npm install --no-audit --no-fund
)
start "" http://localhost:8080
node server.js
pause
