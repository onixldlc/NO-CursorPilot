namespace NOCursorPilot.Controller
{
    /// <summary>
    /// Contract for a single cursor-pilot control method. Engagement gating and
    /// controller selection live in MainController; a provider only implements
    /// the control law and its own state reset.
    ///
    /// To add a method, subclass this, implement Engage()/Reset(), then wire it
    /// into MainController.Select().
    /// </summary>
    internal abstract class ControllerProvider
    {
        /// <summary>
        /// The control law. Called only when engaged; aircraft and inputs are
        /// guaranteed non-null. Write to inputs to take over flight controls.
        /// </summary>
        public abstract void Run(Aircraft aircraft, ControlInputs inputs);

        /// <summary>
        /// Clear all internal state (integrators, smoothing). Called by
        /// MainController on disengagement or controller switch, and by the
        /// patch as a safety net if a controller throws.
        /// </summary>
        public abstract void Reset();
    }
}
