using System;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using NOCursorPilot.CursorFlight;

namespace NOCursorPilot
{
    // Dumps a timestamped snapshot of the active profile to disk as JSON.
    // Useful for: tuning A/B, capturing current gains after live edits, sharing presets.
    internal static class PidProfileDumper
    {
        public static void Dump(ManualLogSource logger, string label = null)
        {
            string dir = ProfileStore.ProfileDir;
            try { Directory.CreateDirectory(dir); }
            catch (Exception e)
            {
                logger.LogError($"[Profile] cannot create dir {dir}: {e.Message}");
                return;
            }

            ProfileData p = ProfileStore.Active;
            if (p == null)
            {
                logger.LogWarning("[Profile] no active profile; dumping default.");
                p = ProfileData.Default();
            }

            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string suffix = string.IsNullOrEmpty(label) ? "" : $"_{Sanitize(label)}";
            string filename = $"snapshot_{stamp}{suffix}.json";
            string path = Path.Combine(dir, filename);

            string contents = p.SerializeJson();

            try
            {
                File.WriteAllText(path, contents);
                logger.LogInfo($"[Profile] dumped active='{p.Name}' to {path}");
            }
            catch (Exception e)
            {
                logger.LogError($"[Profile] write failed {path}: {e.Message}");
                return;
            }

            logger.LogInfo("[Profile] contents:\n" + contents);
        }

        private static string Sanitize(string label)
        {
            var sb = new StringBuilder(label.Length);
            foreach (char c in label)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
                else if (c == ' ') sb.Append('_');
            }
            return sb.ToString();
        }
    }
}
