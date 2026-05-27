using UnityEngine;

namespace NOCursorPilot.CursorFlight
{
    internal static class GainSchedule
    {
        // multP applied to Kp, multD applied to Kd. Ki not scaled.
        // ratio = refSpeed / max(speed, minSpeed). At low speed ratio>1 -> mult>1.
        public static void Compute(float speed, ScheduleParams s, out float multP, out float multD)
        {
            float denom = Mathf.Max(speed, s.MinSpeed);
            float ratio = s.RefSpeed / denom;
            multP = Mathf.Clamp(Mathf.Pow(ratio, s.ExpP), s.MultMin, s.MultMax);
            multD = Mathf.Clamp(Mathf.Pow(ratio, s.ExpD), s.MultMin, s.MultMax);
        }
    }
}
