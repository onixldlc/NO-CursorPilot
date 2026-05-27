using HarmonyLib;
using NOCursorPilot.CursorFlight;

namespace NOCursorPilot
{
    [HarmonyPatch(typeof(PilotPlayerState), "PlayerAxisControls")]
    internal static class CursorFlightPatch
    {
        static void Postfix(PilotPlayerState __instance) => FlightController.Tick(__instance);
    }
}
