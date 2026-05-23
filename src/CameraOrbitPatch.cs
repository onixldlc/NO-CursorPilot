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

        // ===== Heading compensation =====
        private static readonly FieldInfo fPanView = typeof(CameraOrbitState).GetField("panView", BindingFlags.Instance | BindingFlags.NonPublic);

        private static float prevHeadingDeg;
        private static bool  prevHeadingValid;

        [HarmonyPatch("UpdateState")]
        [HarmonyPostfix]
        static void Post_UpdateState(CameraOrbitState __instance, CameraStateManager cam)
        {
            if (!Plugin.Enabled || CameraStateManager.cameraMode != CameraMode.orbit || cam == null || cam.followingRB == null)
            {
                prevHeadingValid = false;
                return;
            }

            Vector3 fwd = cam.followingRB.velocity + cam.followingRB.transform.forward * 10f;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) { prevHeadingValid = false; return; }

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
    }
}
