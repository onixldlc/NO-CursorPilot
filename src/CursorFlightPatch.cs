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

        private static readonly PidController pitchPid = new PidController();
        private static readonly PidController yawPid   = new PidController();
        private static readonly PidController rollPid  = new PidController();

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

            CameraStateManager camMgr = SceneSingleton<CameraStateManager>.i;
            if (camMgr == null) return;

            Vector3 rawCamForward = camMgr.transform.forward;
            float dt = Time.fixedDeltaTime;
            Transform aircraftTf = aircraft.transform;

            float camLambda = Plugin.TargetSmoothing.Value;
            float tCam = camLambda > 0f ? 1f - Mathf.Exp(-camLambda * dt) : 1f;

            if (!initialized)
            {
                smoothedCamForward = rawCamForward;
                initialized = true;
            }
            smoothedCamForward = Vector3.Slerp(smoothedCamForward, rawCamForward, tCam).normalized;

            float stickPitch = inputs.pitch;
            float stickRoll  = inputs.roll;
            float stickYaw   = inputs.yaw;

            // === ERROR PROXIES ===
            float aimDistance = Plugin.AimDistance.Value;
            Vector3 flyTarget = aircraftTf.position + smoothedCamForward * aimDistance;

            float sensitivity = Plugin.Sensitivity.Value;
            Vector3 localTarget = aircraftTf.InverseTransformPoint(flyTarget).normalized * sensitivity;

            // angleOff uses velocity direction (where plane is actually heading),
            // not nose forward. Plane has inertia; nose can be on-target while
            // velocity still drifts. Velocity reference closes that gap.
            // Fall back to nose forward at very low speed (taxi / stall).
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

            // === SOFT GATES ===
            string gate;
            float finalPitch, finalRoll, finalYaw;

            bool freeLookHeld = GameManager.playerInput != null &&
                                GameManager.playerInput.GetButton("Free Look");
            bool camRecovering = CameraOrbitPatch.IsRecovering;
            const float stickThreshold = 0.05f;
            bool stickHeld = Mathf.Abs(stickPitch) > stickThreshold ||
                             Mathf.Abs(stickRoll)  > stickThreshold ||
                             Mathf.Abs(stickYaw)   > stickThreshold;

            if (freeLookHeld || camRecovering)
            {
                // Reset mod state each frame so PID integrators and output smoothing don't
                // dump accumulated error / stale values into controls when cursor pilot resumes.
                ResetState();
                gate = freeLookHeld ? "freeLook" : "camRecover";
                finalPitch = stickPitch; finalRoll = stickRoll; finalYaw = stickYaw;
            }
            else if (stickHeld)
            {
                gate = "stickOverride";
                finalPitch = stickPitch; finalRoll = stickRoll; finalYaw = stickYaw;
            }
            else
            {
                gate = "wrote";
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
                    iPitch           = pitchPid.Integral,
                    iYaw             = yawPid.Integral,
                    iRoll            = rollPid.Integral,
                });
            }
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
        }
    }
}
