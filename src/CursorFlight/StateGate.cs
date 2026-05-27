using UnityEngine;

namespace NOCursorPilot.CursorFlight
{
    // Hard gates: mod disabled, no aircraft, wrong camera mode, chat open, etc.
    // ShouldRun returns false -> caller resets state.
    // Soft gate: stick override (caller still updates cam tracking but lets pilot stick win).
    internal static class StateGate
    {
        public const float StickThreshold = 0.05f;

        public static bool ShouldRun(PilotPlayerState instance, out Aircraft aircraft, out ControlInputs inputs)
        {
            aircraft = null;
            inputs   = null;

            if (!Plugin.Enabled) return false;
            if (instance?.pilot?.aircraft == null) return false;
            if (!GameManager.flightControlsEnabled) return false;
            if (Cursor.visible) return false;
            if (CursorManager.GetFlag(CursorFlags.Chat)) return false;
            if (DynamicMap.mapMaximized) return false;
            if (CameraStateManager.cameraMode != CameraMode.orbit) return false;

            aircraft = instance.pilot.aircraft;
            inputs   = instance.controlInputs;
            return inputs != null;
        }

        public static bool IsStickHeld(ControlInputs inputs)
        {
            return Mathf.Abs(inputs.pitch) > StickThreshold
                || Mathf.Abs(inputs.roll)  > StickThreshold
                || Mathf.Abs(inputs.yaw)   > StickThreshold;
        }
    }
}
