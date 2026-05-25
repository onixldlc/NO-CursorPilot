using HarmonyLib;
using UnityEngine;

namespace NOCursorPilot
{
    [HarmonyPatch(typeof(PilotPlayerState), "PlayerAxisControls")]
    internal static class CursorFlightPatch
    {
        private static Vector3 smoothedCamForward;
        private static float smoothedPitch, smoothedRoll, smoothedYaw;
        private static bool initialized;

        // Flips false on every disengage (ResetState). First frame inside orbit after a disengage
        // zeros the stick to flush whatever input was held in cockpit cam, then this latches true.
        private static bool wasOrbitLastFrame;

        private static readonly PidController pitchPid = new PidController();
        private static readonly PidController yawPid   = new PidController();
        private static readonly PidController rollPid  = new PidController();
        private static readonly InternalPidController internalPid = new InternalPidController();

        // Roll error = angle between actual plane-up and a desired plane-up tilted by yaw err.
        private const float YawToRollFactor = 0.30f;
        private const float YawToRollLimit  = 38f;
        private const float RollErrorLimit  = 3.2f;

        static void Postfix(PilotPlayerState __instance)
        {
            // Hard gates: mod not actively engaged. Reset all state so next engagement starts fresh.
            if (!Plugin.Enabled)                                   { ResetState(); return; }
            if (__instance?.pilot?.aircraft == null)               { ResetState(); return; }
            if (!GameManager.flightControlsEnabled)                { ResetState(); return; }
            if (Cursor.visible)                                    { ResetState(); return; }
            if (CursorManager.GetFlag(CursorFlags.Chat))           { ResetState(); return; }
            if (DynamicMap.mapMaximized)                           { ResetState(); return; }
            if (CameraStateManager.cameraMode != CameraMode.orbit) { ResetState(); return; }

            Aircraft aircraft = __instance.pilot.aircraft;
            ControlInputs inputs = __instance.controlInputs;
            if (inputs == null) return;

            // First frame inside orbit cam after a disengage: flush any stick value held over
            // from cockpit cam. After this, normal stickOverride logic applies.
            if (!wasOrbitLastFrame)
            {
                inputs.pitch = 0f;
                inputs.roll  = 0f;
                inputs.yaw   = 0f;
                wasOrbitLastFrame = true;
            }

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

            // angleOff is needed by both PID paths (custom uses it for aggressive-bank blend;
            // internal path uses it for telemetry / stick-override decisions).
            float aimDistance = Plugin.AimDistance.Value;
            Vector3 flyTarget = aircraftTf.position + smoothedCamForward * aimDistance;
            Vector3 flightDir = (aircraft.rb != null && aircraft.rb.velocity.sqrMagnitude > 25f)
                ? aircraft.rb.velocity.normalized
                : aircraftTf.forward;
            float angleOff = Vector3.Angle(flightDir, flyTarget - aircraftTf.position);

            // Body-frame angular velocity for D-term (both paths use this).
            Vector3 localAngVel = aircraft.rb != null
                ? aircraftTf.InverseTransformDirection(aircraft.rb.angularVelocity)
                : Vector3.zero;

            float pitchErr, yawErr, rollErr;
            float pitch, yaw, roll;
            Vector3 localTarget;

            // Resolve effective mode. If Internal/Hybrid is requested but Resync fails, fall back to Custom.
            var mode = Plugin.PidMode.Value;
            bool needInternal = mode != Plugin.CursorPidMode.Custom;
            if (needInternal && !internalPid.Resync(aircraft))
                mode = Plugin.CursorPidMode.Custom;

            float airspeed = aircraft.rb != null ? aircraft.rb.velocity.magnitude : 0f;

            if (mode == Plugin.CursorPidMode.Internal)
            {
                // === ALL-INTERNAL PATH: angle errors (degrees) via TargetCalc, game-tuned PID ===
                Vector3 planeFwd   = aircraftTf.forward;
                Vector3 planeRight = aircraftTf.right;
                Vector3 planeUp    = aircraftTf.up;

                pitchErr = TargetCalc.GetAngleOnAxis(planeFwd, smoothedCamForward, planeRight);
                yawErr   = TargetCalc.GetAngleOnAxis(planeFwd, smoothedCamForward, planeUp);
                rollErr  = ComputeAngleRollError(yawErr, planeFwd, planeRight, planeUp);

                if (Plugin.InvertPitch.Value) pitchErr = -pitchErr;
                if (Plugin.InvertRoll.Value)  rollErr  = -rollErr;

                pitch = internalPid.ComputePitch(pitchErr, localAngVel.x, airspeed, dt);
                yaw   = Plugin.UseYaw.Value
                    ? internalPid.ComputeYaw(yawErr, localAngVel.y, airspeed, dt)
                    : 0f;
                roll  = internalPid.ComputeRoll(rollErr, localAngVel.z, airspeed, dt);

                localTarget = aircraftTf.InverseTransformPoint(flyTarget).normalized;
            }
            else
            {
                // === CUSTOM PATH for pitch/roll. yaw may be hybrid-swapped to internal below. ===
                float sensitivity = Plugin.Sensitivity.Value;
                localTarget = aircraftTf.InverseTransformPoint(flyTarget).normalized * sensitivity;

                float wingsLevelErr = aircraftTf.right.y;
                pitchErr = -localTarget.y;
                yawErr   = localTarget.x;
                float aggressiveAngle = Plugin.AggressiveTurnAngle.Value;
                float influence = Mathf.InverseLerp(0f, aggressiveAngle, angleOff);
                rollErr = Mathf.Lerp(wingsLevelErr, localTarget.x, influence);

                if (Plugin.InvertPitch.Value) pitchErr = -pitchErr;
                if (Plugin.InvertRoll.Value)  rollErr  = -rollErr;

                float ki = Plugin.Ki.Value;
                float intLim = Plugin.IntegralLimit.Value;
                pitchPid.Kp = 1f; pitchPid.Ki = ki; pitchPid.Kd = Plugin.KdPitch.Value; pitchPid.IntegralLimit = intLim;
                yawPid.Kp   = 1f; yawPid.Ki   = ki; yawPid.Kd   = Plugin.KdYaw.Value;   yawPid.IntegralLimit   = intLim;
                rollPid.Kp  = 1f; rollPid.Ki  = ki; rollPid.Kd  = Plugin.KdRoll.Value;  rollPid.IntegralLimit  = intLim;

                pitch = pitchPid.Compute(pitchErr, localAngVel.x, dt, false);
                roll  = rollPid.Compute(rollErr,  localAngVel.z, dt, false);

                if (mode == Plugin.CursorPidMode.HybridInternalYaw && Plugin.UseYaw.Value)
                {
                    // Override yaw with internal PID using angle error (degrees).
                    float yawErrDeg = TargetCalc.GetAngleOnAxis(aircraftTf.forward, smoothedCamForward, aircraftTf.up);
                    yaw = internalPid.ComputeYaw(yawErrDeg, localAngVel.y, airspeed, dt);
                }
                else
                {
                    yaw = Plugin.UseYaw.Value
                        ? yawPid.Compute(yawErr, localAngVel.y, dt, false)
                        : 0f;
                }
            }

            float rawWantPitch = Mathf.Clamp(pitch, -1f, 1f);
            float rawWantRoll  = Mathf.Clamp(roll, -1f, 1f);
            float rawWantYaw   = Mathf.Clamp(yaw, -1f, 1f);

            // === OUTPUT SMOOTHING ===
            float outLambda = Plugin.OutputSmoothing.Value;
            float tOut = outLambda > 0f ? 1f - Mathf.Exp(-outLambda * dt) : 1f;
            smoothedPitch = Mathf.Lerp(smoothedPitch, rawWantPitch, tOut);
            smoothedRoll  = Mathf.Lerp(smoothedRoll,  rawWantRoll,  tOut);
            smoothedYaw   = Mathf.Lerp(smoothedYaw,   rawWantYaw,   tOut);

            // === SOFT GATES ===
            string gate;
            float finalPitch, finalRoll, finalYaw;

            const float stickThreshold = 0.05f;
            bool stickHeld = Mathf.Abs(stickPitch) > stickThreshold
                          || Mathf.Abs(stickRoll)  > stickThreshold
                          || Mathf.Abs(stickYaw)   > stickThreshold;

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

            if (Plugin.TelemetryEnabled.Value)
            {
                TelemetryRecorder.TryRecord(new TelemetryRecorder.Snapshot
                {
                    time             = Time.fixedTime,
                    camForwardRaw    = rawCamForward,
                    camForwardSmoothed = smoothedCamForward,
                    planeForward     = aircraftTf.forward,
                    planeRight       = aircraftTf.right,
                    planeUp          = aircraftTf.up,
                    velocity         = aircraft.rb != null ? aircraft.rb.velocity : Vector3.zero,
                    angVel           = localAngVel,
                    speed            = aircraft.speed,
                    radarAlt         = aircraft.radarAlt,
                    stickPitch       = stickPitch,
                    stickRoll        = stickRoll,
                    stickYaw         = stickYaw,
                    localTarget      = localTarget,
                    angleOff         = angleOff,
                    fade             = 1f,
                    outPitch         = finalPitch,
                    outRoll          = finalRoll,
                    outYaw           = finalYaw,
                    camMode          = CameraStateManager.cameraMode.ToString(),
                    gate             = gate,
                    iPitch           = mode == Plugin.CursorPidMode.Internal ? internalPid.PitchIntegral : pitchPid.Integral,
                    iYaw             = (mode == Plugin.CursorPidMode.Internal || mode == Plugin.CursorPidMode.HybridInternalYaw) ? internalPid.YawIntegral : yawPid.Integral,
                    iRoll            = mode == Plugin.CursorPidMode.Internal ? internalPid.RollIntegral : rollPid.Integral,
                });
            }
        }

        // NO-WTC-style roll error: desired plane-up tilted toward turn direction unless climbing.
        // Returns degrees, clamped to RollErrorLimit.
        private static float ComputeAngleRollError(float yawErrDeg, Vector3 planeFwd, Vector3 planeRight, Vector3 planeUp)
        {
            Vector3 desiredUp;
            if (Vector3.Dot(planeFwd, Vector3.up) > 0.25f)
            {
                desiredUp = Vector3.up;
            }
            else
            {
                float d = Mathf.Clamp(yawErrDeg * YawToRollFactor, -YawToRollLimit, YawToRollLimit);
                desiredUp = Vector3.up + d * planeRight;
            }
            float err = TargetCalc.GetAngleOnAxis(planeUp, desiredUp, -planeFwd);
            return Mathf.Clamp(err, -RollErrorLimit, RollErrorLimit);
        }

        private static void ResetState()
        {
            initialized = false;
            smoothedPitch = 0f;
            smoothedRoll = 0f;
            smoothedYaw = 0f;
            pitchPid.Reset();
            yawPid.Reset();
            rollPid.Reset();
            internalPid.Reset();
            wasOrbitLastFrame = false;
        }
    }
}
