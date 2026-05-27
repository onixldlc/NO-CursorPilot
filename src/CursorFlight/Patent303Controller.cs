using UnityEngine;

namespace NOCursorPilot.CursorFlight
{
    // Patent 303A adaptive chase controller. Replaces chase PIDs.
    // Reference: .reference/how_to_build/13-WT_MouseAim_Implementation_Guide.md.
    //
    // Per tick:
    //   1. Measure angular acceleration (diff of localAngVel since last call).
    //   2. Update per-axis "effectiveness" estimate = |angAccel / prevInput| (smoothed).
    //   3. Derive per-axis coefficients = targetResponseRate / effectiveness (clamped).
    //   4. Wobble detector tracks sign-changes of angular rate; boosts damping if exceeded.
    //   5. Output = error * coeff - angVel * damping * wobbleBoost.
    //      Pitch and yaw use localCamFwd components directly.
    //      Roll uses bank-to-turn: desiredBank = lateralErr * factor (clamped),
    //                             bankError = (desiredBank - currentBank) in radians.
    //
    // The caller passes `measureEffectiveness=false` when the game wasn't actually
    // driven by our outputs last frame (stick override, freelook, recovery), to avoid
    // contaminating the effectiveness estimate with inputs the plant didn't see.
    internal class Patent303Controller
    {
        private Vector3 _prevLocalAngVel;
        private bool    _haveHistory;

        private float _prevPitchInput;
        private float _prevRollInput;
        private float _prevYawInput;

        private float _pitchEff = 1f;
        private float _rollEff  = 1f;
        private float _yawEff   = 1f;

        private float[] _pitchRateHist = new float[1];
        private float[] _rollRateHist  = new float[1];
        private float[] _yawRateHist   = new float[1];
        private int     _histIdx;
        private int     _histSize = 1;

        // Diagnostic outputs (last computed values), exposed for telemetry.
        public float LastPitchEff, LastRollEff, LastYawEff;
        public float LastPitchCoeff, LastRollCoeff, LastYawCoeff;
        public float LastPitchDamp, LastRollDamp, LastYawDamp;
        public int   LastPitchWobble, LastRollWobble, LastYawWobble;
        public float LastDesiredBank, LastCurrentBank, LastBankError;

        public void Reset()
        {
            _prevLocalAngVel = Vector3.zero;
            _haveHistory     = false;
            _prevPitchInput  = 0f;
            _prevRollInput   = 0f;
            _prevYawInput    = 0f;
            _pitchEff = _rollEff = _yawEff = 1f;
            for (int i = 0; i < _histSize; i++)
            {
                _pitchRateHist[i] = 0f;
                _rollRateHist[i]  = 0f;
                _yawRateHist[i]   = 0f;
            }
            _histIdx = 0;
        }

        private void EnsureHistory(int size)
        {
            if (size < 2) size = 2;
            if (size == _histSize) return;
            _histSize = size;
            _pitchRateHist = new float[size];
            _rollRateHist  = new float[size];
            _yawRateHist   = new float[size];
            _histIdx = 0;
        }

