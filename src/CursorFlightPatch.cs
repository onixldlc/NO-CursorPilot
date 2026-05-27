using HarmonyLib;

namespace NOCursorPilot
{
    [HarmonyPatch(typeof(PilotPlayerState), "PlayerAxisControls")]
    internal static class CursorFlightPatch
    {
        private static readonly System.Collections.Generic.HashSet<string> ErrorLists
            = new System.Collections.Generic.HashSet<string>();

        static void Postfix(PilotPlayerState __instance)
        {
            try
            {
                if (__instance == null) return;
                MainController.Run(__instance);
            }
            catch (System.Exception e)
            {
                MainController.Reset();
                if (ErrorLists.Add(e.GetType() + ":" + e.StackTrace)){
                    Plugin.LogSource?.LogError($"Controller threw, disengaging this frame: {e}");
                }
            }
        }
    }
}
