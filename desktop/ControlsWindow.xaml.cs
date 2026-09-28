using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Tuner
{
    /// <summary>Transparent window above the video: control bars, status, subtitles menu.</summary>
    public partial class ControlsWindow : Window
    {
        readonly Player p;
        public IntPtr Handle { get; }
        bool interactive = true, fullScreen, seeking, volDragging, barsShown = true;
        double seekFrac;
        readonly DispatcherTimer hideTimer, uiTimer, clickTimer, toastTimer;

        static readonly Geometry PlayGeo = Geometry.Parse("M7,4.5 L7,19.5 L20,12 Z");
        static readonly Geometry PauseGeo = Geometry.Parse("M6,4.5 L10,4.5 L10,19.5 L6,19.5 Z M14,4.5 L18,4.5 L18,19.5 L14,19.5 Z");
        static readonly Geometry FullGeo = Geometry.Parse("M4,9 L4,4 L9,4 M20,9 L20,4 L15,4 M4,15 L4,20 L9,20 M20,15 L20,20 L15,20");
        static readonly Geometry ExitFullGeo = Geometry.Parse("M9,4 L9,9 L4,9 M15,4 L15,9 L20,9 M9,20 L9,15 L4,15 M15,20 L15,15 L20,15");

        public ControlsWindow(Player player, Window owner)
        {
            InitializeComponent();
            p = player;
            Owner = owner;
            Handle = new WindowInteropHelper(this).EnsureHandle();
            Native.SetExStyle(Handle, Native.WS_EX_TOOLWINDOW, true);

            hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            hideTimer.Tick += (s, e) => { hideTimer.Stop(); MaybeHideBars(); };
            uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            uiTimer.Tick += (s, e) => UpdateTime();
            uiTimer.Start();
            clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
            clickTimer.Tick += (s, e) => { clickTimer.Stop(); SingleClick(); };
            toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
            toastTimer.Tick += (s, e) => { toastTimer.Stop(); ToastBox.Visibility = Visibility.Collapsed; };

            PreviewKeyDown += OnKey;
            CcPopup.Closed += (s, e) => RestartHide();
            UpdateVolume();
        }

        // ---------- mode ----------
        public void SetInteractive(bool on, bool isFullScreen)
        {
            fullScreen = isFullScreen;
            FullIcon.Data = isFullScreen ? ExitFullGeo : FullGeo;
            if (interactive == on) { if (on) ShowBars(); return; }
            interactive = on;
            // In the page (inline) the controls window must never take the keyboard away from the page.
            Native.SetExStyle(Handle, Native.WS_EX_NOACTIVATE, !on);
            if (!on)
            {
                CcPopup.IsOpen = false;
                TopBar.Visibility = BottomBar.Visibility = Visibility.Collapsed;
                StatusBox.Visibility = Visibility.Collapsed;
                Root.Cursor = null;
            }
            else ShowBars();
        }

        public void ActivateSoon()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!interactive || !IsVisible) return;
                try { Activate(); Root.Focus(); Keyboard.Focus(Root); } catch { }
            }), DispatcherPriority.Background);
        }

        public void SetTitle(string title, bool live, bool hasNext)
        {
            TitleText.Text = title ?? "";
            LiveBadge.Visibility = ZapHint.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            NextBtn.Visibility = hasNext ? Visibility.Visible : Visibility.Collapsed;
            SeekWrap.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
            RewBtn.Visibility = FwdBtn.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
            TimeText.Text = "";
            SeekFill.Width = 0;
        }

        public void ShowStatus(string title, string text, bool isError)
        {
            StatusTitle.Text = title ?? "";
            StatusText.Text = text ?? "";
            StatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            StatusActions.Visibility = isError ? Visibility.Visible : Visibility.Collapsed;
            StatusBox.Visibility = interactive ? Visibility.Visible : Visibility.Collapsed;
            BufferBox.Visibility = Visibility.Collapsed;
            if (isError) ShowBars();
        }

        public void HideStatus()
        {
            StatusBox.Visibility = Visibility.Collapsed;
            BufferBox.Visibility = Visibility.Collapsed;
        }

        public void SetBuffering(float percent)
        {
            if (percent >= 100 || !p.Started) { BufferBox.Visibility = Visibility.Collapsed; return; }
            BufferText.Text = "Buffering " + (int)percent + "%";
            BufferBox.Visibility = Visibility.Visible;
        }

        public void Toast(string text)
        {
            ToastText.Text = text;
            ToastBox.Visibility = interactive ? Visibility.Visible : Visibility.Collapsed;
            toastTimer.Stop(); toastTimer.Start();
        }

        public void ClosePopup() => CcPopup.IsOpen = false;

        // ---------- bars ----------
        public void ShowBarsNow() => ShowBars();

        void ShowBars()
        {
            if (!interactive) return;
            TopBar.Visibility = BottomBar.Visibility = Visibility.Visible;
            Root.Cursor = null;
            barsShown = true;
            RestartHide();
        }

        void RestartHide() { hideTimer.Stop(); hideTimer.Start(); }

        void MaybeHideBars()
        {
            if (!interactive || CcPopup.IsOpen || seeking || volDragging || !p.Started) return;
            bool paused = false;
            try { paused = !p.Mp.IsPlaying; } catch { }
            if (paused) return;
            if (TopBar.IsMouseOver || BottomBar.IsMouseOver) { RestartHide(); return; }
            TopBar.Visibility = BottomBar.Visibility = Visibility.Collapsed;
            Root.Cursor = Cursors.None;
            barsShown = false;
        }

        public void UpdatePlayState()
        {
            bool playing = false;
            try { playing = p.Mp.IsPlaying; } catch { }
            PlayIcon.Data = playing ? PauseGeo : PlayGeo;
            if (!playing) ShowBars();
        }

        public void UpdateVolume()
        {
            bool muted = p.Muted || p.Volume == 0;
            VolWaves.Visibility = muted ? Visibility.Collapsed : Visibility.Visible;
            VolCross.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
            VolFill.Width = VolArea.Width * (muted ? 0 : p.Volume / 100.0);
        }

        static string Fmt(long ms)
        {
            if (ms < 0) ms = 0;
            var t = TimeSpan.FromMilliseconds(ms);
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        void UpdateTime()
        {
            if (!IsVisible || p.IsLive || !p.Started) return;
            long t, d;
            try { t = p.Mp.Time; d = p.Mp.Length; } catch { return; }
            if (t < 0) t = p.TimeMs;
            if (d <= 0) d = p.LengthMs;
            if (seeking && d > 0) t = (long)(seekFrac * d);
            TimeText.Text = d > 0 ? Fmt(t) + "  /  " + Fmt(d) : Fmt(t);
            double w = SeekArea.ActualWidth;
            double frac = d > 0 ? Math.Min(1, Math.Max(0, (double)t / d)) : 0;
            SeekFill.Width = w * frac;
            SeekThumb.Margin = new Thickness(w * frac - 7, 0, 0, 0);
        }

        // ---------- mouse ----------
        void Root_MouseMove(object sender, MouseEventArgs e)
        {
            if (interactive && !barsShown) ShowBars();
            else if (interactive) RestartHide();
        }

        bool InBars(object source) =>
            source is DependencyObject d && (IsInside(d, TopBar) || IsInside(d, BottomBar));

        static bool IsInside(DependencyObject d, DependencyObject container)
        {
            while (d != null)
            {
                if (d == container) return true;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (InBars(e.OriginalSource) || IsInside(e.OriginalSource as DependencyObject, StatusBox)) return;
            if (e.ClickCount >= 2)
            {
                clickTimer.Stop();
                if (!interactive) p.EnterFull(); else p.ToggleFullScreen();
                e.Handled = true;
                return;
            }
            clickTimer.Stop(); clickTimer.Start();
        }

        void SingleClick()
        {
            if (!interactive) { p.FocusPage(); return; }
            if (CcPopup.IsOpen) { CcPopup.IsOpen = false; return; }
            p.TogglePause();
        }

        void Root_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!interactive) return;
            p.SetVolume(p.Volume + (e.Delta > 0 ? 5 : -5), true);
            Toast("Volume " + p.Volume + "%");
        }

        // seek bar
        double FracAt(MouseEventArgs e, FrameworkElement el) =>
            el.ActualWidth <= 0 ? 0 : Math.Min(1, Math.Max(0, e.GetPosition(el).X / el.ActualWidth));

        void Seek_Down(object sender, MouseButtonEventArgs e)
        {
            if (p.LengthMs <= 0) return;
            seeking = true;
            SeekArea.CaptureMouse();
            seekFrac = FracAt(e, SeekArea);
            UpdateTime();
            e.Handled = true;
        }

        void Seek_Move(object sender, MouseEventArgs e)
        {
            double f = FracAt(e, SeekArea);
            if (seeking) { seekFrac = f; UpdateTime(); }
            if (p.LengthMs > 0)
            {
                SeekTipText.Text = Fmt((long)(f * p.LengthMs));
                SeekTip.Visibility = Visibility.Visible;
                SeekTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(SeekTip, f * SeekArea.ActualWidth - SeekTip.DesiredSize.Width / 2);
            }
            SeekThumb.Visibility = Visibility.Visible;
        }

        void Seek_Up(object sender, MouseButtonEventArgs e)
        {
            if (!seeking) return;
            seeking = false;
            SeekArea.ReleaseMouseCapture();
            p.SeekTo((long)(FracAt(e, SeekArea) * p.LengthMs));
            e.Handled = true;
        }

        void Seek_Leave(object sender, MouseEventArgs e)
        {
            if (seeking) return;
            SeekTip.Visibility = Visibility.Hidden;
            SeekThumb.Visibility = Visibility.Hidden;
        }

        // volume bar
        void Vol_Down(object sender, MouseButtonEventArgs e)
        {
            volDragging = true;
            VolArea.CaptureMouse();
            p.SetVolume((int)Math.Round(FracAt(e, VolArea) * 100), false);
            e.Handled = true;
        }

        void Vol_Move(object sender, MouseEventArgs e)
        {
            if (volDragging) p.SetVolume((int)Math.Round(FracAt(e, VolArea) * 100), false);
        }

        void Vol_Up(object sender, MouseButtonEventArgs e)
        {
            if (!volDragging) return;
            volDragging = false;
            VolArea.ReleaseMouseCapture();
            p.SetVolume((int)Math.Round(FracAt(e, VolArea) * 100), true);
            e.Handled = true;
        }

        // ---------- buttons ----------
        void Back_Click(object sender, RoutedEventArgs e) => p.Exit();
        void Next_Click(object sender, RoutedEventArgs e) => p.RequestNext();
        void Play_Click(object sender, RoutedEventArgs e) => p.TogglePause();
        void Rew_Click(object sender, RoutedEventArgs e) => p.SeekBy(-10000);
        void Fwd_Click(object sender, RoutedEventArgs e) => p.SeekBy(10000);
        void Mute_Click(object sender, RoutedEventArgs e) => p.ToggleMute();
        void Full_Click(object sender, RoutedEventArgs e) => p.ToggleFullScreen();
        void Retry_Click(object sender, RoutedEventArgs e) => p.Retry();
        void Copy_Click(object sender, RoutedEventArgs e) => p.CopyLink();
        void Cc_Click(object sender, RoutedEventArgs e)
        {
            if (CcPopup.IsOpen) { CcPopup.IsOpen = false; return; }
            BuildCcMenu();
            CcPopup.IsOpen = true;
            hideTimer.Stop();
        }

        // ---------- keyboard ----------
        void OnKey(object sender, KeyEventArgs e)
        {
            if (!interactive) return;
            ShowBars();
            switch (e.Key)
            {
                case Key.Escape: if (CcPopup.IsOpen) CcPopup.IsOpen = false; else p.Exit(); break;
                case Key.Space: case Key.K: p.TogglePause(); break;
                case Key.F: case Key.F11: p.ToggleFullScreen(); break;
                case Key.M: p.ToggleMute(); break;
                case Key.C: p.ToggleSubs(); break;
                case Key.N: p.RequestNext(); break;
                case Key.Left: if (!p.IsLive) p.SeekBy(-10000); break;
                case Key.Right: if (!p.IsLive) p.SeekBy(10000); break;
                case Key.Up: case Key.PageUp:
                    if (p.IsLive) p.RequestZap(-1); else { p.SetVolume(p.Volume + 5, true); Toast("Volume " + p.Volume + "%"); }
                    break;
                case Key.Down: case Key.PageDown:
                    if (p.IsLive) p.RequestZap(1); else { p.SetVolume(p.Volume - 5, true); Toast("Volume " + p.Volume + "%"); }
                    break;
                default: return;
            }
            e.Handled = true;
        }

        // ---------- subtitles & audio menu ----------
        public void RefreshTracks()
        {
            bool anyOn = p.SubTracks().Any(t => t.Active);
            var brush = anyOn ? (Brush)FindResource("Accent") : Brushes.White;
            CcFrame.Stroke = brush; CcLines.Stroke = brush;
            if (CcPopup.IsOpen) BuildCcMenu();
        }

        void BuildCcMenu()
        {
            CcList.Children.Clear();
            var subs = p.SubTracks();
            var audio = p.AudioTracks();
            bool anyOn = subs.Any(t => t.Active);

            Header("Subtitles");
            Row("Off", !anyOn, null, () => p.SetSub(-1));
            foreach (var t in subs) { var id = t.Id; Row(t.Name, t.Active, null, () => p.SetSub(id)); }
            if (subs.Count == 0) Note("This video has no built-in subtitles.");

            if (anyOn)
            {
                Header("Timing");
                var row = new DockPanel { Margin = new Thickness(10, 2, 10, 4), LastChildFill = true };
                void Chip(string text, double delta, Dock dock)
                {
                    var b = new Button { Content = text, Style = (Style)FindResource("ChipBtn"), Margin = new Thickness(dock == Dock.Left ? 0 : 6, 0, dock == Dock.Left ? 6 : 0, 0) };
                    b.Click += (s, e) => { p.AddSubDelay(delta); };
                    DockPanel.SetDock(b, dock);
                    row.Children.Add(b);
                }
                Chip("−1s", -1, Dock.Left); Chip("−0.25", -0.25, Dock.Left);
                Chip("+1s", 1, Dock.Right); Chip("+0.25", 0.25, Dock.Right);
                double d = p.SubDelaySeconds;
                row.Children.Add(new TextBlock
                {
                    Text = (d > 0 ? "+" : "") + d.ToString("0.00") + "s",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 13,
                });
                CcList.Children.Add(row);
                Note("Subtitles too early? Press +. Too late? Press −.");
            }

            Separator();
            Row("Load subtitle file…", null, "srt · vtt · ass", () => { CcPopup.IsOpen = false; p.PickSubtitleFile(this); });

            if (audio.Count > 1)
            {
                Separator();
                Header("Audio");
                foreach (var t in audio) { var id = t.Id; Row(t.Name, t.Active, null, () => p.SetAudio(id)); }
            }
        }

        void Header(string text) => CcList.Children.Add(new TextBlock
        {
            Text = text.ToUpperInvariant(),
            FontSize = 11, FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("Dim"),
            Margin = new Thickness(10, 10, 10, 4),
        });

        void Note(string text) => CcList.Children.Add(new TextBlock
        {
            Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("Muted"),
            Margin = new Thickness(10, 2, 10, 8),
        });

        void Separator() => CcList.Children.Add(new Border
        {
            Height = 1, Background = (Brush)FindResource("Line"), Margin = new Thickness(4, 6, 4, 6),
        });

        void Row(string text, bool? selected, string hint, Action onClick)
        {
            var grid = new DockPanel { LastChildFill = true };
            if (selected.HasValue)
            {
                var ring = new Grid { Width = 16, Height = 16, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                ring.Children.Add(new Ellipse { Stroke = selected.Value ? (Brush)FindResource("Accent") : (Brush)FindResource("Line"), StrokeThickness = 2 });
                if (selected.Value) ring.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = (Brush)FindResource("Accent") });
                DockPanel.SetDock(ring, Dock.Left);
                grid.Children.Add(ring);
            }
            if (hint != null)
            {
                var h = new TextBlock { Text = hint, FontSize = 11, Foreground = (Brush)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                DockPanel.SetDock(h, Dock.Right);
                grid.Children.Add(h);
            }
            grid.Children.Add(new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var b = new Button { Content = grid, Style = (Style)FindResource("MenuRow") };
            b.Click += (s, e) => { onClick(); Dispatcher.BeginInvoke(new Action(() => { if (CcPopup.IsOpen) BuildCcMenu(); }), DispatcherPriority.Background); };
            CcList.Children.Add(b);
        }
    }
}
