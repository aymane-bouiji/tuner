# Tuner

An IPTV player for Xtream Codes accounts: live TV with a programme guide, movies and series,
built-in subtitles and audio languages.

| Folder | What it is |
| --- | --- |
| `desktop/` | **Tuner for Windows** — a native app (WPF + WebView2 + VLC's engine). Download it from this repo's **Releases** page. |
| `web/` | The browser version with its small local relay (`node server.js`). See `web/README.md`. |
| `tests/mock/` | A fake IPTV provider and sample videos used by the automatic Windows build. |

Every push to `main` builds the Windows app on GitHub's Windows machines, runs a self-test against
the fake provider (sign in, live TV, an MKV movie with AC-3 audio and two subtitle tracks, seeking,
resume) and publishes `Tuner-Windows.zip` as a release when everything passes.