        public void Compute(
            Vector3 localCamFwd,
            Vector3 localAngVel,
            Transform aircraftTf,
            Chase303Params p,
            float dt,
            bool measureEffectiveness,
            out float pitchOut,
            out float yawOut,
            out float rollOut)
        {
            EnsureHistory(Mathf.Max(2, p.WobbleWindow));

            // === Angular acceleration ===
            Vector3 angAccel = _haveHistory && dt > 0f
                ? (localAngVel - _prevLocalAngVel) / dt
                : Vector3.zero;
            _prevLocalAngVel = localAngVel;
            _haveHistory = true;

            // === Effectiveness estimator ===
            // Only update if we actually drove the plant last frame AND our prev input
            // was above the noise threshold. Smoothed EMA on |angAccel / prevInput|.
            if (measureEffectiveness)
            {
                if (Mathf.Abs(_prevPitchInput) > p.MinInputThreshold)
                {
                    float measured = angAccel.x / _prevPitchInput;
                    _pitchEff = _pitchEff * (1f - p.EffSmoothing) + Mathf.Abs(measured) * p.EffSmoothing;
                }
                if (Mathf.Abs(_prevRollInput) > p.MinInputThreshold)
                {
                    float measured = angAccel.z / _prevRollInput;
                    _rollEff = _rollEff * (1f - p.EffSmoothing) + Mathf.Abs(measured) * p.EffSmoothing;
                }
                if (Mathf.Abs(_prevYawInput) > p.MinInputThreshold)
                {
                    float measured = angAccel.y / _prevYawInput;
                    _yawEff = _yawEff * (1f - p.EffSmoothing) + Mathf.Abs(measured) * p.EffSmoothing;
                }
            }

            _pitchEff = Mathf.Clamp(_pitchEff, p.EffMin, p.EffMax);
            _rollEff  = Mathf.Clamp(_rollEff,  p.EffMin, p.EffMax);
            _yawEff   = Mathf.Clamp(_yawEff,   p.EffMin, p.EffMax);

            // === Adaptive coefficients ===
            float pitchCoeff = Mathf.Clamp(
                p.TargetResponseRate / Mathf.Max(_pitchEff, p.EffMin),
                p.PitchCoeffMin, p.PitchCoeffMax);
            float rollCoeff  = Mathf.Clamp(
                p.TargetResponseRate / Mathf.Max(_rollEff, p.EffMin),
                p.RollCoeffMin, p.RollCoeffMax);
            float yawCoeff   = Mathf.Clamp(
                (p.TargetResponseRate * p.YawMultiplier) / Mathf.Max(_yawEff, p.EffMin),
                p.YawCoeffMin, p.YawCoeffMax);

            float pitchDamp = Mathf.Clamp(pitchCoeff * p.DampingRatio, p.PitchDampMin, p.PitchDampMax);
            float rollDamp  = Mathf.Clamp(rollCoeff  * p.DampingRatio, p.RollDampMin,  p.RollDampMax);
            float yawDamp   = Mathf.Clamp(yawCoeff   * p.DampingRatio, p.YawDampMin,   p.YawDampMax);

            // === Wobble detector ===
            int slot = _histIdx % _histSize;
            _pitchRateHist[slot] = localAngVel.x;
            _rollRateHist[slot]  = localAngVel.z;
            _yawRateHist[slot]   = localAngVel.y;
            _histIdx++;

            int pitchWobble = CountSignChanges(_pitchRateHist);
            int rollWobble  = CountSignChanges(_rollRateHist);
            int yawWobble   = CountSignChanges(_yawRateHist);

            float pitchBoost = pitchWobble > p.WobbleThreshold ? p.WobbleBoost : 1f;
            float rollBoost  = rollWobble  > p.WobbleThreshold ? p.WobbleBoost : 1f;
            float yawBoost   = yawWobble   > p.WobbleThreshold ? p.WobbleBoost : 1f;

            // === Errors ===
            // Sign convention matches existing mod:
            //   pitch error = -localCamFwd.y (nose-up needs negative input)
            //   yaw   error =  localCamFwd.x (target right needs positive yaw)
            //   roll  bank-to-turn: target right -> right bank
            float lateralError  = localCamFwd.x;
            float verticalError = -localCamFwd.y;

            // === Pitch + yaw outputs ===
            pitchOut = verticalError * pitchCoeff - localAngVel.x * pitchDamp * pitchBoost;
            yawOut   = lateralError  * yawCoeff   - localAngVel.y * yawDamp   * yawBoost;

            // === Bank-to-turn roll ===
            // currentBank derived geometrically from the wing right-vector (matches
            // TelemetryWebServer._wingAngle). Avoids Unity eulerAngles.z sign
            // ambiguity which caused runaway feedback in earlier revs: when
            // eulerAngles.z's sign disagreed with desiredBank's sign convention,
            // bankError grew rather than shrank as the plane responded.
            //
            // Convention here: bank > 0 means right wing tilted down (right bank).
            // -asin(right.y) > 0 when right.y < 0, i.e. right wing below horizon.
            float desiredBank = Mathf.Clamp(lateralError * p.BankToTurnFactor, -p.BankClampDeg, p.BankClampDeg);
            float currentBank = -Mathf.Asin(Mathf.Clamp(aircraftTf.right.y, -1f, 1f)) * Mathf.Rad2Deg;
            float bankError = (desiredBank - currentBank) * Mathf.Deg2Rad;
            rollOut = bankError * rollCoeff - localAngVel.z * rollDamp * rollBoost;

            // === Diagnostic snapshot ===
            LastPitchEff   = _pitchEff;
            LastRollEff    = _rollEff;
            LastYawEff     = _yawEff;
            LastPitchCoeff = pitchCoeff;
            LastRollCoeff  = rollCoeff;
            LastYawCoeff   = yawCoeff;
            LastPitchDamp  = pitchDamp;
            LastRollDamp   = rollDamp;
            LastYawDamp    = yawDamp;
            LastPitchWobble = pitchWobble;
            LastRollWobble  = rollWobble;
            LastYawWobble   = yawWobble;
            LastDesiredBank = desiredBank;
            LastCurrentBank = currentBank;
            LastBankError   = bankError;

            // Caller will store post-clamp outputs via StoreInputs() AFTER blend with leveler.
        }

        // Record what was actually sent to game (post-blend, post-smooth, post-gate)
        // so next-tick effectiveness estimate uses the real plant input.
        public void StoreInputs(float pitch, float roll, float yaw)
        {
            _prevPitchInput = pitch;
            _prevRollInput  = roll;
            _prevYawInput   = yaw;
        }

        private static int CountSignChanges(float[] vals)
        {
            int n = 0;
            for (int i = 1; i < vals.Length; i++)
            {
                if ((vals[i] > 0f && vals[i - 1] < 0f) || (vals[i] < 0f && vals[i - 1] > 0f))
                    n++;
            }
            return n;
        }
    }
}
