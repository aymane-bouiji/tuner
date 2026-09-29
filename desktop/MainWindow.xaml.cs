using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace Tuner
{
    /// <summary>A rectangle in the page's CSS pixels, plus the page's device-pixel ratio.</summary>
    public struct CssRect
    {
        public double X, Y, W, H, Dpr;
    }

    public partial class MainWindow : Window
    {
        public readonly AppSettings Cfg = AppSettings.Load();
        public Player Player { get; private set; }
        public bool WebReady { get; private set; }
        public IntPtr Hwnd { get; private set; }
        string messageLink;

        static readonly HttpClient http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
        })
        { Timeout = TimeSpan.FromSeconds(90) };

        public MainWindow()
        {
            InitializeComponent();
            if (Cfg.WindowWidth >= MinWidth) Width = Cfg.WindowWidth;
            if (Cfg.WindowHeight >= MinHeight) Height = Cfg.WindowHeight;
            if (Cfg.Maximized && !SelfTest.Enabled) WindowState = WindowState.Maximized;

            SourceInitialized += (s, e) => { Hwnd = new WindowInteropHelper(this).Handle; Native.DarkTitleBar(Hwnd); };
            Loaded += OnLoaded;
            LocationChanged += (s, e) => Player?.ApplyGeometry();
            SizeChanged += (s, e) => Player?.ApplyGeometry();
            StateChanged += (s, e) => Player?.ApplyGeometry();
            Closing += (s, e) =>
            {
                if (!SelfTest.Enabled)
                {
                    Cfg.Maximized = WindowState == WindowState.Maximized;
                    if (WindowState == WindowState.Normal) { Cfg.WindowWidth = ActualWidth; Cfg.WindowHeight = ActualHeight; }
                    Cfg.Save();
                }
                Player?.Shutdown();
            };
        }

        async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try { Player = new Player(this, Cfg); }
            catch (Exception ex)
            {
                App.Log(ex);
                ShowMessage("The video engine didn't start", "Tuner's copy of VLC could not be loaded: " + ex.Message +
                    "\n\nTry downloading Tuner again and unzipping the whole folder.", null, null);
                return;
            }
            await InitWeb();
            if (SelfTest.Enabled) SelfTest.Run(this);
        }

        async Task InitWeb()
        {
            var ui = Path.Combine(AppContext.BaseDirectory, "ui");
            try
            {
                var options = new CoreWebView2EnvironmentOptions(
                    "--autoplay-policy=no-user-gesture-required " +
                    "--disable-features=msSmartScreenProtection,BlockInsecurePrivateNetworkRequests,PrivateNetworkAccessSendPreflights,PrivateNetworkAccessRespectPreflightResults");
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppSettings.DataDir, "WebView2"), options);
                await Web.EnsureCoreWebView2Async(env);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                ShowMessage("One more thing to install",
                    "Tuner shows its interface with Microsoft Edge WebView2, which isn't installed on this PC yet. It's a free, small download from Microsoft. Install it, then open Tuner again.",
                    "Download WebView2", "https://go.microsoft.com/fwlink/p/?LinkId=2124703");
                return;
            }
            catch (Exception ex)
            {
                App.Log(ex);
                ShowMessage("Tuner's window couldn't start", ex.Message, null, null);
                return;
            }

            var core = Web.CoreWebView2;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = true;
            core.Settings.IsGeneralAutofillEnabled = true;
            Web.ZoomFactor = 1;
            Web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(13, 18, 32);

            core.SetVirtualHostNameToFolderMapping("tuner.app", ui, CoreWebView2HostResourceAccessKind.Allow);

            // The page talks to the IPTV provider through this window, not a separate server.
            // Tuner fetches on the page's behalf, so the browser's cross-site rules don't get in the way.
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Fetch);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.XmlHttpRequest);
            core.WebResourceRequested += OnResourceRequested;

            core.WebMessageReceived += OnWebMessage;
            core.NewWindowRequested += (s, a) => { a.Handled = true; OpenExternal(a.Uri); };
            core.NavigationCompleted += (s, a) => { if (a.IsSuccess) WebReady = true; else App.Log("navigation failed: " + a.WebErrorStatus); };
            core.ProcessFailed += (s, a) =>
            {
                App.Log("WebView2 process failed: " + a.ProcessFailedKind);
                if (a.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited ||
                    a.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                    Dispatcher.BeginInvoke(new Action(() => { Player?.Stop(); core.Reload(); }));
            };
            core.Navigate("http://tuner.app/index.html");

            var watchdog = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            int attempts = 0;
            watchdog.Tick += (s, a) =>
            {
                if (WebReady) { watchdog.Stop(); return; }
                attempts++;
                App.Log("interface not ready after " + (attempts * 20) + "s");
                if (attempts == 1) { core.Reload(); return; }
                watchdog.Stop();
                ShowMessage("Tuner's screen didn't load",
                    "Close Tuner and open it again. If this keeps happening, send the file %LOCALAPPDATA%\\Tuner\\tuner.log to Claude.", null, null);
            };
            watchdog.Start();
        }

        async void OnResourceRequested(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            Uri uri;
            try { uri = new Uri(e.Request.Uri); } catch { return; }
            if (uri.Host == "tuner.app" || (uri.Scheme != "http" && uri.Scheme != "https")) return;

            var deferral = e.GetDeferral();
            var started = DateTime.UtcNow;
            string shown = RedactQuery(uri);
            try
            {
                using var req = new HttpRequestMessage(new HttpMethod(e.Request.Method), uri);
                req.Headers.TryAddWithoutValidation("User-Agent", Cfg.UserAgent);
                req.Headers.TryAddWithoutValidation("Accept", "*/*");
                using var resp = await http.SendAsync(req);
                var bytes = await resp.Content.ReadAsByteArrayAsync();
                var type = resp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
                var headers = "Content-Type: " + type + "\nAccess-Control-Allow-Origin: *\nCache-Control: no-store";
                e.Response = Web.CoreWebView2.Environment.CreateWebResourceResponse(
                    new MemoryStream(bytes), (int)resp.StatusCode, resp.ReasonPhrase ?? "OK", headers);
                var took = (DateTime.UtcNow - started).TotalSeconds;
                if (!resp.IsSuccessStatusCode || took > 8)
                    App.Log($"provider: {(int)resp.StatusCode} in {took:0.0}s, {bytes.Length / 1024} KB  {shown}");
            }
            catch (Exception ex)
            {
                var reason = ex is TaskCanceledException ? "the server took too long to answer" : (ex.InnerException?.Message ?? ex.Message);
                if (ex.InnerException is System.Net.Sockets.SocketException se &&
                    (se.SocketErrorCode == System.Net.Sockets.SocketError.HostNotFound || se.SocketErrorCode == System.Net.Sockets.SocketError.NoData || se.SocketErrorCode == System.Net.Sockets.SocketError.TryAgain))
                    reason = "your provider's address " + uri.Host + " can't be found right now. Their server may be down, or your internet provider may be blocking it";
                App.Log($"provider: failed after {(DateTime.UtcNow - started).TotalSeconds:0.0}s ({reason})  {shown}");
                var body = System.Text.Encoding.UTF8.GetBytes("Could not reach the server (" + reason + ")");
                e.Response = Web.CoreWebView2.Environment.CreateWebResourceResponse(
                    new MemoryStream(body), 502, "Bad Gateway", "Content-Type: text/plain; charset=utf-8\nAccess-Control-Allow-Origin: *");
            }
            finally { deferral.Complete(); }
        }

        static string RedactQuery(Uri u)
        {
            var q = System.Text.RegularExpressions.Regex.Replace(u.Query, @"(username|password)=[^&]*", "$1=***");
            return u.Host + (u.IsDefaultPort ? "" : ":" + u.Port) + u.AbsolutePath + q;
        }

        // ---------- page <-> app messages ----------
        void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                Handle(doc.RootElement);
            }
            catch (Exception ex) { App.Log("message: " + ex); }
        }

        static string Str(JsonElement m, string k) =>
            m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static double Num(JsonElement m, string k) =>
            m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        static bool Bool(JsonElement m, string k) =>
            m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
        static string[] Strs(JsonElement m, string k) =>
            m.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array
                ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToArray()
                : Array.Empty<string>();
        static CssRect RectOf(JsonElement m)
        {
            if (!m.TryGetProperty("rect", out var r) || r.ValueKind != JsonValueKind.Object) return default;
            return new CssRect { X = Num(r, "x"), Y = Num(r, "y"), W = Num(r, "w"), H = Num(r, "h"), Dpr = Math.Max(0.5, Num(r, "dpr")) };
        }

        void Handle(JsonElement m)
        {
            var cmd = Str(m, "cmd");
            switch (cmd)
            {
                case "ready": WebReady = true; return;
                case "log": App.Log(Str(m, "text")); return;
                case "copy": try { Clipboard.SetText(Str(m, "text") ?? ""); } catch { } return;
                case "open": OpenExternal(Str(m, "url")); return;
            }
            if (Player == null) return;
            switch (cmd)
            {
                case "live.play": Player.PlayLive(Strs(m, "urls"), Str(m, "title") ?? "", RectOf(m)); break;
                case "live.rect": Player.SetInline(RectOf(m), Bool(m, "visible")); break;
                case "live.stop": if (Player.IsLive) Player.Stop(); break;
                case "live.full": Player.EnterFull(); break;
                case "vod.play": Player.PlayVod(Strs(m, "urls"), Str(m, "title") ?? "", Num(m, "resumeAt"), Bool(m, "hasNext")); break;
                case "vod.stop": if (!Player.IsLive && Player.Mode != PlayerMode.Hidden) Player.CloseVod(); break;
                case "tracks.get": Player.SendTracks(); break;
                case "tracks.set":
                    if (Str(m, "kind") == "audio") Player.SetAudio((int)Num(m, "id"));
                    else Player.SetSub((int)Num(m, "id"));
                    break;
                case "sub.delay": Player.AddSubDelay(Num(m, "delta")); break;
                case "sub.file": Player.PickSubtitleFile(this); break;
                case "sub.toggle": Player.ToggleSubs(); break;
                case "mute": Player.ToggleMute(); break;
            }
        }

        public void Post(object o)
        {
            try { Web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(o)); }
            catch (Exception ex) { App.Log("post: " + ex.Message); }
        }

        public async Task<string> Js(string code)
        {
            try { return await Web.CoreWebView2.ExecuteScriptAsync(code); }
            catch (Exception ex) { return "error: " + ex.Message; }
        }

        public void FocusWeb()
        {
            try { Activate(); Web.Focus(); } catch { }
        }

        // ---------- geometry helpers (device pixels) ----------
        Point WebOrigin() => Web.PointToScreen(new Point(0, 0));

        public Int32Rect WebScreenRect()
        {
            var o = WebOrigin();
            var dpi = VisualTreeHelper.GetDpi(this);
            return new Int32Rect((int)Math.Round(o.X), (int)Math.Round(o.Y),
                Math.Max(1, (int)Math.Round(Web.ActualWidth * dpi.DpiScaleX)), Math.Max(1, (int)Math.Round(Web.ActualHeight * dpi.DpiScaleY)));
        }

        public Int32Rect CssToScreen(CssRect r)
        {
            var o = WebOrigin();
            return new Int32Rect((int)Math.Round(o.X + r.X * r.Dpr), (int)Math.Round(o.Y + r.Y * r.Dpr),
                Math.Max(1, (int)Math.Round(r.W * r.Dpr)), Math.Max(1, (int)Math.Round(r.H * r.Dpr)));
        }

        public Int32Rect MonitorRect() => Native.MonitorRect(Hwnd);

        public Int32Rect WindowRect()
        {
            Native.GetWindowRect(Hwnd, out var r);
            return new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }

        // ---------- misc ----------
        public static void OpenExternal(string url)
        {
            if (string.IsNullOrEmpty(url) || !(url.StartsWith("http://") || url.StartsWith("https://"))) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        void ShowMessage(string title, string text, string button, string link)
        {
            MessageTitle.Text = title;
            MessageText.Text = text;
            messageLink = link;
            MessageButton.Content = button;
            MessageButton.Visibility = button == null ? Visibility.Collapsed : Visibility.Visible;
            Message.Visibility = Visibility.Visible;
            Web.Visibility = Visibility.Collapsed;
        }

        void MessageButton_Click(object sender, RoutedEventArgs e) => OpenExternal(messageLink);
    }
}
