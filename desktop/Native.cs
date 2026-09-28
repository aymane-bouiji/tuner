using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Tuner
{
    static class Native
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        public const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_NOOWNERZORDER = 0x0200;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        static extern int SetWindowLong(IntPtr h, int idx, int val);
        const int GWL_EXSTYLE = -20;
        public const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;

        public static void SetExStyle(IntPtr h, int flags, bool on)
        {
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            SetWindowLong(h, GWL_EXSTYLE, on ? ex | flags : ex & ~flags);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);

        public static Int32Rect MonitorRect(IntPtr window)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            GetMonitorInfo(MonitorFromWindow(window, 2 /* nearest */), ref mi);
            return new Int32Rect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top);
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        public static void DarkTitleBar(IntPtr h)
        {
            try
            {
                int on = 1;
                if (DwmSetWindowAttribute(h, 20, ref on, 4) != 0) DwmSetWindowAttribute(h, 19, ref on, 4);
            }
            catch { }
        }

        // ---- screen capture (used by the self-test) ----
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);

        public static void CaptureScreen(Int32Rect r, string file)
        {
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, r.Width, r.Height);
            IntPtr old = SelectObject(mem, bmp);
            try
            {
                BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.X, r.Y, 0x00CC0020 | 0x40000000 /* SRCCOPY | CAPTUREBLT */);
                SelectObject(mem, old);
                var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(src));
                using var fs = File.Create(file);
                enc.Save(fs);
            }
            finally
            {
                DeleteObject(bmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }
}
