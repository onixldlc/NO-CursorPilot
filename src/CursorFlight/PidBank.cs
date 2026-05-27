namespace NOCursorPilot.CursorFlight
{
    // Holds the 5 PIDs for cursor flight: 3 chase + 2 leveler.
    // Per-tick caller: ApplyProfile() once when profile changes, then ApplySchedule(multP, multD)
    // every tick to scale Kp/Kd by speed-based multipliers. Ki untouched.
    internal class PidBank
    {
        public readonly PidController ChasePitch = new PidController();
        public readonly PidController ChaseYaw   = new PidController();
        public readonly PidController ChaseRoll  = new PidController();
        public readonly PidController LevelPitch = new PidController();
        public readonly PidController LevelRoll  = new PidController();

        // Cached base gains so per-tick schedule can re-scale from a known origin.
        private AxisGains _baseChasePitch, _baseChaseYaw, _baseChaseRoll;
        private AxisGains _baseLevelPitch, _baseLevelRoll;

        public void ApplyProfile(ProfileData p)
        {
            _baseChasePitch = p.ChasePitch;
            _baseChaseYaw   = p.ChaseYaw;
            _baseChaseRoll  = p.ChaseRoll;
            _baseLevelPitch = p.LevelPitch;
            _baseLevelRoll  = p.LevelRoll;

            CopyBase(ChasePitch, _baseChasePitch);
            CopyBase(ChaseYaw,   _baseChaseYaw);
            CopyBase(ChaseRoll,  _baseChaseRoll);
            CopyBase(LevelPitch, _baseLevelPitch);
            CopyBase(LevelRoll,  _baseLevelRoll);
        }

        public void ApplySchedule(float multP, float multD)
        {
            ScaleFromBase(ChasePitch, _baseChasePitch, multP, multD);
            ScaleFromBase(ChaseYaw,   _baseChaseYaw,   multP, multD);
            ScaleFromBase(ChaseRoll,  _baseChaseRoll,  multP, multD);
            ScaleFromBase(LevelPitch, _baseLevelPitch, multP, multD);
            ScaleFromBase(LevelRoll,  _baseLevelRoll,  multP, multD);
        }

        public void Reset()
        {
            ChasePitch.Reset();
            ChaseYaw.Reset();
            ChaseRoll.Reset();
            LevelPitch.Reset();
            LevelRoll.Reset();
        }

        private static void CopyBase(PidController pid, AxisGains g)
        {
            if (g == null) return;
            pid.Kp = g.Kp;
            pid.Ki = g.Ki;
            pid.Kd = g.Kd;
            pid.IntegralLimit = g.IntegralLimit;
        }

        private static void ScaleFromBase(PidController pid, AxisGains g, float multP, float multD)
        {
            if (g == null) return;
            pid.Kp = g.Kp * multP;
            pid.Kd = g.Kd * multD;
            // Ki, IntegralLimit untouched (kept from CopyBase).
        }
    }
}
