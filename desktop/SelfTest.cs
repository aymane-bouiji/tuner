using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace Tuner
{
    /// <summary>
    /// Automatic check used by the build machine:  Tuner.exe --selftest --out <folder>
    /// Signs in to a fake IPTV server on 127.0.0.1:9100, plays a live channel and a movie,
    /// switches subtitles, seeks, takes screenshots and writes result.json.
    /// </summary>
    static class SelfTest
    {
        public static bool Enabled => App.HasArg("--selftest");

        public static async void Run(MainWindow w)
        {
            var outDir = App.ArgAfter("--out") ?? Path.Combine(AppSettings.DataDir, "selftest");
            Directory.CreateDirectory(outDir);
            var steps = new List<string>();
            var data = new Dictionary<string, object>();
            bool pass = true;
            var p = w.Player;

            void Check(string name, bool ok, object detail = null)
            {
                var line = (ok ? "PASS  " : "FAIL  ") + name + (detail != null ? "  —  " + detail : "");
                steps.Add(line); App.Log("selftest " + line);
                if (!ok) pass = false;
            }

            void Shot(string name, bool fullMonitor = false)
            {
                try
                {
                    var r = fullMonitor ? w.MonitorRect() : w.WindowRect();
                    Native.CaptureScreen(r, Path.Combine(outDir, name));
                }
                catch (Exception ex) { steps.Add("note  screenshot " + name + " failed: " + ex.Message); }
            }

            async Task<bool> Until(Func<bool> cond, int ms)
            {
                for (int waited = 0; waited < ms; waited += 200) { if (cond()) return true; await Task.Delay(200); }
                return cond();
            }

            async Task<bool> UntilJs(string expr, int ms)
            {
                for (int waited = 0; waited < ms; waited += 250) { if (await w.Js(expr) == "true") return true; await Task.Delay(250); }
                return await w.Js(expr) == "true";
            }

            void Write()
            {
                data["pass"] = pass;
                data["steps"] = steps;
                File.WriteAllText(Path.Combine(outDir, "result.json"), JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            }

            var guard = Task.Delay(TimeSpan.FromSeconds(200)).ContinueWith(_ =>
            {
                w.Dispatcher.Invoke(() => { Check("finished in time", false, "the test took longer than 200 seconds"); Write(); });
                Environment.Exit(3);
            });

            try
            {
                w.WindowState = WindowState.Normal;
                w.Left = 0; w.Top = 0; w.Width = 1010; w.Height = 715;
                w.Activate();

                Check("interface loaded", await Until(() => w.WebReady, 40000));
                await Task.Delay(1500);
                var native = await w.Js("document.documentElement.classList.contains('native')");
                Check("interface runs in app mode", native == "true", native);
                Shot("1-sign-in.png");

                await w.Js("(() => { const s = (id, v) => { const el = document.querySelector(id); el.value = v; el.dispatchEvent(new Event('input')); }; " +
                           "s('#fServer', 'http://127.0.0.1:9100'); s('#fUser', 'alice'); s('#fPass', 's3cret'); " +
                           "document.querySelector('#loginForm').requestSubmit(); return 1; })()");
                bool listed = await UntilJs("document.querySelectorAll('#chanList .ch').length > 0", 40000);
                Check("signs in and lists channels, talking to the provider directly", listed,
                    await w.Js("document.querySelector('#loginErr').hidden ? document.querySelector('#liveCount').textContent : document.querySelector('#loginErr').textContent"));
                if (!listed) { Shot("2-sign-in-failed.png"); Write(); await Task.Delay(300); Environment.Exit(0); return; }
                Shot("2-channels.png");

                // --- live channel (MPEG-TS) shown inside the page ---
                await w.Js("document.querySelector('#chanList .ch[data-id=\"2\"]').click(); 1");
                Check("live channel starts (MPEG-TS)", await Until(() => p.Started, 30000), p.Debug());
                await Task.Delay(2500);
                Check("picture sits in the page's video area", p.Mode == PlayerMode.Inline && p.VideoVisible, p.Debug());
                Shot("3-live-in-page.png");

                // --- HLS channel with subtitle tracks, then full window ---
                await w.Js("document.querySelector('#chanList .ch[data-id=\"4\"]').click(); 1");
                await Task.Delay(500);
                Check("second live channel starts (HLS)", await Until(() => p.Started, 30000), p.Debug());
                await Task.Delay(2500);
                data["liveSubtitleTracks"] = p.SubTracks().Select(t => t.Name).ToArray();
                p.EnterFull();
                await Task.Delay(1500);
                Check("live goes full-window", p.Mode == PlayerMode.Full && p.VideoVisible, p.Debug());
                Shot("4-live-full-window.png");
                p.Exit();
                await Task.Delay(800);
                Check("Esc returns live to the page", p.Mode == PlayerMode.Inline, p.Debug());

                // --- movie: MKV with AC-3 audio and two subtitle tracks ---
                await w.Js("document.querySelector('.tab[data-sec=\"vod\"]').click(); 1");
                Check("movies list", await UntilJs("!!document.querySelector('#posters .card[data-id=\"5001\"]')", 30000));
                await w.Js("document.querySelector('#posters .card[data-id=\"5001\"]').click(); 1");
                await UntilJs("!!document.querySelector('[data-act=\"start\"]')", 20000);
                await w.Js("document.querySelector('[data-act=\"start\"]').click(); 1");
                Check("movie (MKV + AC-3) plays", await Until(() => p.Started && !p.IsLive, 30000), p.Debug());
                await Task.Delay(2500);
                var subs = p.SubTracks();
                var audio = p.AudioTracks();
                data["movieSubtitleTracks"] = subs.Select(t => t.Name).ToArray();
                data["movieAudioTracks"] = audio.Select(t => t.Name).ToArray();
                Check("built-in subtitle tracks found", subs.Count >= 2, string.Join(", ", subs.Select(t => t.Name)));
                Check("audio tracks found", audio.Count >= 2, string.Join(", ", audio.Select(t => t.Name)));
                var fr = subs.FirstOrDefault(t => t.Lang.StartsWith("Fr", StringComparison.OrdinalIgnoreCase)) ?? subs.LastOrDefault();
                if (fr != null) p.SetSub(fr.Id);
                p.SeekTo(59000);
                await Task.Delay(3500);
                long now = p.Mp.Time;
                data["timeAfterSeekMs"] = now;
                Check("seeking works", now > 58000 && now < 75000, now);
                Check("French subtitles selected", p.SubTracks().Any(t => t.Active && t.Lang.StartsWith("Fr", StringComparison.OrdinalIgnoreCase)),
                    string.Join(", ", p.SubTracks().Where(t => t.Active).Select(t => t.Name)));
                p.Controls.ShowBarsNow();
                await Task.Delay(400);
                Shot("5-movie-subtitles.png");

                // Go back to 0:30 so the position counts as "part-way through" (not near the end).
                p.SeekTo(30000);
                await Task.Delay(2500);
                p.Exit();
                Check("closing the movie offers Resume", await UntilJs("!!document.querySelector('[data-act=\"resume\"]')", 10000),
                    await w.Js("(document.querySelector('[data-act=\"resume\"]') || {}).textContent || 'no resume button'"));
                Shot("6-after-closing.png");
            }
            catch (Exception ex)
            {
                Check("no crash", false, ex.ToString());
            }

            Write();
            await Task.Delay(500);
            Environment.Exit(0);
        }
    }
}
