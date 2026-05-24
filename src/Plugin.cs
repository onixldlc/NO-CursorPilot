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
        public const string PluginVersion = "0.2.1";

        public static bool Enabled;
        public static ManualLogSource LogSource;
        private Harmony _harmony;
        private CameraMode _lastCamMode;

        public static ConfigEntry<KeyboardShortcut> ToggleKey;
        public static ConfigEntry<KeyboardShortcut> DumpKey;
        public static ConfigEntry<KeyboardShortcut> DumpProfileKey;
        public static ConfigEntry<KeyboardShortcut> ReloadConfigKey;

        public static ConfigEntry<float> Sensitivity;
        public static ConfigEntry<float> TargetSmoothing;
        public static ConfigEntry<float> OutputSmoothing;
        public static ConfigEntry<float> Ki;
        public static ConfigEntry<float> IntegralLimit;
        public static ConfigEntry<float> KdPitch;
        public static ConfigEntry<float> KdYaw;
        public static ConfigEntry<float> KdRoll;
        public static ConfigEntry<float> AggressiveTurnAngle;
        public static ConfigEntry<float> AimDistance;
        public static ConfigEntry<bool>  UseYaw;
        public static ConfigEntry<bool>  InvertPitch;
        public static ConfigEntry<bool>  InvertRoll;
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
                new KeyboardShortcut(KeyCode.F1),
                "Dump last N telemetry snapshots to the BepInEx log.");

            DumpProfileKey = Config.Bind("Controls", "DumpProfileKey",
                new KeyboardShortcut(KeyCode.F2),
                "Dump current PID + tuning values to BepInEx/config/NOCursorPilot.profiles/ as a .cfg file.");

            ReloadConfigKey = Config.Bind("Controls", "ReloadConfigKey",
                new KeyboardShortcut(KeyCode.F3),
                "Re-read the BepInEx config file from disk. Use after editing values externally.");

            Sensitivity = Config.Bind("Flight", "Sensitivity", 5.0f,
                "Proportional gain on local-space error. Higher = more aggressive.");

            TargetSmoothing = Config.Bind("Flight", "TargetSmoothing", 3.0f,
                "Camera-direction smoothing lambda (1 - exp(-lambda * dt)). " +
                "Higher = snappier, lower = lazier. 0 = no smoothing.");

            OutputSmoothing = Config.Bind("Flight", "OutputSmoothing", 5.0f,
                "Stick-output smoothing lambda. Damps oscillation at saturated inputs. " +
                "Higher = less damping (faster response), lower = more damping. 0 = no smoothing.");

            Ki = Config.Bind("Flight.PID", "Ki", 0.05f,
                "Integral gain (single value, applied to all axes). Eliminates steady-state error. " +
                "Try 0-0.2. 0 = no I-term (PD only).");

            IntegralLimit = Config.Bind("Flight.PID", "IntegralLimit", 1.0f,
                "Anti-windup cap on integrator magnitude per axis.");

            KdPitch = Config.Bind("Flight.PID", "KdPitch", 0.18f,
                "Pitch D gain. Subtracts (KdPitch * body_pitch_rate) from pitch command. " +
                "Damps inertial overshoot. Try 0.02-0.2. Negate if oscillation worsens.");

            KdYaw = Config.Bind("Flight.PID", "KdYaw", 0.15f,
                "Yaw D gain. Try 0.02-0.2. Negate if needed.");

            KdRoll = Config.Bind("Flight.PID", "KdRoll", -0.35f,
                "Roll D gain. Try 0.02-0.2. Negate if needed.");

            AggressiveTurnAngle = Config.Bind("Flight", "AggressiveTurnAngle", 3.0f,
                "Degrees off target where full aggressive bank applies. " +
                "Below this angle blends toward wings-level. Lower = more banking even at small errors.");

            AimDistance = Config.Bind("Flight", "AimDistance", 500.0f,
                "Meters ahead the virtual target is projected. " +
                "Larger = smoother but less responsive.");

            UseYaw = Config.Bind("Flight", "UseYaw", true,
                "Apply yaw correction in addition to pitch/roll.");

            InvertPitch = Config.Bind("Flight", "InvertPitch", false,
                "Flip pitch sign if plane pitches wrong way.");

            InvertRoll = Config.Bind("Flight", "InvertRoll", false,
                "Flip roll sign if plane banks wrong way.");

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
