using UnityEngine;

namespace NOCursorPilot.CursorFlight
{
    // Per-tick orchestrator. Replaces v1 CursorFlightPatch.Postfix body.
    //
    // Pipeline:
    //   1. Gate -> hard skip / reset
    //   2. Camera direction smoothing (with saved-direction during Free Look / recovery)
    //   3. Compute chase errors (localTarget) and level errors (right.y, -forward.y)
    //   4. Blend chase vs level by influence(angleOff)
    //   5. Per-tick gain schedule (speed-based) -> scale Kp/Kd
    //   6. Run 5 PIDs -> finalPitch/Roll/Yaw
    //   7. Output smoothing
    //   8. Soft gate (stick override) -> either write outputs or pass-through stick
    //   9. Telemetry push
    internal static class FlightController
    {
        private static readonly PidBank Pids = new PidBank();
        private static readonly Patent303Controller Chase303 = new Patent303Controller();

        private static Vector3 smoothedCamForward;
        private static float smoothedPitch, smoothedRoll, smoothedYaw;
        private static bool  initialized;
        private static bool  wasOrbitLastFrame;
        private static string lastProfileName;

        public static void Tick(PilotPlayerState __instance)
        {
            if (!StateGate.ShouldRun(__instance, out Aircraft aircraft, out ControlInputs inputs))
            {
                ResetState();
                wasOrbitLastFrame = false;
                return;
            }

            ProfileData prof = ProfileStore.Active;
            if (prof == null) { ResetState(); return; }

            // Re-copy gains into PIDs whenever the active profile reference changes (hot reload).
            if (!ReferenceEquals(prof.Name, lastProfileName))
            {
                Pids.ApplyProfile(prof);
                lastProfileName = prof.Name;
            }

            CameraStateManager camMgr = SceneSingleton<CameraStateManager>.i;
            if (camMgr == null) return;

            // Zero stick once on orbit-cam entry to flush cockpit input residue.
            if (!wasOrbitLastFrame)
            {
                inputs.pitch = 0f;
                inputs.roll  = 0f;
                inputs.yaw   = 0f;
            }
            wasOrbitLastFrame = true;

            bool freeLookHeld = GameManager.playerInput != null &&
                                GameManager.playerInput.GetButton("Free Look");
            bool camRecovering = CameraOrbitPatch.IsRecovering;
            bool hasSavedDir = CameraOrbitPatch.TryGetSavedDirection(out Vector3 savedDir);
            bool useSavedDir = (freeLookHeld || camRecovering) && hasSavedDir;
            Vector3 rawCamForward = useSavedDir ? savedDir : camMgr.transform.forward;

            float dt = Time.fixedDeltaTime;
            Transform aircraftTf = aircraft.transform;

            // === Camera direction smoothing ===
            float camLambda = prof.TargetSmoothing;
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
            // Local-frame camera direction. Components are sin(angle) in [-1, 1].
            //   .x -> yaw error proxy
            //   .y -> pitch error proxy (inverted: nose-up = negative)
            //   .z -> forward alignment (unused as error, sign tells front vs behind)
            Vector3 localCamFwd = aircraftTf.InverseTransformDirection(smoothedCamForward);

            // Plane-nose vs camera angle. Use nose, NOT velocity (AoA + sideslip make
            // velocity diverge from nose in steady flight, so angleOff never reaches 0).
            float angleOff = Vector3.Angle(aircraftTf.forward, smoothedCamForward);

            // Leveler errors. Sign chosen so positive command drives toward 0.
            float wingsLevelErr =  aircraftTf.right.y;
            float pitchLevelErr = -aircraftTf.forward.y;

            // influence: 1 = full chase, 0 = full level.
            float influence = Mathf.InverseLerp(0f, prof.AggressiveAngle, angleOff);

            // Body-frame angular velocity for D-term (and Patent303 accel diff).
            Vector3 localAngVel = aircraft.rb != null
                ? aircraftTf.InverseTransformDirection(aircraft.rb.angularVelocity)
                : Vector3.zero;

            // === Per-tick gain schedule (applies to leveler PIDs; chase303 self-scales) ===
            GainSchedule.Compute(aircraft.speed, prof.Schedule, out float multP, out float multD);
            Pids.ApplySchedule(multP, multD);

            // === CHASE: Patent 303A adaptive controller ===
            // measureEffectiveness = false when stick override or freelook/recovery is
            // diverting actual plant inputs; measuring under those conditions corrupts
            // the effectiveness estimate.
            bool stickHeld = StateGate.IsStickHeld(inputs);
            bool measureEff = !stickHeld && !useSavedDir;
            Chase303.Compute(
                localCamFwd, localAngVel, aircraftTf, prof.Chase303, dt, measureEff,
                out float chasePitchOut, out float chaseYawOut, out float chaseRollOut);

            if (prof.InvertPitch) chasePitchOut = -chasePitchOut;
            if (prof.InvertRoll)  chaseRollOut  = -chaseRollOut;

            // === LEVEL: PIDs unchanged ===
            float levelPitchOut = Pids.LevelPitch.Compute(pitchLevelErr, localAngVel.x, dt, false);
            float levelRollOut  = Pids.LevelRoll.Compute(wingsLevelErr,  localAngVel.z, dt, false);

            // Blend outputs by influence. Yaw has no leveler -> faded to 0 at small angles.
            float pitch = Mathf.Lerp(levelPitchOut, chasePitchOut, influence);
            float roll  = Mathf.Lerp(levelRollOut,  chaseRollOut,  influence);
            float yaw   = prof.UseYaw ? (chaseYawOut * influence) : 0f;

            float rawWantPitch = Mathf.Clamp(pitch, -1f, 1f);
            float rawWantRoll  = Mathf.Clamp(roll,  -1f, 1f);
            float rawWantYaw   = Mathf.Clamp(yaw,   -1f, 1f);

            // === OUTPUT SMOOTHING ===
            float outLambda = prof.OutputSmoothing;
            float tOut = outLambda > 0f ? 1f - Mathf.Exp(-outLambda * dt) : 1f;
            smoothedPitch = Mathf.Lerp(smoothedPitch, rawWantPitch, tOut);
            smoothedRoll  = Mathf.Lerp(smoothedRoll,  rawWantRoll,  tOut);
            smoothedYaw   = Mathf.Lerp(smoothedYaw,   rawWantYaw,   tOut);

            // === SOFT GATE: stick override ===
            string gate;
            float finalPitch, finalRoll, finalYaw;

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

            // Record the actual plant input for next tick's effectiveness estimate.
            Chase303.StoreInputs(finalPitch, finalRoll, finalYaw);

            TelemetryWebServer.UpdateState(aircraft, inputs, smoothedCamForward);

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
                    localTarget      = localCamFwd,
                    angleOff         = angleOff,
                    fade             = influence,
                    outPitch         = finalPitch,
                    outRoll          = finalRoll,
                    outYaw           = finalYaw,
                    camMode          = CameraStateManager.cameraMode.ToString(),
                    gate             = gate,
                    // Chase PIDs disabled (Patent 303A controls chase). Repurpose
                    // iPitch/iYaw/iRoll columns to surface adaptive coefficients so the
                    // telemetry dump shows what the 303 controller is doing.
                    iPitch           = Chase303.LastPitchCoeff,
                    iYaw             = Chase303.LastYawCoeff,
                    iRoll            = Chase303.LastRollCoeff,
                    iLevelPitch      = Pids.LevelPitch.Integral,
                    iLevelRoll       = Pids.LevelRoll.Integral,
                });
            }
        }

        public static void OnProfileReloaded()
        {
            // Force PIDs to re-pull gains on next tick.
            lastProfileName = null;
            ResetState();
        }

        private static void ResetState()
        {
            initialized = false;
            smoothedPitch = 0f;
            smoothedRoll  = 0f;
            smoothedYaw   = 0f;
            Pids.Reset();
            Chase303.Reset();
        }
    }
}
