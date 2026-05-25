using UnityEngine;

namespace NOCursorPilot
{
    // Per-aircraft PID that pulls gains (P/I/D + referenceAirspeed) from the game's
    // Autopilot.forwardFlightController, then runs our own algorithm:
    //   - measurement-D (uses body angular velocity, no derivative-on-setpoint kick)
    //   - freeze+decay anti-windup (PidController-style, not game's hard-reset)
    //   - airspeed compensation on D (game-style: scale by (refAS/AS)^2, clamped 0..4)
    //
    // Direct typed access via IgnoresAccessChecksTo — see src/IgnoresAccessChecksTo.cs.
    // NOT wired into CursorFlightPatch yet. Drop-in alternative for future swap/blend.
    //
    // Lifecycle (when wired):
    //   internalPid.Resync(aircraft);  // call on aircraft swap; cheap no-op if same aircraft
    //   float pitch = internalPid.ComputePitch(pitchErr, localAngVel.x, airspeed, dt);
    //   float yaw   = internalPid.ComputeYaw  (yawErr,   localAngVel.y, airspeed, dt);
    //   float roll  = internalPid.ComputeRoll (rollErr,  localAngVel.z, airspeed, dt);
    internal class InternalPidController
    {
        // ===== Anti-windup / saturation knobs (PidController parity) =====
        public float IntegralLimit       = 1f;
        public float SaturationThreshold = 0.95f;
        public float FrozenDecay         = 0.95f;

        // ===== Per-axis state + gains pulled from game =====
        private class Axis
        {
            public float P, I, D;        // gains pulled from PIDFactors at Resync
            public float Integral;       // our state
            public float Output;         // last clamped output (for telemetry)
        }
        private readonly Axis _pitch = new Axis();
        private readonly Axis _yaw   = new Axis();
        private readonly Axis _roll  = new Axis();

        private float    _referenceAirspeed;
        private Aircraft _cachedAircraft;
        private bool     _gainsValid;

        public float PitchIntegral => _pitch.Integral;
        public float YawIntegral   => _yaw.Integral;
        public float RollIntegral  => _roll.Integral;
        public bool  GainsValid    => _gainsValid;

        // Pull current per-aircraft gains. Returns true if game PID found and gains loaded.
        // Safe to call every frame — early-outs if aircraft unchanged.
        public bool Resync(Aircraft aircraft)
        {
            if (aircraft == null)                          { _gainsValid = false; return false; }
            if (_cachedAircraft == aircraft && _gainsValid) return true;

            _cachedAircraft = aircraft;
            _gainsValid     = false;

            if (aircraft.cockpit == null) return false;

            var autopilot = aircraft.cockpit.GetComponent<Autopilot>();
            if (autopilot == null) return false;

            var ffc = autopilot.forwardFlightController;
            if (ffc == null) return false;
            if (ffc.pitchFlightPID == null || ffc.yawFlightPID == null || ffc.rollFlightPID == null) return false;

            LoadGains(_pitch, ffc.pitchFlightPID);
            LoadGains(_yaw,   ffc.yawFlightPID);
            LoadGains(_roll,  ffc.rollFlightPID);
            _referenceAirspeed = ffc.referenceAirspeed;
            _gainsValid = true;
            return true;
        }

        public float ComputePitch(float error, float rate, float airspeed, float dt) => Compute(_pitch, error, rate, airspeed, dt);
        public float ComputeYaw  (float error, float rate, float airspeed, float dt) => Compute(_yaw,   error, rate, airspeed, dt);
        public float ComputeRoll (float error, float rate, float airspeed, float dt) => Compute(_roll,  error, rate, airspeed, dt);

        public void Reset()
        {
            _pitch.Integral = _yaw.Integral = _roll.Integral = 0f;
            _pitch.Output   = _yaw.Output   = _roll.Output   = 0f;
            _gainsValid     = false;
            _cachedAircraft = null;
        }

        // Algorithm: measurement-D + freeze-decay anti-windup + airspeed comp on D.
        //   num = clamp(refAS^2 / max(AS^2, 10), 0, 4)          // game-style airspeed scaling
        //   pd  = P*error - D*rate*num                          // measurement-D (no setpoint kick)
        //   if (saturated) decay integrator; else accumulate clamped
        //   out = clamp(pd + I*integrator, -1, 1)
        private float Compute(Axis ax, float error, float rate, float airspeed, float dt)
        {
            if (!_gainsValid) { ax.Output = 0f; return 0f; }

            float num = Mathf.Clamp(
                _referenceAirspeed * _referenceAirspeed / Mathf.Max(airspeed * airspeed, 10f),
                0f, 4f);

            float pd = ax.P * error - ax.D * rate * num;

            bool saturated = Mathf.Abs(pd + ax.I * ax.Integral) >= SaturationThreshold;
            if (saturated)
                ax.Integral *= FrozenDecay;
            else
                ax.Integral = Mathf.Clamp(ax.Integral + error * dt, -IntegralLimit, IntegralLimit);

            ax.Output = Mathf.Clamp(pd + ax.I * ax.Integral, -1f, 1f);
            return ax.Output;
        }

        private static void LoadGains(Axis ax, PIDFactors f)
        {
            ax.P = f.P;
            ax.I = f.I;
            ax.D = f.D;
        }
    }
}
