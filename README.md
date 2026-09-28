# Tuner

An IPTV player for Xtream Codes accounts: live TV with a programme guide, movies and series,
subtitles and audio languages built into the stream.

## Tuner for Windows

1. Double-click **build.bat**.
   - The first time, it installs Microsoft's free .NET 8 build tools (about 250 MB) if they're missing.
     Close the window when it says so and double-click build.bat again.
   - Building takes 1–3 minutes the first time (it downloads VLC's video engine).
2. Tuner opens by itself when the build finishes. From then on, open **dist\Tuner\Tuner.exe**
   (you can move the whole `dist\Tuner` folder anywhere, or pin Tuner.exe to the taskbar).

If the build fails, it opens `build-log.txt` — send that file to Claude.

**Optional check:** double-click **selftest.bat**. It signs in to a fake IPTV server with a separate
profile (your own sign-in isn't touched), plays a live channel and an MKV movie with two subtitle
tracks, and saves screenshots and `result.json` in `selftest-results`. Handy to send if something
misbehaves.

## Folders

| Folder | What it is |
| --- | --- |
| `desktop/` | Tuner for Windows: WPF window, WebView2 for the interface, VLC's engine (libVLC) for video. |
| `web/` | The browser version with its local relay (`node server.js`). See `web/README.md`. |
| `tests/mock/` | Fake IPTV provider and sample videos used by the self-test. |
| `.github/` | Automatic build on GitHub's Windows machines, if you put this in a GitHub repository. |
