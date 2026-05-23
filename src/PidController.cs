using UnityEngine;

namespace NOCursorPilot
{
    /// <summary>
    /// Single-axis PID controller with conditional-integration anti-windup.
    /// Stateful: keeps integrator between Compute() calls. Call Reset() on disengagement.
    /// </summary>
    internal class PidController
    {
        public float Kp = 1f;
        public float Ki = 0f;
        public float Kd = 0f;
        public float IntegralLimit = 1f;
        public float OutputMin = -1f;
        public float OutputMax = 1f;

        // Threshold above which conditional integration freezes (prevents windup at saturation).
        public float SaturationThreshold = 0.95f;
        // Per-frame decay applied to integrator when frozen.
        public float FrozenDecay = 0.95f;

        public float Integral { get; private set; }

        /// <summary>
        /// Compute PID output for one step.
        /// </summary>
        /// <param name="error">Setpoint - measurement (or signed error proxy).</param>
        /// <param name="rate">Rate-of-change of the measurement (used for D, with negative sign internally).</param>
        /// <param name="dt">Time step in seconds (Time.fixedDeltaTime).</param>
        /// <param name="freeze">Force-freeze integrator regardless of saturation check (e.g. inside deadzone).</param>
        public float Compute(float error, float rate, float dt, bool freeze = false)
        {
            float pd = Kp * error - Kd * rate;

            bool saturated = Mathf.Abs(pd + Ki * Integral) >= SaturationThreshold;
            if (freeze || saturated)
            {
                Integral *= FrozenDecay;
            }
            else
            {
                Integral = Mathf.Clamp(Integral + error * dt, -IntegralLimit, IntegralLimit);
            }

            return Mathf.Clamp(pd + Ki * Integral, OutputMin, OutputMax);
        }

        public void Reset()
        {
            Integral = 0f;
        }
    }
}
