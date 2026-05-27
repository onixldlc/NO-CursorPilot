using UnityEngine;

namespace NOCursorPilot.Controller
{
    /// <summary>
    /// Base for all cursor-pilot controllers. Owns the universal engagement
    /// gating (Template Method): Run() decides whether the mod should act this
    /// frame and resets state on disengagement; subclasses implement only the
    /// control law in Engage() plus their own state reset.
    ///
    /// To prototype a new method, subclass this and implement Engage()/Reset(),
    /// then swap the instance in CursorFlightPatch. The patch stays dumb and the
    /// gates are shared by every controller automatically.
    /// </summary>
    internal abstract class ControllerProvider
    {
        /// <summary>
        /// Frame entry point (called from the PlayerAxisControls Postfix).
        /// Non-virtual on purpose: the engagement contract is identical for all
        /// controllers. Runs the hard gates, then dispatches to Engage().
        /// </summary>
        public void Run(PilotPlayerState instance)
        {
            // Universal hard gates: mod not actively engaged -> reset so the
            // next engagement starts fresh.
            if (!Plugin.Enabled)                                   { Reset(); return; }
            if (instance?.pilot?.aircraft == null)                 { Reset(); return; }
            if (!GameManager.flightControlsEnabled)                { Reset(); return; }
            if (CameraStateManager.cameraMode != CameraMode.orbit) { Reset(); return; }

            ControlInputs inputs = instance.controlInputs;
            if (inputs == null) return;

            Engage(instance.pilot.aircraft, inputs);
        }

        /// <summary>
        /// The control law. Called only when engaged; aircraft and inputs are
        /// guaranteed non-null. Write to inputs to take over flight controls.
        /// </summary>
        protected abstract void Engage(Aircraft aircraft, ControlInputs inputs);

        /// <summary>
        /// Clear all internal state (integrators, smoothing). Called by the base
        /// on disengagement, and by the patch as a safety net if Run() throws.
        /// </summary>
        public abstract void Reset();
    }
}
