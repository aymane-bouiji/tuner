# Tuner — web IPTV player (Xtream Codes)

Live TV with a programme guide, Movies and Series, in your browser.

## Run it

1. Install **Node.js 18 or newer** from https://nodejs.org (the LTS version is fine).
2. Start Tuner:
   - **Windows:** double-click `start.bat`
   - **Mac:** double-click `start.command` (first time: right-click → Open)
   - **Anything else:** open a terminal in this folder and run `node server.js`
3. Open **http://localhost:8080** in Chrome, Edge or Safari.
4. Sign in with your provider's server URL, username and password.
   You can also paste your full M3U link into the server box; it fills in the login for you.

Click **Try the demo channels** on the sign-in screen to check that everything works before using your own account.

## Subtitles and audio languages

Click the **subtitles button** (bottom-right of the player, or press **C**).

- **Movies and series:** the subtitle and audio tracks inside the file show up in that menu with their language names (English, Français, العربية…), just like in UHF. Tuner remembers the language you pick and turns it on for the next movie or episode.
- **Live TV:** subtitle tracks sent with the channel show up in the same menu.
- **Your own file:** *Load subtitle file…* adds an `.srt`, `.vtt` or `.ass` file, or drag it onto the video. Use the timing buttons if it's out of sync.
- Picture-based subtitles (PGS / DVD, found on some Blu-ray rips) can't be shown yet.

### This needs FFmpeg

Subtitles and audio tracks inside movie files are read by **FFmpeg**, a free video tool. FFmpeg also makes MKV files and cinema sound (AC-3, DTS) play in the browser.

- **Easiest:** `start.bat` / `start.command` download a private copy into this folder on first run (about 80 MB, no admin rights needed). You can also run `npm install` here yourself.
- Already have FFmpeg installed (`winget install ffmpeg`, `brew install ffmpeg`)? Tuner finds it automatically.
- The Docker image includes it.

When Tuner starts, it prints whether FFmpeg was found. Without it, everything else still works; movies just play without their built-in subtitles.

### How it works

For a movie that has subtitles or extra audio tracks, FFmpeg on your computer reads the file **once** from your provider and sends the picture, the sound and every subtitle track to the player separately. That's why the player has its own seek bar: jumping to a new time restarts the stream from that point. Plain MP4 files with nothing extra inside still go straight to the browser.

## Run with Docker (always-on machine: NAS, home server, Raspberry Pi)

```
docker compose up -d
```

The image includes FFmpeg, so built-in subtitles work out of the box.

Then open `http://<that machine's IP>:8080` from any device on your network. Keep it on your home network: anyone who can reach port 8080 can use the relay.

## Why is there a server?

Browsers block web pages from talking to most IPTV servers directly. `server.js` is a tiny relay that runs on your computer and fetches the channel lists and streams for the page. It has no dependencies and sends nothing anywhere except your IPTV server.

## Keyboard

| Key | Action |
| --- | --- |
| ↑ / ↓ | Previous / next channel |
| F | Full screen |
| M | Mute |
| / | Search |
| Space | Pause (movies and episodes) |
| ← / → | Skip 10 seconds (movies and episodes) |
| C | Subtitles on / off |
| Esc | Close |

## Options

- Different port: `PORT=9000 node server.js` (Windows: `set PORT=9000 && node server.js`)
- Use it from your phone or TV browser on the same Wi-Fi: `HOST=0.0.0.0 node server.js`, then open `http://<your computer's IP>:8080`. Only do this on a network you trust: anyone on it can use the relay.
- If your provider rejects the connection, try a different player identity: `IPTV_UA="okhttp/4.9.0" node server.js`

## If a channel won't play

- Tuner tries the HLS version of each channel first, then the MPEG-TS version.
- A few live channels use formats browsers can't decode (for example some HEVC/H.265 channels). Use **Copy link** and open it in VLC.
- Movies in unusual formats are converted by FFmpeg. Old formats (like DivX/AVI) and HEVC on browsers without HEVC support are re-encoded, which needs a reasonably fast computer.
