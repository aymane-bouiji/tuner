using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace Tuner
{
    public enum PlayerMode { Hidden, Inline, Full, FullScreen }

    public class Track
    {
        public int Id;
        public string Name;   // what we show ("English", "French · Forced")
        public string Lang;   // what we remember as a preference ("English")
        public bool Active;
    }

    /// <summary>
    /// Plays everything with VLC's engine. The picture goes into its own borderless window that sits
    /// exactly over the page's video area (live TV) or over the whole window (movies, full screen).
    /// A transparent window on top of it carries the controls.
    /// </summary>
    public class Player
    {
        readonly MainWindow main;
        readonly AppSettings cfg;
        public readonly LibVLC Vlc;
        public readonly MediaPlayer Mp;
        readonly VideoWindow video;
        public readonly ControlsWindow Controls;

        public PlayerMode Mode { get; private set; } = PlayerMode.Hidden;
        public bool IsLive { get; private set; }
        public string Title { get; private set; } = "";
        public bool HasNext { get; private set; }
        public bool Started { get; private set; }
        public bool Failed { get; private set; }
        public string LastError { get; private set; } = "";
        public bool VideoVisible => video.IsVisible;
        public long TimeMs => lastTimeMs;
        public long LengthMs => lastLenMs;
        public string CurrentUrl => urlIndex < urls.Length ? urls[urlIndex] : null;

        string[] urls = Array.Empty<string>();
        int urlIndex, gen;
        long resumeMs, lastTimeMs, lastLenMs;
        bool liveRetried, userPickedTracks;
        CssRect inlineRect;
        bool inlineVisible;
        PlayerMode beforeFullScreen = PlayerMode.Full;

        readonly DispatcherTimer watchdog, tick, tracksDebounce;
        readonly object chainLock = new object();
        Task chain = Task.CompletedTask;
        int vlcErrors;

        public Player(MainWindow main, AppSettings cfg)
        {
            this.main = main;
            this.cfg = cfg;
            Core.Initialize();
            Vlc = new LibVLC("--no-video-title-show", "--no-osd", "--no-snapshot-preview", "--no-stats",
                "--avcodec-hw=any", "--http-reconnect", "--sub-autodetect-file");
            Vlc.Log += (s, e) =>
            {
                if (e.Level == LogLevel.Error && vlcErrors++ < 400) App.Log("vlc: " + e.Module + ": " + e.Message);
            };
            Mp = new MediaPlayer(Vlc) { EnableHardwareDecoding = true, EnableKeyInput = false, EnableMouseInput = false };

            video = new VideoWindow(main);
            Mp.Hwnd = video.Handle;
            Controls = new ControlsWindow(this, video);

            Mp.Playing += (s, e) => Ui(OnPlaying);
            Mp.Paused += (s, e) => Ui(() => Controls.UpdatePlayState());
            Mp.EncounteredError += (s, e) => Ui(() => Fallback("The stream could not be opened. The channel may be offline, or your provider refused the connection."));
            Mp.EndReached += (s, e) => Ui(OnEnd);
            Mp.Buffering += (s, e) => { float c = e.Cache; Ui(() => Controls.SetBuffering(Started ? c : 100)); };
            Mp.ESAdded += (s, e) => Ui(TracksChangedSoon);
            Mp.ESDeleted += (s, e) => Ui(TracksChangedSoon);
            Mp.ESSelected += (s, e) => Ui(TracksChangedSoon);

            watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            watchdog.Tick += (s, e) => { watchdog.Stop(); if (!Started && Mode != PlayerMode.Hidden) Fallback("No picture after 20 seconds."); };

            tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            tick.Tick += (s, e) =>
            {
                if (!Started || Mode == PlayerMode.Hidden) return;
                long t = Mp.Time, d = Mp.Length;
                if (t > 0) lastTimeMs = t;
                if (d > 0) lastLenMs = d;
                if (!IsLive) main.Post(new { ev = "vod.time", t = lastTimeMs / 1000.0, d = lastLenMs / 1000.0 });
            };
            tick.Start();

            tracksDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            tracksDebounce.Tick += (s, e) => { tracksDebounce.Stop(); SendTracks(); Controls.RefreshTracks(); };
        }

        static void Ui(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

        /// <summary>VLC calls that can block (opening, stopping) run one after another off the UI thread.</summary>
        void Run(Action a)
        {
            lock (chainLock)
                chain = chain.ContinueWith(_ => { try { a(); } catch (Exception ex) { App.Log("vlc call: " + ex); } }, TaskScheduler.Default);
        }

        // ---------- starting and stopping ----------
        public void PlayLive(string[] u, string title, CssRect rect)
        {
            IsLive = true; Title = title; HasNext = false;
            inlineRect = rect; inlineVisible = true; liveRetried = false;
            if (Mode == PlayerMode.Hidden) Mode = PlayerMode.Inline;
            Start(u, 0);
        }

        public void PlayVod(string[] u, string title, double resumeAtSeconds, bool hasNext)
        {
            IsLive = false; Title = title; HasNext = hasNext;
            if (Mode != PlayerMode.FullScreen) Mode = PlayerMode.Full;
            Start(u, (long)(resumeAtSeconds * 1000));
            Controls.ActivateSoon();
        }

        void Start(string[] u, long resume)
        {
            urls = u ?? Array.Empty<string>();
            urlIndex = 0;
            resumeMs = resume;
            lastTimeMs = resume; lastLenMs = 0;
            userPickedTracks = false;
            StartCurrent();
        }

        void StartCurrent()
        {
            int g = ++gen;
            Started = false; Failed = false;
            watchdog.Stop(); watchdog.Start();
            Controls.SetTitle(Title, IsLive, HasNext);
            ApplyGeometry();
            Controls.ShowStatus(IsLive ? "Tuning in…" : "Loading…", null, false);
            if (IsLive) main.Post(new { ev = "live.state", state = "loading" });

            var url = CurrentUrl;
            if (url == null) { Fail("There's no stream address for this."); return; }
            Uri uri;
            try { uri = new Uri(url); } catch { Fail("The stream address isn't valid."); return; }

            var opts = new List<string>
            {
                ":http-user-agent=" + cfg.UserAgent,
                ":network-caching=" + (IsLive ? cfg.LiveCaching : cfg.VodCaching),
                ":http-reconnect",
            };
            if (resumeMs > 5000) opts.Add(":start-time=" + (resumeMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture));
            App.Log((IsLive ? "live" : "vod") + " open #" + (urlIndex + 1) + ": " + Redact(url));

            Run(() =>
            {
                if (g != gen) return;
                using var media = new Media(Vlc, uri, opts.ToArray());
                Mp.Play(media);
            });
        }

        static string Redact(string url)
        {
            // Keep usernames and passwords out of the log file.
            return Regex.Replace(url ?? "", @"/(live|movie|series)/[^/]+/[^/]+/", "/$1/***/***/");
        }

        void OnPlaying()
        {
            watchdog.Stop();
            bool first = !Started;
            Started = true; Failed = false;
            Controls.HideStatus();
            Controls.UpdatePlayState();
            if (first)
            {
                try { Mp.Volume = cfg.Volume; Mp.SetSpuDelay(0); } catch { }
                if (IsLive) main.Post(new { ev = "live.state", state = "playing" });
                int g = gen;
                // Tracks appear a moment after playback starts, sometimes later on live channels.
                After(700, () => { if (g == gen) ApplyTrackPrefs(); });
                After(2500, () => { if (g == gen) ApplyTrackPrefs(); });
            }
            ApplyGeometry();
        }

        void Fallback(string message)
        {
            if (Mode == PlayerMode.Hidden) return;
            if (!Started && urlIndex + 1 < urls.Length)
            {
                urlIndex++;
                App.Log("trying the next stream address");
                StartCurrent();
                return;
            }
            Fail(message);
        }

        void Fail(string message)
        {
            watchdog.Stop();
            Started = false; Failed = true; LastError = message;
            App.Log("playback failed: " + message);
            gen++;
            Run(() => Mp.Stop());
            Controls.ShowStatus("Couldn't play this", message, true);
            if (IsLive) main.Post(new { ev = "live.state", state = "error", message });
            ApplyGeometry();
        }

        void OnEnd()
        {
            if (Mode == PlayerMode.Hidden) return;
            if (IsLive)
            {
                // Live streams sometimes drop for a moment; reconnect once before giving up.
                if (!liveRetried) { liveRetried = true; urlIndex = 0; StartCurrent(); }
                else Fail("The channel stopped sending video.");
                return;
            }
            if (lastLenMs > 0 && lastTimeMs < lastLenMs - 30000)
            {
                resumeMs = lastTimeMs;
                Fail("The connection to the video dropped.");
                return;
            }
            main.Post(new { ev = "vod.ended" });
            if (!HasNext) CloseVod();
        }

        public void Retry()
        {
            if (urls.Length == 0) return;
            resumeMs = IsLive ? 0 : Math.Max(resumeMs, lastTimeMs);
            urlIndex = 0;
            liveRetried = false;
            StartCurrent();
        }

        public void Stop()
        {
            gen++;
            watchdog.Stop();
            Started = false; Failed = false;
            Mode = PlayerMode.Hidden;
            Controls.ClosePopup();
            ApplyGeometry();
            Run(() => Mp.Stop());
        }

        /// <summary>Close a movie or episode and tell the page where we stopped.</summary>
        public void CloseVod()
        {
            double t = lastTimeMs / 1000.0, d = lastLenMs / 1000.0;
            Stop();
            main.Post(new { ev = "vod.closed", t, d });
            main.FocusWeb();
        }

        public void Shutdown()
        {
            gen++;
            try { watchdog.Stop(); tick.Stop(); } catch { }
            try { video.Hide(); Controls.Hide(); } catch { }
            Task.Run(() => { try { Mp.Stop(); } catch { } });
        }

        // ---------- where the picture goes ----------
        public void SetInline(CssRect rect, bool visible)
        {
            inlineRect = rect;
            inlineVisible = visible;
            if (Mode == PlayerMode.Inline) ApplyGeometry();
        }

        public void EnterFull()
        {
            if (Mode == PlayerMode.Hidden || !IsLive) return;
            Mode = PlayerMode.Full;
            ApplyGeometry();
            Controls.ActivateSoon();
        }

        /// <summary>Esc / back button.</summary>
        public void Exit()
        {
            if (Mode == PlayerMode.FullScreen) { Mode = beforeFullScreen; ApplyGeometry(); return; }
            if (IsLive)
            {
                if (Mode != PlayerMode.Inline)
                {
                    Mode = PlayerMode.Inline;
                    ApplyGeometry();
                    main.Post(new { ev = "live.mode", mode = "inline" });
                    main.FocusWeb();
                }
            }
            else CloseVod();
        }

        public void ToggleFullScreen()
        {
            if (Mode == PlayerMode.Hidden) return;
            if (Mode == PlayerMode.FullScreen) Mode = beforeFullScreen;
            else { beforeFullScreen = Mode == PlayerMode.Inline ? PlayerMode.Full : Mode; Mode = PlayerMode.FullScreen; }
            ApplyGeometry();
            Controls.ActivateSoon();
        }

        public void ApplyGeometry()
        {
            bool minimized = main.WindowState == WindowState.Minimized;
            bool show = !minimized && (Mode == PlayerMode.Full || Mode == PlayerMode.FullScreen ||
                (Mode == PlayerMode.Inline && inlineVisible && Started && inlineRect.W > 8 && inlineRect.H > 8));
            if (!show)
            {
                if (video.IsVisible) video.Hide();
                if (Controls.IsVisible) Controls.Hide();
                return;
            }
            Int32Rect r;
            try
            {
                r = Mode == PlayerMode.FullScreen ? main.MonitorRect()
                  : Mode == PlayerMode.Full ? main.WebScreenRect()
                  : main.CssToScreen(inlineRect);
            }
            catch (Exception ex) { App.Log("geometry: " + ex.Message); return; }

            bool top = Mode == PlayerMode.FullScreen;
            if (video.Topmost != top) video.Topmost = top;
            if (Controls.Topmost != top) Controls.Topmost = top;
            Controls.SetInteractive(Mode != PlayerMode.Inline, Mode == PlayerMode.FullScreen);

            if (!video.IsVisible) video.Show();
            Native.SetWindowPos(video.Handle, IntPtr.Zero, r.X, r.Y, r.Width, r.Height,
                Native.SWP_NOACTIVATE | Native.SWP_NOZORDER | Native.SWP_NOOWNERZORDER);
            if (!Controls.IsVisible) Controls.Show();
            Native.SetWindowPos(Controls.Handle, IntPtr.Zero, r.X, r.Y, r.Width, r.Height,
                Native.SWP_NOACTIVATE | Native.SWP_NOZORDER | Native.SWP_NOOWNERZORDER);
        }

        // ---------- playback controls ----------
        public void TogglePause()
        {
            if (!Started) { if (Failed) Retry(); return; }
            Run(() => Mp.Pause());
        }

        public void SeekTo(long ms)
        {
            if (IsLive || !Started) return;
            if (lastLenMs > 0) ms = Math.Max(0, Math.Min(ms, lastLenMs - 1500));
            lastTimeMs = ms;
            try { if (Mp.IsSeekable) Mp.Time = ms; } catch { }
        }

        public void SeekBy(long deltaMs)
        {
            long now = Mp.Time > 0 ? Mp.Time : lastTimeMs;
            SeekTo(now + deltaMs);
        }

        public int Volume => cfg.Volume;
        public bool Muted { get { try { return Mp.Mute; } catch { return false; } } }

        public void SetVolume(int v, bool save)
        {
            v = Math.Max(0, Math.Min(100, v));
            cfg.Volume = v;
            try { Mp.Volume = v; if (v > 0 && Mp.Mute) Mp.Mute = false; } catch { }
            if (save) cfg.Save();
            Controls.UpdateVolume();
        }

        public void ToggleMute()
        {
            try { Mp.ToggleMute(); } catch { }
            After(80, () => Controls.UpdateVolume());
        }

        // ---------- subtitles and audio ----------
        static (string name, string lang) Clean(string raw)
        {
            raw = (raw ?? "").Trim();
            var m = Regex.Match(raw, @"^(.*?)\s*-\s*\[(.+?)\]$");
            if (!m.Success) return (raw, raw);
            string label = m.Groups[1].Value.Trim(), lang = m.Groups[2].Value.Trim();
            if (label.Length == 0 || Regex.IsMatch(label, @"^Track \d+$", RegexOptions.IgnoreCase)) return (lang, lang);
            if (label.IndexOf(lang, StringComparison.OrdinalIgnoreCase) >= 0) return (label, lang);
            return (lang + " · " + label, lang);
        }

        public List<Track> SubTracks()
        {
            var list = new List<Track>();
            try
            {
                int cur = Mp.Spu;
                foreach (var t in Mp.SpuDescription)
                {
                    if (t.Id < 0) continue;
                    var (name, lang) = Clean(t.Name);
                    list.Add(new Track { Id = t.Id, Name = name, Lang = lang, Active = t.Id == cur });
                }
            }
            catch { }
            return list;
        }

        public List<Track> AudioTracks()
        {
            var list = new List<Track>();
            try
            {
                int cur = Mp.AudioTrack;
                foreach (var t in Mp.AudioTrackDescription)
                {
                    if (t.Id < 0) continue;
                    var (name, lang) = Clean(t.Name);
                    list.Add(new Track { Id = t.Id, Name = name, Lang = lang, Active = t.Id == cur });
                }
            }
            catch { }
            return list;
        }

        public double SubDelaySeconds { get { try { return Mp.SpuDelay / 1_000_000.0; } catch { return 0; } } }

        static bool Matches(Track t, string pref) =>
            !string.IsNullOrEmpty(pref) &&
            (string.Equals(t.Lang, pref, StringComparison.OrdinalIgnoreCase) || string.Equals(t.Name, pref, StringComparison.OrdinalIgnoreCase));

        void ApplyTrackPrefs()
        {
            if (userPickedTracks) return;
            try
            {
                var aud = AudioTracks();
                var a = aud.FirstOrDefault(x => Matches(x, cfg.AudioLang));
                if (a != null && !a.Active) Mp.SetAudioTrack(a.Id);

                var subs = SubTracks();
                if (cfg.SubLang == "off") { if (subs.Any(x => x.Active)) Mp.SetSpu(-1); }
                else if (!string.IsNullOrEmpty(cfg.SubLang) && !subs.Any(x => x.Active && Matches(x, cfg.SubLang)))
                {
                    var s = subs.FirstOrDefault(x => Matches(x, cfg.SubLang));
                    if (s != null) Mp.SetSpu(s.Id);
                }
            }
            catch (Exception ex) { App.Log("track prefs: " + ex.Message); }
            TracksChangedSoon();
        }

        public void SetSub(int id)
        {
            userPickedTracks = true;
            try { Mp.SetSpu(id); } catch { }
            if (id < 0) cfg.SubLang = "off";
            else
            {
                var t = SubTracks().FirstOrDefault(x => x.Id == id);
                if (t != null) cfg.SubLang = t.Lang;
            }
            cfg.Save();
            TracksChangedSoon();
        }

        public void SetAudio(int id)
        {
            userPickedTracks = true;
            try { Mp.SetAudioTrack(id); } catch { }
            var t = AudioTracks().FirstOrDefault(x => x.Id == id);
            if (t != null) { cfg.AudioLang = t.Lang; cfg.Save(); }
            TracksChangedSoon();
        }

        public void ToggleSubs()
        {
            var subs = SubTracks();
            if (subs.Any(x => x.Active)) SetSub(-1);
            else if (subs.Count > 0) SetSub((subs.FirstOrDefault(x => Matches(x, cfg.SubLang)) ?? subs[0]).Id);
            Controls.Toast(subs.Count == 0 ? "No subtitles in this video" : subs.Any(x => x.Active) ? "Subtitles off" : "Subtitles on");
        }

        public void AddSubDelay(double seconds)
        {
            try { Mp.SetSpuDelay(Mp.SpuDelay + (long)(seconds * 1_000_000)); } catch { }
            TracksChangedSoon();
        }

        public void PickSubtitleFile(Window owner)
        {
            if (!Started) { Controls.Toast("Start playing something first"); return; }
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Add subtitles",
                Filter = "Subtitle files|*.srt;*.vtt;*.ass;*.ssa;*.sub;*.smi|All files|*.*",
            };
            if (dlg.ShowDialog(owner) != true) return;
            try
            {
                userPickedTracks = true;
                Mp.AddSlave(MediaSlaveType.Subtitle, new Uri(dlg.FileName).AbsoluteUri, true);
                Controls.Toast("Subtitles added");
            }
            catch (Exception ex) { Controls.Toast("Couldn't add that file"); App.Log(ex); }
            After(600, TracksChangedSoon);
        }

        void TracksChangedSoon() { tracksDebounce.Stop(); tracksDebounce.Start(); }

        public void SendTracks()
        {
            main.Post(new
            {
                ev = "tracks",
                subs = SubTracks().Select(t => new { id = t.Id, name = t.Name, lang = t.Lang, active = t.Active }).ToArray(),
                audio = AudioTracks().Select(t => new { id = t.Id, name = t.Name, lang = t.Lang, active = t.Active }).ToArray(),
                delay = SubDelaySeconds,
            });
        }

        public void RequestZap(int dir) => main.Post(new { ev = "zap", dir });
        public void RequestNext() { if (HasNext) main.Post(new { ev = "vod.next" }); }
        public void CopyLink() { try { Clipboard.SetText(CurrentUrl ?? ""); Controls.Toast("Stream link copied"); } catch { } }
        public void FocusPage() => main.FocusWeb();

        public string Debug() =>
            $"mode={Mode} started={Started} failed={Failed} state={SafeState()} url#{urlIndex + 1}/{urls.Length} visible={video.IsVisible} err={LastError}";

        string SafeState() { try { return Mp.State.ToString(); } catch { return "?"; } }

        static void After(int ms, Action a)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (s, e) => { t.Stop(); a(); };
            t.Start();
        }
    }
}
