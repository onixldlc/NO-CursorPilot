using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NOCursorPilot
{
    // Pitch and roll: our custom PID (aggressive, full deflection authority for big turns).
    // Yaw: 100% game's autopilot yaw PID (silky, no wobble, the part we couldn't match).
    // Both stack on top of player stick (assistive). Stick always wins via clamp headroom.
    [HarmonyPatch(typeof(PilotPlayerState), "PlayerAxisControls")]
    internal static class CursorFlightPatch
    {
        private static Vector3 smoothedCamForward;
        private static float smoothedPitch, smoothedRoll;
        private static bool initialized;

        private static readonly PidController pitchPid = new PidController();
        private static readonly PidController rollPid  = new PidController();

        private static readonly FieldInfo fForwardFlightController =
            typeof(Autopilot).GetField("forwardFlightController",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);

        private static MethodInfo cachedApplyInputs;
        private static Type cachedControllerType;
        private static readonly ControlInputs gameYawScratch = new ControlInputs();

        static void Postfix(PilotPlayerState __instance)
        {
            if (!Plugin.Enabled)                                   { ResetState(); return; }
            if (__instance?.pilot?.aircraft == null)               { ResetState(); return; }
            if (!GameManager.flightControlsEnabled)                { ResetState(); return; }
            if (Cursor.visible)                                    { ResetState(); return; }
            if (CursorManager.GetFlag(CursorFlags.Chat))           { ResetState(); return; }
            if (DynamicMap.mapMaximized)                           { ResetState(); return; }
            if (CameraStateManager.cameraMode != CameraMode.orbit) { ResetState(); return; }

            Aircraft aircraft = __instance.pilot.aircraft;
            ControlInputs inputs = __instance.controlInputs;
            if (inputs == null || aircraft.cockpit == null) return;

            CameraStateManager camMgr = SceneSingleton<CameraStateManager>.i;
            if (camMgr == null) return;

            // === Camera direction (saved during Free Look hold / recovery, else live) ===
            bool freeLookHeld  = GameManager.playerInput != null &&
                                 GameManager.playerInput.GetButton("Free Look");
            bool camRecovering = CameraOrbitPatch.IsRecovering;
            bool hasSavedDir   = CameraOrbitPatch.TryGetSavedDirection(out Vector3 savedDir);
            bool useSavedDir   = (freeLookHeld || camRecovering) && hasSavedDir;
            Vector3 rawCamForward = useSavedDir ? savedDir : camMgr.transform.forward;

            float dt = Time.fixedDeltaTime;
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

            // Snapshot stick BEFORE we write inputs.
            float stickPitch = inputs.pitch;
            float stickRoll  = inputs.roll;
            float stickYaw   = inputs.yaw;

            Transform planeTf = aircraft.transform;
            Vector3 planeFwd  = planeTf.forward;
            float angleOff    = Vector3.Angle(planeFwd, smoothedCamForward);

            // === CUSTOM PID: pitch + roll ===
            float aimDistance = Plugin.AimDistance.Value;
            Vector3 flyTarget = planeTf.position + smoothedCamForward * aimDistance;
            float sensitivity = Plugin.Sensitivity.Value;
            Vector3 localTarget = planeTf.InverseTransformPoint(flyTarget).normalized * sensitivity;

            float wingsLevelErr = planeTf.right.y;
            float customPitchErr = -localTarget.y;
            float aggressiveAngle = Plugin.AggressiveTurnAngle.Value;
            float influence = Mathf.InverseLerp(0f, aggressiveAngle, angleOff);
            float customRollErr = Mathf.Lerp(wingsLevelErr, localTarget.x, influence);

            if (Plugin.InvertPitch.Value) customPitchErr = -customPitchErr;
            if (Plugin.InvertRoll.Value)  customRollErr  = -customRollErr;

            Vector3 localAngVel = aircraft.rb != null
                ? planeTf.InverseTransformDirection(aircraft.rb.angularVelocity)
                : Vector3.zero;

            float ki = Plugin.Ki.Value;
            float intLim = Plugin.IntegralLimit.Value;
            pitchPid.Kp = 1f; pitchPid.Ki = ki; pitchPid.Kd = Plugin.KdPitch.Value; pitchPid.IntegralLimit = intLim;
            rollPid.Kp  = 1f; rollPid.Ki  = ki; rollPid.Kd  = Plugin.KdRoll.Value;  rollPid.IntegralLimit  = intLim;

            float customPitchRaw = Mathf.Clamp(pitchPid.Compute(customPitchErr, localAngVel.x, dt, false), -1f, 1f);
            float customRollRaw  = Mathf.Clamp(rollPid.Compute(customRollErr,  localAngVel.z, dt, false), -1f, 1f);

            float outLambda = Plugin.OutputSmoothing.Value;
            float tOut = outLambda > 0f ? 1f - Mathf.Exp(-outLambda * dt) : 1f;
            smoothedPitch = Mathf.Lerp(smoothedPitch, customPitchRaw, tOut);
            smoothedRoll  = Mathf.Lerp(smoothedRoll,  customRollRaw,  tOut);

            // === GAME PID: yaw only ===
            float gameYaw = TryGameYaw(aircraft, smoothedCamForward, planeTf);

            // === Stack with player stick (assistive) ===
            inputs.pitch = Mathf.Clamp(smoothedPitch + stickPitch, -1f, 1f);
            inputs.roll  = Mathf.Clamp(smoothedRoll  + stickRoll,  -1f, 1f);
            inputs.yaw   = Mathf.Clamp(gameYaw       + stickYaw,   -1f, 1f);

            if (Plugin.TelemetryEnabled.Value)
            {
                TelemetryRecorder.TryRecord(new TelemetryRecorder.Snapshot
                {
                    time               = Time.fixedTime,
                    camForwardRaw      = rawCamForward,
                    camForwardSmoothed = smoothedCamForward,
                    planeForward       = planeTf.forward,
                    planeRight         = planeTf.right,
                    planeUp            = planeTf.up,
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
                    outPitch           = inputs.pitch,
                    outRoll            = inputs.roll,
                    outYaw             = inputs.yaw,
                    camMode            = CameraStateManager.cameraMode.ToString(),
                    gate               = useSavedDir
                        ? (freeLookHeld ? "freeLookTrack" : "savedDirHold")
                        : "wrote",
                    iPitch             = pitchPid.Integral,
                    iYaw               = 0f,
                    iRoll              = rollPid.Integral,
                });
            }
        }

        // Calls game's autopilot PID, returns just its yaw output. Returns 0 if unavailable.
        private static float TryGameYaw(Aircraft aircraft, Vector3 camForward, Transform planeTf)
        {
            if (fForwardFlightController == null) return 0f;
            Autopilot ap = aircraft.cockpit.GetComponent<Autopilot>();
            if (ap == null) return 0f;
            object forwardController = fForwardFlightController.GetValue(ap);
            if (forwardController == null) return 0f;

            if (cachedApplyInputs == null || cachedControllerType != forwardController.GetType())
            {
                cachedControllerType = forwardController.GetType();
                cachedApplyInputs = cachedControllerType
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "ApplyInputs") return false;
                        var ps = m.GetParameters();
                        return ps.Length == 3
                            && ps[0].ParameterType == typeof(ControlInputs)
                            && ps[1].ParameterType == typeof(float)
                            && ps[2].ParameterType == typeof(Vector3);
                    });
            }
            if (cachedApplyInputs == null) return 0f;

            float yawErr = AngleOnAxis(planeTf.forward, camForward, planeTf.up);

            // Each axis is independent in AeroPID. Pass 0 for pitch/roll error.
            Vector3 errorAngles = new Vector3(0f, yawErr, 0f);
            float airspeed = aircraft.rb != null ? aircraft.rb.velocity.magnitude : aircraft.speed;

            // Reset scratch to avoid stale values affecting downstream readers.
            gameYawScratch.pitch = 0f;
            gameYawScratch.yaw   = 0f;
            gameYawScratch.roll  = 0f;
            cachedApplyInputs.Invoke(forwardController, new object[] { gameYawScratch, airspeed, errorAngles });

            return Mathf.Clamp(gameYawScratch.yaw, -Plugin.PidYawLimit.Value, Plugin.PidYawLimit.Value);
        }

        private static float AngleOnAxis(Vector3 self, Vector3 other, Vector3 axis)
        {
            Vector3 from = Vector3.Cross(axis, self);
            Vector3 to   = Vector3.Cross(axis, other);
            return Vector3.SignedAngle(from, to, axis);
        }

        private static void ResetState()
        {
            initialized = false;
            smoothedPitch = 0f;
            smoothedRoll = 0f;
            pitchPid.Reset();
            rollPid.Reset();
        }
    }
}
