TUNER FOR WINDOWS
=================

Start:  double-click Tuner.exe  (keep all the files in this folder together).

The first time, Windows may show "Windows protected your PC" because the app
isn't signed by a company. Click "More info", then "Run anyway".

Sign in with your provider's server URL, username and password, same as the
web version. You can also paste your full M3U link into the server box.

Keys
  Up / Down        change channel (live TV)
  F or double-click   full screen
  Space            pause
  Left / Right     back / forward 10 seconds (movies and episodes)
  Up / Down        volume (movies and episodes)
  M                mute
  C                subtitles on / off
  N                next episode
  Esc              leave full screen / close the player

Subtitles and audio languages
  Click the subtitles button on the player. Tuner remembers the languages you
  pick. "Load subtitle file..." adds an .srt / .ass file from your PC.

If something goes wrong
  A log is kept in  %LOCALAPPDATA%\Tuner\tuner.log  (usernames and passwords are
  removed from it). Settings are in  %LOCALAPPDATA%\Tuner\settings.json:
    LiveCaching   milliseconds buffered on live TV (lower = faster channel changes,
                  higher = fewer stalls on a weak connection). Default 1200.
    VodCaching    same for movies. Default 2000.
    UserAgent     how Tuner identifies itself to your provider.

  If Tuner says WebView2 is missing, install it from Microsoft (the app shows a
  download button). It's already on most Windows 10 and 11 PCs.
