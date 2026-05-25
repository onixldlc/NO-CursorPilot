using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NOCursorPilot
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid    = "com.cursorpilot.NOCursorPilot";
        public const string PluginName    = "NOCursorPilot";
        public const string PluginVersion = "1.0.0";

        public static bool Enabled = true;
        public static ManualLogSource LogSource;
        private Harmony _harmony;
        private CameraMode _lastCamMode;

        public static ConfigEntry<KeyboardShortcut> ToggleKey;
        public static ConfigEntry<KeyboardShortcut> DumpKey;
        public static ConfigEntry<KeyboardShortcut> DumpProfileKey;
        public static ConfigEntry<KeyboardShortcut> ReloadConfigKey;

        public static ConfigEntry<float> OrbitMouseSensitivity;
        public static ConfigEntry<float> FreelookMouseSensitivity;
        public static ConfigEntry<float> TargetSmoothing;
        public static ConfigEntry<float> OutputSmoothing;
        public static ConfigEntry<float> Sensitivity;
        public static ConfigEntry<float> AggressiveTurnAngle;
        public static ConfigEntry<float> AimDistance;
        public static ConfigEntry<bool>  InvertPitch;
        public static ConfigEntry<bool>  InvertRoll;
        public static ConfigEntry<float> Ki;
        public static ConfigEntry<float> IntegralLimit;
        public static ConfigEntry<float> KdPitch;
        public static ConfigEntry<float> KdRoll;
        public static ConfigEntry<float> PidYawLimit;
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
                "Dump last N telemetry snapshots to the BepInEx log. Unbound by default.");

            DumpProfileKey = Config.Bind("Controls", "DumpProfileKey",
                new KeyboardShortcut(KeyCode.None),
                "Dump current PID + tuning values to BepInEx/config/NOCursorPilot.profiles/ as a .cfg file. Unbound by default.");

            ReloadConfigKey = Config.Bind("Controls", "ReloadConfigKey",
                new KeyboardShortcut(KeyCode.F1),
                "Re-read the BepInEx config file from disk. Use after editing values externally.");

            OrbitMouseSensitivity = Config.Bind("Camera", "OrbitMouseSensitivity", 1.0f,
                "Multiplier on mouse pan/tilt accumulation in orbit camera mode while NOT holding Free Look. " +
                "1.0 = game default. 2.0 = twice as fast. 0.5 = half as fast. " +
                "Stacks on top of PlayerSettings.viewSensitivity.");

            FreelookMouseSensitivity = Config.Bind("Camera", "FreelookMouseSensitivity", 1.0f,
                "Multiplier on mouse pan/tilt accumulation while Free Look is HELD in orbit cam. " +
                "Independent from OrbitMouseSensitivity so you can have e.g. slow cursor pilot aim " +
                "but fast freelook scanning.");

            TargetSmoothing = Config.Bind("Flight", "TargetSmoothing", 3.0f,
                "Camera-direction smoothing lambda (1 - exp(-lambda * dt)). " +
                "Higher = snappier, lower = lazier. 0 = no smoothing. " +
                "Skipped (snap) during Free Look hold and recovery.");

            OutputSmoothing = Config.Bind("Flight", "OutputSmoothing", 5.0f,
                "Custom PID stick-output smoothing lambda. Damps oscillation when saturated. " +
                "Higher = less smoothing (snappier), lower = more smoothing (lazier). 0 disables.");

            Sensitivity = Config.Bind("Flight", "Sensitivity", 5.0f,
                "Custom PID proportional gain on local-space error. Higher = more aggressive at big angles.");

            AggressiveTurnAngle = Config.Bind("Flight", "AggressiveTurnAngle", 3.0f,
                "Degrees off target where custom PID's roll blends fully from wings-level to bank-toward-target.");

            AimDistance = Config.Bind("Flight", "AimDistance", 500.0f,
                "Meters ahead the virtual fly-target is projected for the custom PID.");

            InvertPitch = Config.Bind("Flight", "InvertPitch", false,
                "Flip custom PID pitch sign if plane pitches wrong way.");

            InvertRoll = Config.Bind("Flight", "InvertRoll", false,
                "Flip custom PID roll sign if plane banks wrong way.");

            Ki = Config.Bind("Flight.PID", "Ki", 0.05f,
                "Custom PID integral gain (all axes). Eliminates steady-state error. 0 = PD only.");

            IntegralLimit = Config.Bind("Flight.PID", "IntegralLimit", 1.0f,
                "Custom PID anti-windup cap on integrator magnitude per axis.");

            KdPitch = Config.Bind("Flight.PID", "KdPitch", 0.18f,
                "Custom PID pitch D gain (damps inertial overshoot).");

            KdRoll = Config.Bind("Flight.PID", "KdRoll", -0.35f,
                "Custom PID roll D gain. Note: negative for this game's roll convention.");

            PidYawLimit = Config.Bind("Flight.PID", "PidYawLimit", 0.18f,
                "Hard cap on game-autopilot yaw output before stacking with player stick. 0..1.");

            TurnToFreelook = Config.Bind("Flight", "TurnToFreelook", false,
                "On Free Look release: true = plane turns to wherever camera ended up " +
                "(default cursor-pilot chase). false = camera animates back to plane's " +
                "velocity direction over FreelookRecoverySeconds; plane holds course.");

            FreelookRecoverySeconds = Config.Bind("Flight", "FreelookRecoverySeconds", 0.5f,
                "Seconds for camera pan/tilt to decay back to plane velocity direction after " +
                "Free Look release, when TurnToFreelook = false. Lower = snappier, higher = lazier.");

            FreelookGraceSeconds = Config.Bind("Flight", "FreelookGraceSeconds", 0.5f,
                "Extra delay after Free Look release (or camera-recovery completion) before " +
                "cursor pilot resumes plane control. Prevents jerk from stale PID state.");

            ShowHudLabel = Config.Bind("UI", "ShowHudLabel", true,
                "Show 'CURSOR' label near top of screen when active.");

            TelemetryEnabled = Config.Bind("Telemetry", "Enabled", true,
                "Capture snapshots of camera/plane/input state into a ring buffer.");

            TelemetryIntervalFrames = Config.Bind("Telemetry", "IntervalFrames", 50,
                "Frames between snapshots. FixedUpdate is 50Hz so 50 = ~1s. Min 1.");

            DumpOnCameraModeChange = Config.Bind("Telemetry", "DumpOnCameraModeChange", true,
                "Auto-dump when switching out of orbit camera mode.");

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded. Toggle: {ToggleKey.Value}, Dump: {DumpKey.Value}");
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
                Logger.LogInfo("[Config] reloaded from disk");
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
        }
    }
}
