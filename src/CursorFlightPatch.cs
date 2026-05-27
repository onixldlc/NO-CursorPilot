using HarmonyLib;
using NOCursorPilot.Controller;

namespace NOCursorPilot
{
    [HarmonyPatch(typeof(PilotPlayerState), "PlayerAxisControls")]
    internal static class CursorFlightPatch
    {
        
        private static readonly ControllerProvider controller = new ControllerPID();
        private static bool loggedError;

        static void Postfix(PilotPlayerState __instance)
        {
            if (__instance == null) return;
            try
            {
                controller.Run(__instance);
                loggedError = false;
            }
            catch (System.Exception e)
            {
                controller.Reset();
                if (!loggedError)
                {
                    Plugin.LogSource?.LogError($"Controller threw, disengaging this frame: {e}");
                    loggedError = true;
                }
            }
        }
    }
}
