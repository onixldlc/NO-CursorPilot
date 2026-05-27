using UnityEngine;

namespace NOCursorPilot.Controller
{
    /// <summary>
    /// Default controller: builds an aim point along the smoothed cursor
    /// direction, derives per-axis error proxies, and drives them with three
    /// single-axis PIDs. Owns its own camera/output smoothing, stick override,
    /// and telemetry. Ported from the original CursorFlightPatch logic.
    /// </summary>
    internal sealed class ControllerPID : ControllerProvider
    {
        private const float StickThreshold = 0.05f;

        private readonly PidController pitchPid = new PidController();
        private readonly PidController yawPid   = new PidController();
        private readonly PidController rollPid  = new PidController();

        // Smoothing state.
        private Vector3 smoothedCamForward;
        private bool initialized;
        private float smoothedPitch, smoothedRoll, smoothedYaw;

        protected override void Engage(Aircraft aircraft, ControlInputs inputs)
        {
            CameraStateManager camMgr = SceneSingleton<CameraStateManager>.i;
            if (camMgr == null) return;

            bool freeLookHeld = GameManager.playerInput != null &&
                                GameManager.playerInput.GetButton("Free Look");
            bool camRecovering = CameraOrbitPatch.IsRecovering;
            bool hasSavedDir = CameraOrbitPatch.TryGetSavedDirection(out Vector3 savedDir);
            bool useSavedDir = (freeLookHeld || camRecovering) && hasSavedDir;
            Vector3 rawCamForward = useSavedDir ? savedDir : camMgr.transform.forward;

            float dt = Time.fixedDeltaTime;
            Transform aircraftTf = aircraft.transform;

            // === TARGET SMOOTHING ===
            float camLambda = Plugin.TargetSmoothing.Value;
            float tCam = camLambda > 0f ? 1f - Mathf.Exp(-camLambda * dt) : 1f;

            if (!initialized)
            {
                smoothedCamForward = rawCamForward;
                initialized = true;
            }
            if (useSavedDir)
                smoothedCamForward = rawCamForward.normalized;
            else
                smoothedCamForward = Vector3.Slerp(smoothedCamForward, rawCamForward, tCam).normalized;

            float stickPitch = inputs.pitch;
            float stickRoll  = inputs.roll;
            float stickYaw   = inputs.yaw;

            // === ERROR PROXIES ===
            float aimDistance = Plugin.AimDistance.Value;
            Vector3 flyTarget = aircraftTf.position + smoothedCamForward * aimDistance;

            float sensitivity = Plugin.Sensitivity.Value;
            Vector3 localTarget = aircraftTf.InverseTransformPoint(flyTarget).normalized * sensitivity;

            Vector3 flightDir = (aircraft.rb != null && aircraft.rb.velocity.sqrMagnitude > 25f)
                ? aircraft.rb.velocity.normalized
                : aircraftTf.forward;
            float angleOff = Vector3.Angle(flightDir, flyTarget - aircraftTf.position);

            float wingsLevelErr = aircraftTf.right.y;
            float pitchErr = -localTarget.y;
            float yawErr   = localTarget.x;
            float aggressiveAngle = Plugin.AggressiveTurnAngle.Value;
            float influence = Mathf.InverseLerp(0f, aggressiveAngle, angleOff);
            float rollErr = Mathf.Lerp(wingsLevelErr, localTarget.x, influence);

            if (Plugin.InvertPitch.Value) pitchErr = -pitchErr;
            if (Plugin.InvertRoll.Value)  rollErr  = -rollErr;

            // Body-frame angular velocity for D-term
            Vector3 localAngVel = aircraft.rb != null
                ? aircraftTf.InverseTransformDirection(aircraft.rb.angularVelocity)
                : Vector3.zero;

            // === Sync PID gains from config (cheap; ConfigEntry getter is just a field read) ===
            float ki = Plugin.Ki.Value;
            float intLim = Plugin.IntegralLimit.Value;

            pitchPid.Kp = 1f; pitchPid.Ki = ki; pitchPid.Kd = Plugin.KdPitch.Value; pitchPid.IntegralLimit = intLim;
            yawPid.Kp   = 1f; yawPid.Ki   = ki; yawPid.Kd   = Plugin.KdYaw.Value;   yawPid.IntegralLimit   = intLim;
            rollPid.Kp  = 1f; rollPid.Ki  = ki; rollPid.Kd  = Plugin.KdRoll.Value;  rollPid.IntegralLimit  = intLim;

            // === Compute PID outputs (Kp=1 because error already scaled by Sensitivity) ===
            // No freezeI: integrator anti-windup is handled by IntegralLimit clamp.
            float pitch = pitchPid.Compute(pitchErr, localAngVel.x, dt, false);
            float yaw   = Plugin.UseYaw.Value
                ? yawPid.Compute(yawErr, localAngVel.y, dt, false)
                : 0f;
            float roll  = rollPid.Compute(rollErr, localAngVel.z, dt, false);

            float rawWantPitch = Mathf.Clamp(pitch, -1f, 1f);
            float rawWantRoll  = Mathf.Clamp(roll, -1f, 1f);
            float rawWantYaw   = Mathf.Clamp(yaw, -1f, 1f);

            // === OUTPUT SMOOTHING ===
            float outLambda = Plugin.OutputSmoothing.Value;
            float tOut = outLambda > 0f ? 1f - Mathf.Exp(-outLambda * dt) : 1f;
            smoothedPitch = Mathf.Lerp(smoothedPitch, rawWantPitch, tOut);
            smoothedRoll  = Mathf.Lerp(smoothedRoll,  rawWantRoll,  tOut);
            smoothedYaw   = Mathf.Lerp(smoothedYaw,   rawWantYaw,   tOut);

            // === STICK OVERRIDE: yield to manual input ===
            string gate;
            float finalPitch, finalRoll, finalYaw;

            bool stickHeld = Mathf.Abs(stickPitch) > StickThreshold ||
                             Mathf.Abs(stickRoll)  > StickThreshold ||
                             Mathf.Abs(stickYaw)   > StickThreshold;

            if (stickHeld)
            {
                gate = "stickOverride";
                finalPitch = stickPitch; finalRoll = stickRoll; finalYaw = stickYaw;
            }
            else
            {
                gate = useSavedDir ? (freeLookHeld ? "freeLookTrack" : "savedDirHold") : "wrote";
                inputs.pitch = smoothedPitch;
                inputs.roll  = smoothedRoll;
                inputs.yaw   = smoothedYaw;
                finalPitch = smoothedPitch; finalRoll = smoothedRoll; finalYaw = smoothedYaw;
            }

            // Push live state to web telemetry AFTER inputs are finalized for this frame.
            TelemetryWebServer.UpdateState(aircraft, inputs, smoothedCamForward);

            if (Plugin.TelemetryEnabled.Value)
            {
                TelemetryRecorder.TryRecord(new TelemetryRecorder.Snapshot
                {
                    time               = Time.fixedTime,
                    camForwardRaw      = rawCamForward,
                    camForwardSmoothed = smoothedCamForward,
                    planeForward       = aircraftTf.forward,
                    planeRight         = aircraftTf.right,
                    planeUp            = aircraftTf.up,
                    velocity           = aircraft.rb != null ? aircraft.rb.velocity : Vector3.zero,
                    angVel             = localAngVel,
                    speed              = aircraft.speed,
                    radarAlt           = aircraft.radarAlt,
                    stickPitch         = stickPitch,
                    stickRoll          = stickRoll,
                    stickYaw           = stickYaw,
                    localTarget        = localTarget,
                    angleOff           = angleOff,
                    fade               = 1f,
                    outPitch           = finalPitch,
                    outRoll            = finalRoll,
                    outYaw             = finalYaw,
                    camMode            = CameraStateManager.cameraMode.ToString(),
                    gate               = gate,
                    iPitch             = pitchPid.Integral,
                    iYaw               = yawPid.Integral,
                    iRoll              = rollPid.Integral,
                });
            }
        }

        public override void Reset()
        {
            initialized = false;
            smoothedPitch = 0f;
            smoothedRoll = 0f;
            smoothedYaw = 0f;
            pitchPid.Reset();
            yawPid.Reset();
            rollPid.Reset();
        }
    }
}
