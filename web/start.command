#!/bin/bash
cd "$(dirname "$0")"
command -v node >/dev/null || { echo "Node.js is not installed. Get it from https://nodejs.org"; read -r; exit 1; }
if ! command -v ffmpeg >/dev/null && [ ! -d node_modules/ffmpeg-static ]; then
  echo "Downloading FFmpeg for built-in subtitles (first run only, about 80 MB)..."
  npm install --no-audit --no-fund
fi
(sleep 1; open http://localhost:8080) &
node server.js
