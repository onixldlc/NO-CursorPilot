using System;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;

namespace NOCursorPilot
{
    /// <summary>
    /// Dumps current PID + flight tuning values to a file under BepInEx/config/NOCursorPilot.profiles/.
    /// Format is BepInEx .cfg compatible -- contents can be pasted directly into the live config.
    /// </summary>
    internal static class PidProfileDumper
    {
        private const string SubDir = "NOCursorPilot.profiles";

        public static void Dump(ManualLogSource logger, string label = null)
        {
            string dir = Path.Combine(Paths.ConfigPath, SubDir);
            try { Directory.CreateDirectory(dir); }
            catch (Exception e)
            {
                logger.LogError($"[Profile] cannot create dir {dir}: {e.Message}");
                return;
            }

            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string suffix = string.IsNullOrEmpty(label) ? "" : $"_{Sanitize(label)}";
            string filename = $"profile_{stamp}{suffix}.cfg";
            string path = Path.Combine(dir, filename);

            string contents = BuildContents();

            try
            {
                File.WriteAllText(path, contents);
                logger.LogInfo($"[Profile] dumped to {path}");
            }
            catch (Exception e)
            {
                logger.LogError($"[Profile] write failed {path}: {e.Message}");
                return;
            }

            // Also echo to log for quick copy
            logger.LogInfo("[Profile] contents:\n" + contents);
        }

        private static string BuildContents()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"## NOCursorPilot tuning profile -- dumped {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("## Paste sections below into BepInEx/config/com.cursorpilot.NOCursorPilot.cfg to apply.");
            sb.AppendLine();

            sb.AppendLine("[Flight]");
            sb.AppendLine($"Sensitivity = {Plugin.Sensitivity.Value}");
            sb.AppendLine($"TargetSmoothing = {Plugin.TargetSmoothing.Value}");
            sb.AppendLine($"AggressiveTurnAngle = {Plugin.AggressiveTurnAngle.Value}");
            sb.AppendLine($"AimDistance = {Plugin.AimDistance.Value}");
            sb.AppendLine($"OutputSmoothing = {Plugin.OutputSmoothing.Value}");
            sb.AppendLine($"UseYaw = {Plugin.UseYaw.Value}");
            sb.AppendLine($"InvertPitch = {Plugin.InvertPitch.Value}");
            sb.AppendLine($"InvertRoll = {Plugin.InvertRoll.Value}");
            sb.AppendLine();

            sb.AppendLine("[Flight.PID]");
            sb.AppendLine($"Ki = {Plugin.Ki.Value}");
            sb.AppendLine($"IntegralLimit = {Plugin.IntegralLimit.Value}");
            sb.AppendLine($"KdPitch = {Plugin.KdPitch.Value}");
            sb.AppendLine($"KdYaw = {Plugin.KdYaw.Value}");
            sb.AppendLine($"KdRoll = {Plugin.KdRoll.Value}");

            return sb.ToString();
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
