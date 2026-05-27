using NOCursorPilot.Controller;

namespace NOCursorPilot
{
    /// <summary>
    /// Router for the cursor-pilot. Owns the engagement gates and decides which
    /// ControllerProvider drives the aircraft each frame. The Harmony patch just
    /// forwards here; the actual control laws live in ControllerProvider
    /// subclasses under src/controller/.
    ///
    /// To add a guidance method: instantiate it below and return it from
    /// Select() under whatever condition you want.
    /// </summary>
    internal static class MainController
    {
        // Each controller is stateful (integrators, smoothing), so keep one
        // long-lived instance per method rather than reallocating per frame.
        private static readonly ControllerProvider pid = new ControllerPID();
        // private static readonly ControllerProvider autopilot = new AutopilotController();

        // Controller that drove last frame; tracked so we can reset the right
        // one on disengage or when switching methods.
        private static ControllerProvider active;

        public static void Run(PilotPlayerState instance)
        {
            if (!Plugin.Enabled)                                   { Reset(); return; }
            if (instance?.pilot?.aircraft == null)                 { Reset(); return; }
            if (!GameManager.flightControlsEnabled)                { Reset(); return; }
            if (CameraStateManager.cameraMode != CameraMode.orbit) { Reset(); return; }

            ControlInputs inputs = instance.controlInputs;
            if (inputs == null) return;

            ControllerProvider next = Select(instance);

            if (active != null && active != next) active.Reset();
            active = next;

            active.Run(instance.pilot.aircraft, inputs);
        }

        /// <summary>
        /// Single place to choose the active control method. Edit this to add or
        /// switch controllers, e.g.:
        ///     return Plugin.UseAutopilot.Value ? autopilot : pid;
        /// </summary>
        private static ControllerProvider Select(PilotPlayerState instance)
        {
            return pid;
        }

        /// <summary>Clear the active controller's state (disengage / safety net).</summary>
        public static void Reset()
        {
            active?.Reset();
        }
    }
}
