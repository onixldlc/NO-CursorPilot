using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NOCursorPilot
{
    // Two patches on CameraOrbitState:
    //
    // 1. Transpiler on CameraMotion: replace the three smoothing call sites with no-op pass-throughs.
    //       line 170 Vector3.RotateTowards  -> target
    //       line 177 Vector3.SmoothDamp     -> target
    //       line 182 first Quaternion.Lerp  -> b (current)
    //    Other Lerps in CameraMotion (lookAt target logic at lines 214/227/230) untouched.
    //
    // 2. Postfix on UpdateState: subtract plane heading delta from panView each frame.
    //    Original game's line-182 Lerp gave implicit world-absolute camera lock via low-pass
    //    on previous pivot rotation. Removing the Lerp killed that, so panView (which is
    //    additive on top of velocity tracking) made camera drift relative to world as plane
    //    turned. Compensating panView by -deltaHeading restores absolute lock via pure
    //    discrete arithmetic (no filter, no accumulator residue, no drift source).
    [HarmonyPatch(typeof(CameraOrbitState))]
    internal static class CameraOrbitPatch
    {
        // ===== Transpiler bits =====
        private static readonly MethodInfo M_RotateTowards =
            typeof(Vector3).GetMethod("RotateTowards", BindingFlags.Static | BindingFlags.Public);

        private static readonly MethodInfo M_SmoothDamp =
            typeof(Vector3).GetMethod("SmoothDamp",
                BindingFlags.Static | BindingFlags.Public,
                null,
                new[] { typeof(Vector3), typeof(Vector3), typeof(Vector3).MakeByRefType(), typeof(float) },
                null);

        private static readonly MethodInfo M_QuatLerp =
            typeof(Quaternion).GetMethod("Lerp", BindingFlags.Static | BindingFlags.Public);

        private static readonly MethodInfo M_PassRotate = AccessTools.Method(typeof(CameraOrbitPatch), nameof(PassRotateTowards));
        private static readonly MethodInfo M_PassDamp   = AccessTools.Method(typeof(CameraOrbitPatch), nameof(PassSmoothDamp));
        private static readonly MethodInfo M_PassQuat   = AccessTools.Method(typeof(CameraOrbitPatch), nameof(PassQuatLerp));

        public static Vector3 PassRotateTowards(Vector3 current, Vector3 target, float maxRad, float maxMag)
        {
            if (Plugin.Enabled) return target;
            return Vector3.RotateTowards(current, target, maxRad, maxMag);
        }

        public static Vector3 PassSmoothDamp(Vector3 current, Vector3 target, ref Vector3 velocity, float smoothTime)
        {
            if (Plugin.Enabled) return target;
            return Vector3.SmoothDamp(current, target, ref velocity, smoothTime);
        }

        public static Quaternion PassQuatLerp(Quaternion a, Quaternion b, float t)
        {
            if (Plugin.Enabled) return b;
            return Quaternion.Lerp(a, b, t);
        }

        [HarmonyPatch("CameraMotion")]
        [HarmonyTranspiler]
        static IEnumerable<CodeInstruction> Transpile_CameraMotion(IEnumerable<CodeInstruction> instructions)
        {
            bool quatLerpReplaced = false;
            foreach (var ins in instructions)
            {
                if (ins.opcode == System.Reflection.Emit.OpCodes.Call && ins.operand is MethodInfo mi)
                {
                    if (mi == M_RotateTowards) { yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, M_PassRotate); continue; }
                    if (mi == M_SmoothDamp)    { yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, M_PassDamp);   continue; }
                    if (mi == M_QuatLerp && !quatLerpReplaced)
                    {
                        quatLerpReplaced = true;
                        yield return new CodeInstruction(System.Reflection.Emit.OpCodes.Call, M_PassQuat);
                        continue;
                    }
                }
                yield return ins;
            }
        }

        // ===== Heading compensation + free-look recovery =====
        private static readonly FieldInfo fPanView  = typeof(CameraOrbitState).GetField("panView",  BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo fTiltView = typeof(CameraOrbitState).GetField("tiltView", BindingFlags.Instance | BindingFlags.NonPublic);

        private static float prevHeadingDeg;
        private static bool  prevHeadingValid;

        private static bool    prevFreeLookHeld;
        private static bool    recovering;
        private static float   recoveryStartTime;
        private static float   recoveryStartPan;
        private static float   recoveryStartTilt;
        private static float   savedWorldYawDeg;
        private static float   savedTiltView;
        private static bool    savedViewValid;
        private static Vector3 savedPivotForward;
        private static Vector3 savedCursorPilotDir;
        private static float   graceEndsAt;

        // CursorFlightPatch checks this to stop driving the plane while camera animates back
        public static bool IsRecovering => recovering || Time.unscaledTime < graceEndsAt;

        // CursorFlightPatch uses this to keep flying toward the pre-Free-Look direction
        public static bool TryGetSavedDirection(out Vector3 dir)
        {
            dir = savedCursorPilotDir;
            return savedViewValid;
        }

        // Reset all stale recovery / heading state every time orbit cam is entered.
        // Without this, leaving and re-entering orbit can carry over `recovering=true` or
        // a stale `savedViewValid` from the previous session, which causes CursorFlightPatch
        // to fly toward an old saved direction or stay in passive recovery mode forever.
        [HarmonyPatch("EnterState")]
        [HarmonyPostfix]
        static void Post_EnterState(CameraOrbitState __instance, CameraStateManager cam)
        {
            prevHeadingValid = false;
            prevFreeLookHeld = false;
            recovering = false;
            savedViewValid = false;
            graceEndsAt = 0f;
        }

        [HarmonyPatch("UpdateState")]
        [HarmonyPostfix]
        static void Post_UpdateState(CameraOrbitState __instance, CameraStateManager cam)
        {
            if (!Plugin.Enabled || CameraStateManager.cameraMode != CameraMode.orbit || cam == null || cam.followingRB == null)
            {
                prevHeadingValid = false;
                prevFreeLookHeld = false;
                recovering = false;
                return;
            }

            // Heading compensation: subtract plane yaw delta from panView so camera stays world-absolute.
            Vector3 fwd = cam.followingRB.velocity + cam.followingRB.transform.forward * 10f;
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 0.0001f)
            {
                float currHeadingDeg = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
                if (prevHeadingValid)
                {
                    float delta = Mathf.DeltaAngle(prevHeadingDeg, currHeadingDeg);
                    float panView = (float)fPanView.GetValue(__instance);
                    panView -= delta;
                    fPanView.SetValue(__instance, panView);
                }
                prevHeadingDeg = currHeadingDeg;
                prevHeadingValid = true;
            }
            else
            {
                prevHeadingValid = false;
            }

            // Free-look recovery: snapshot world-space camera direction at Free Look press.
            bool freeLookHeld = GameManager.playerInput != null && GameManager.playerInput.GetButton("Free Look");

            if (!prevFreeLookHeld && freeLookHeld)
            {
                // Press transition: capture two things.
                // 1. pivot.forward: used for camera-state recovery (decompose to panView/tiltView targets).
                // 2. the actual cursor pilot input direction the mod was tracking
                Vector3 pivotFwd = cam.cameraPivot != null
                    ? cam.cameraPivot.rotation * Vector3.forward
                    : cam.transform.forward;
                savedPivotForward     = pivotFwd.normalized;
                savedCursorPilotDir   = cam.transform.forward.normalized;
                savedWorldYawDeg      = Mathf.Atan2(pivotFwd.x, pivotFwd.z) * Mathf.Rad2Deg;
                savedTiltView         = -Mathf.Asin(Mathf.Clamp(pivotFwd.y, -1f, 1f)) * Mathf.Rad2Deg;
                savedViewValid        = true;
            }

            if (freeLookHeld)
            {
                recovering = false; // cancel any in-progress recovery
            }
            else if (prevFreeLookHeld && !Plugin.TurnToFreelook.Value && savedViewValid)
            {
                recovering = true;
                recoveryStartTime = Time.unscaledTime;
                recoveryStartPan  = (float)fPanView.GetValue(__instance);
                recoveryStartTilt = (float)fTiltView.GetValue(__instance);
            }

            if (recovering)
            {
                // panView is offset from plane heading -> derive each frame against current heading.
                // tiltView is world-absolute pitch (because LookRotation+yaw leaves local-X horizontal).
                float targetPan  = Mathf.DeltaAngle(prevHeadingDeg, savedWorldYawDeg);
                float targetTilt = savedTiltView;

                float duration = Mathf.Max(0.01f, Plugin.FreelookRecoverySeconds.Value);
                float t = (Time.unscaledTime - recoveryStartTime) / duration;
                if (t >= 1f)
                {
                    fPanView.SetValue(__instance,  targetPan);
                    fTiltView.SetValue(__instance, targetTilt);
                    recovering = false;
                    graceEndsAt = Time.unscaledTime + Mathf.Max(0f, Plugin.FreelookGraceSeconds.Value);
                }
                else
                {
                    fPanView.SetValue(__instance,  Mathf.Lerp(recoveryStartPan,  targetPan,  t));
                    fTiltView.SetValue(__instance, Mathf.Lerp(recoveryStartTilt, targetTilt, t));
                }
            }

            prevFreeLookHeld = freeLookHeld;
        }
    }
}
