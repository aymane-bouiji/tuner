using System;
using System.IO;
using System.Windows;

namespace Tuner
{
    public partial class App : Application
    {
        public static string[] Args = Array.Empty<string>();
        static readonly object logLock = new object();

        protected override void OnStartup(StartupEventArgs e)
        {
            Args = e.Args ?? Array.Empty<string>();
            DispatcherUnhandledException += (s, ev) => { Log(ev.Exception); ev.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (s, ev) => Log(ev.ExceptionObject);
            Log("Tuner starting, version " + typeof(App).Assembly.GetName().Version);
            base.OnStartup(e);
        }

        public static string ArgAfter(string name)
        {
            for (int i = 0; i < Args.Length - 1; i++)
                if (string.Equals(Args[i], name, StringComparison.OrdinalIgnoreCase)) return Args[i + 1];
            return null;
        }

        public static bool HasArg(string name) => Array.Exists(Args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Removes usernames and passwords from anything written to the log (including VLC's own messages).</summary>
        public static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            text = System.Text.RegularExpressions.Regex.Replace(text, @"/(live|movie|series|timeshift)/[^/\s']+/[^/\s']+/", "/$1/***/***/");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(username|password)=[^&\s']*", "$1=***");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"://[^/\s:@']+:[^/\s@']+@", "://***:***@");
            return text;
        }

        public static void Log(object o)
        {
            try
            {
                lock (logLock)
                {
                    Directory.CreateDirectory(AppSettings.DataDir);
                    var file = Path.Combine(AppSettings.DataDir, "tuner.log");
                    var info = new FileInfo(file);
                    if (info.Exists && info.Length > 2_000_000) info.Delete();
                    File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + Redact(o?.ToString()) + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
