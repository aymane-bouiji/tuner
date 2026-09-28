using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Tuner
{
    /// <summary>Plain black borderless window that VLC draws the picture into.</summary>
    public class VideoWindow : Window
    {
        public IntPtr Handle { get; }

        public VideoWindow(Window owner)
        {
            Owner = owner;
            Title = "Tuner video";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Background = Brushes.Black;
            Left = -32000; Top = -32000; Width = 16; Height = 16;
            Handle = new WindowInteropHelper(this).EnsureHandle();
            // Never take keyboard focus away from the main window, and stay out of Alt+Tab.
            Native.SetExStyle(Handle, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW, true);
        }
    }
}
