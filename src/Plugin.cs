using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using NOCursorPilot.CursorFlight;

namespace NOCursorPilot
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid    = "com.cursorpilot.NOCursorPilot";
        public const string PluginName    = "NOCursorPilot";
        public const string PluginVersion = "0.3.0";

        public static bool Enabled = true;
        public static ManualLogSource LogSource;
        private Harmony _harmony;
        private CameraMode _lastCamMode;

        // Keybinds + engine toggles only. Flight tuning lives in JSON profiles
        // under BepInEx/config/NOCursorPilot.profiles/<name>.json.
        public static ConfigEntry<KeyboardShortcut> ToggleKey;
        public static ConfigEntry<KeyboardShortcut> DumpKey;
        public static ConfigEntry<KeyboardShortcut> DumpProfileKey;
        public static ConfigEntry<KeyboardShortcut> ReloadConfigKey;

        public static ConfigEntry<string> ActiveProfile;

        public static ConfigEntry<bool>  TurnToFreelook;
        public static ConfigEntry<float> FreelookRecoverySeconds;
        public static ConfigEntry<float> FreelookGraceSeconds;
        public static ConfigEntry<bool>  ShowHudLabel;

        public static ConfigEntry<bool>  TelemetryEnabled;
        public static ConfigEntry<int>   TelemetryIntervalFrames;
        public static ConfigEntry<bool>  DumpOnCameraModeChange;

        private void Awake()
        {
            LogSource = Logger;

            ToggleKey = Config.Bind("Controls", "ToggleKey",
                new KeyboardShortcut(KeyCode.F9),
                "Toggle cursor pilot on/off.");

            DumpKey = Config.Bind("Controls", "DumpKey",
                new KeyboardShortcut(KeyCode.None),
                "Dump last N telemetry snapshots to the BepInEx log.");

            DumpProfileKey = Config.Bind("Controls", "DumpProfileKey",
                new KeyboardShortcut(KeyCode.None),
                "Dump active profile as JSON snapshot to NOCursorPilot.profiles/.");

            ReloadConfigKey = Config.Bind("Controls", "ReloadConfigKey",
                new KeyboardShortcut(KeyCode.F1),
                "Re-read BepInEx config + all JSON profiles from disk.");

            ActiveProfile = Config.Bind("Profile", "ActiveProfile", "default",
                "Name of the JSON profile under NOCursorPilot.profiles/ to use. " +
                "If missing, falls back to 'default'. Files are <name>.json.");

            TurnToFreelook = Config.Bind("Camera", "TurnToFreelook", false,
                "On Free Look release: true = plane turns to wherever camera ended up. " +
                "false = camera animates back to plane velocity over FreelookRecoverySeconds.");

            FreelookRecoverySeconds = Config.Bind("Camera", "FreelookRecoverySeconds", 0.5f,
                "Seconds for camera pan/tilt to decay back to plane velocity direction.");

            FreelookGraceSeconds = Config.Bind("Camera", "FreelookGraceSeconds", 0.5f,
                "Extra delay after Free Look release before cursor pilot resumes plane control.");

            ShowHudLabel = Config.Bind("UI", "ShowHudLabel", true,
                "Show 'CURSOR' label near top of screen when active.");

            TelemetryEnabled = Config.Bind("Telemetry", "Enabled", true,
                "Capture snapshots of camera/plane/input state into a ring buffer.");

            TelemetryIntervalFrames = Config.Bind("Telemetry", "IntervalFrames", 50,
                "Frames between snapshots. FixedUpdate is 50Hz so 50 = ~1s. Min 1.");

            DumpOnCameraModeChange = Config.Bind("Telemetry", "DumpOnCameraModeChange", true,
                "Auto-dump when switching out of orbit camera mode.");

            ProfileStore.LoadAll(ActiveProfile.Value);

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();

            TelemetryWebServer.Start();

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Toggle: {ToggleKey.Value}, Profile: {ProfileStore.Active?.Name}");
        }

        private void Update()
        {
            if (ToggleKey.Value.IsDown())
            {
                Enabled = !Enabled;
                Logger.LogInfo($"CursorPilot: {(Enabled ? "ON" : "OFF")}");
            }

            if (DumpKey.Value.IsDown())
            {
                TelemetryRecorder.Dump(Logger, "manual key");
            }

            if (DumpProfileKey.Value.IsDown())
            {
                PidProfileDumper.Dump(Logger);
            }

            if (ReloadConfigKey.Value.IsDown())
            {
                Config.Reload();
                ProfileStore.LoadAll(ActiveProfile.Value);
                FlightController.OnProfileReloaded();
                Logger.LogInfo($"[Config] reloaded. Active profile: {ProfileStore.Active?.Name}");
            }

            if (DumpOnCameraModeChange.Value)
            {
                CameraMode current = CameraStateManager.cameraMode;
                if (current != _lastCamMode)
                {
                    if (_lastCamMode == CameraMode.orbit && current != CameraMode.orbit)
                    {
                        TelemetryRecorder.Dump(Logger, $"camera left orbit ({_lastCamMode} -> {current})");
                    }
                    _lastCamMode = current;
                }
            }
        }

        private void OnGUI()
        {
            if (!Enabled || !ShowHudLabel.Value) return;
            var rect = new Rect(Screen.width / 2f - 40f, 10f, 80f, 22f);
            GUI.Label(rect, "CURSOR");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            TelemetryWebServer.Stop();
        }
    }
}
