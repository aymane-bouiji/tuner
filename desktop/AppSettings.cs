using System;
using System.IO;
using System.Text.Json;

namespace Tuner
{
    /// <summary>Small settings file in %LOCALAPPDATA%\Tuner\settings.json.</summary>
    public class AppSettings
    {
        /// <summary>Preferred subtitle language (e.g. "French"). "off" = keep subtitles off. Empty = no preference.</summary>
        public string SubLang { get; set; } = "";
        /// <summary>Preferred audio language (e.g. "English"). Empty = no preference.</summary>
        public string AudioLang { get; set; } = "";
        public int Volume { get; set; } = 100;
        /// <summary>Identity sent to the IPTV provider. Many panels only answer player apps.</summary>
        public string UserAgent { get; set; } = "VLC/3.0.20 LibVLC/3.0.20";
        /// <summary>Milliseconds of video kept ahead on live channels. Lower = faster channel changes, higher = fewer stalls.</summary>
        public int LiveCaching { get; set; } = 1200;
        /// <summary>Milliseconds of video kept ahead on movies and episodes.</summary>
        public int VodCaching { get; set; } = 2000;
        public double WindowWidth { get; set; } = 1360;
        public double WindowHeight { get; set; } = 840;
        public bool Maximized { get; set; }

        /// <summary>%LOCALAPPDATA%\Tuner — the self-test uses its own sub-folder so it never touches your real sign-in.</summary>
        public static string DataDir =>
            App.HasArg("--selftest")
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tuner", "selftest")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tuner");

        static string FilePath => Path.Combine(DataDir, "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
            catch (Exception ex) { App.Log("settings: " + ex.Message); }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { App.Log("settings: " + ex.Message); }
        }
    }
}
