using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using NOCursorPilot.Helper;

namespace NOCursorPilot.CursorFlight
{
    internal class AxisGains
    {
        public float Kp;
        public float Ki;
        public float Kd;
        public float IntegralLimit;

        public static AxisGains FromJson(object node, AxisGains fallback)
        {
            var d = MiniJson.Obj(node);
            if (d == null) return fallback;
            return new AxisGains
            {
                Kp            = MiniJson.NumF(MiniJson.Get(d, "kp"),     fallback.Kp),
                Ki            = MiniJson.NumF(MiniJson.Get(d, "ki"),     fallback.Ki),
                Kd            = MiniJson.NumF(MiniJson.Get(d, "kd"),     fallback.Kd),
                IntegralLimit = MiniJson.NumF(MiniJson.Get(d, "iLimit"), fallback.IntegralLimit),
            };
        }

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["kp"]     = (double)Kp,
            ["ki"]     = (double)Ki,
            ["kd"]     = (double)Kd,
            ["iLimit"] = (double)IntegralLimit,
        };

        public AxisGains Clone() => (AxisGains)MemberwiseClone();
    }

    // Patent 303A adaptive chase controller params. See
    // .reference/how_to_build/13-WT_MouseAim_Implementation_Guide.md.
    internal class Chase303Params
    {
        public float TargetResponseRate;
        public float DampingRatio;
        public float YawMultiplier;
        public float BankToTurnFactor;
        public float BankClampDeg;
        public float MinInputThreshold;
        public float EffSmoothing;
        public float EffMin;
        public float EffMax;
        public float WobbleThreshold;
        public float WobbleBoost;
        public int   WobbleWindow;
        public float PitchCoeffMin, PitchCoeffMax;
        public float RollCoeffMin,  RollCoeffMax;
        public float YawCoeffMin,   YawCoeffMax;
        public float PitchDampMin,  PitchDampMax;
        public float RollDampMin,   RollDampMax;
        public float YawDampMin,    YawDampMax;

        public static Chase303Params Defaults() => new Chase303Params
        {
            TargetResponseRate = 3.0f,
            // Bank-to-turn is a position controller on a double-integrator (input ->
            // rate -> angle). Needs more damping than direct rate control. 0.8 is
            // overdamped on rate path but appropriate for the bank loop.
            DampingRatio       = 0.8f,
            YawMultiplier      = 0.4f,
            // BankToTurnFactor 55 (per guide) saturates output for small lateral
            // errors when combined with rollCoeff ~5-10. 25 keeps desiredBank in a
            // gentler range so the controller has headroom before saturating.
            BankToTurnFactor   = 25f,
            BankClampDeg       = 60f,
            MinInputThreshold  = 0.05f,
            EffSmoothing       = 0.05f,
            EffMin             = 0.1f,
            EffMax             = 50f,
            // Wobble window must be long enough to span at least one full oscillation
            // period of the wobble we want to catch. ~2s wobble at 50Hz fixedupdate
            // = 100 frames. Threshold 3 catches even a couple sign changes inside
            // that window. Boost 2.0 strongly attenuates the runaway when detected.
            WobbleThreshold    = 3f,
            WobbleBoost        = 2.0f,
            WobbleWindow       = 100,
            PitchCoeffMin = 0.5f, PitchCoeffMax = 15f,
            // Cap rollCoeff lower than guide's 20 — high rollCoeff with bank-to-turn
            // amplifies any bankError instability into saturation faster than the
            // adaptive system can pull it down.
            RollCoeffMin  = 0.5f, RollCoeffMax  = 6f,
            YawCoeffMin   = 0.2f, YawCoeffMax   = 5f,
            PitchDampMin  = 0.3f, PitchDampMax  = 8f,
            RollDampMin   = 0.3f, RollDampMax   = 10f,
            YawDampMin    = 0.1f, YawDampMax    = 3f,
        };

        public static Chase303Params FromJson(object node, Chase303Params fb)
        {
            var d = MiniJson.Obj(node);
            if (d == null) return fb.Clone();
            return new Chase303Params
            {
                TargetResponseRate = MiniJson.NumF(MiniJson.Get(d, "targetResponseRate"), fb.TargetResponseRate),
                DampingRatio       = MiniJson.NumF(MiniJson.Get(d, "dampingRatio"),       fb.DampingRatio),
                YawMultiplier      = MiniJson.NumF(MiniJson.Get(d, "yawMultiplier"),      fb.YawMultiplier),
                BankToTurnFactor   = MiniJson.NumF(MiniJson.Get(d, "bankToTurnFactor"),   fb.BankToTurnFactor),
                BankClampDeg       = MiniJson.NumF(MiniJson.Get(d, "bankClampDeg"),       fb.BankClampDeg),
                MinInputThreshold  = MiniJson.NumF(MiniJson.Get(d, "minInputThreshold"),  fb.MinInputThreshold),
                EffSmoothing       = MiniJson.NumF(MiniJson.Get(d, "effSmoothing"),       fb.EffSmoothing),
                EffMin             = MiniJson.NumF(MiniJson.Get(d, "effMin"),             fb.EffMin),
                EffMax             = MiniJson.NumF(MiniJson.Get(d, "effMax"),             fb.EffMax),
                WobbleThreshold    = MiniJson.NumF(MiniJson.Get(d, "wobbleThreshold"),    fb.WobbleThreshold),
                WobbleBoost        = MiniJson.NumF(MiniJson.Get(d, "wobbleBoost"),        fb.WobbleBoost),
                WobbleWindow       = (int)MiniJson.NumF(MiniJson.Get(d, "wobbleWindow"),  fb.WobbleWindow),
                PitchCoeffMin = MiniJson.NumF(MiniJson.Get(d, "pitchCoeffMin"), fb.PitchCoeffMin),
                PitchCoeffMax = MiniJson.NumF(MiniJson.Get(d, "pitchCoeffMax"), fb.PitchCoeffMax),
                RollCoeffMin  = MiniJson.NumF(MiniJson.Get(d, "rollCoeffMin"),  fb.RollCoeffMin),
                RollCoeffMax  = MiniJson.NumF(MiniJson.Get(d, "rollCoeffMax"),  fb.RollCoeffMax),
                YawCoeffMin   = MiniJson.NumF(MiniJson.Get(d, "yawCoeffMin"),   fb.YawCoeffMin),
                YawCoeffMax   = MiniJson.NumF(MiniJson.Get(d, "yawCoeffMax"),   fb.YawCoeffMax),
                PitchDampMin  = MiniJson.NumF(MiniJson.Get(d, "pitchDampMin"),  fb.PitchDampMin),
                PitchDampMax  = MiniJson.NumF(MiniJson.Get(d, "pitchDampMax"),  fb.PitchDampMax),
                RollDampMin   = MiniJson.NumF(MiniJson.Get(d, "rollDampMin"),   fb.RollDampMin),
                RollDampMax   = MiniJson.NumF(MiniJson.Get(d, "rollDampMax"),   fb.RollDampMax),
                YawDampMin    = MiniJson.NumF(MiniJson.Get(d, "yawDampMin"),    fb.YawDampMin),
                YawDampMax    = MiniJson.NumF(MiniJson.Get(d, "yawDampMax"),    fb.YawDampMax),
            };
        }

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["targetResponseRate"] = (double)TargetResponseRate,
            ["dampingRatio"]       = (double)DampingRatio,
            ["yawMultiplier"]      = (double)YawMultiplier,
            ["bankToTurnFactor"]   = (double)BankToTurnFactor,
            ["bankClampDeg"]       = (double)BankClampDeg,
            ["minInputThreshold"]  = (double)MinInputThreshold,
            ["effSmoothing"]       = (double)EffSmoothing,
            ["effMin"]             = (double)EffMin,
            ["effMax"]             = (double)EffMax,
            ["wobbleThreshold"]    = (double)WobbleThreshold,
            ["wobbleBoost"]        = (double)WobbleBoost,
            ["wobbleWindow"]       = (double)WobbleWindow,
            ["pitchCoeffMin"] = (double)PitchCoeffMin, ["pitchCoeffMax"] = (double)PitchCoeffMax,
            ["rollCoeffMin"]  = (double)RollCoeffMin,  ["rollCoeffMax"]  = (double)RollCoeffMax,
            ["yawCoeffMin"]   = (double)YawCoeffMin,   ["yawCoeffMax"]   = (double)YawCoeffMax,
            ["pitchDampMin"]  = (double)PitchDampMin,  ["pitchDampMax"]  = (double)PitchDampMax,
            ["rollDampMin"]   = (double)RollDampMin,   ["rollDampMax"]   = (double)RollDampMax,
            ["yawDampMin"]    = (double)YawDampMin,    ["yawDampMax"]    = (double)YawDampMax,
        };

        public Chase303Params Clone() => (Chase303Params)MemberwiseClone();
    }

    internal class ScheduleParams
    {
        public float RefSpeed;
        public float MinSpeed;
        public float ExpP;
        public float ExpD;
        public float MultMin;
        public float MultMax;

        public static ScheduleParams FromJson(object node, ScheduleParams fallback)
        {
            var d = MiniJson.Obj(node);
            if (d == null) return fallback.Clone();
            return new ScheduleParams
            {
                RefSpeed = MiniJson.NumF(MiniJson.Get(d, "refSpeed"), fallback.RefSpeed),
                MinSpeed = MiniJson.NumF(MiniJson.Get(d, "minSpeed"), fallback.MinSpeed),
                ExpP     = MiniJson.NumF(MiniJson.Get(d, "expP"),     fallback.ExpP),
                ExpD     = MiniJson.NumF(MiniJson.Get(d, "expD"),     fallback.ExpD),
                MultMin  = MiniJson.NumF(MiniJson.Get(d, "multMin"),  fallback.MultMin),
                MultMax  = MiniJson.NumF(MiniJson.Get(d, "multMax"),  fallback.MultMax),
            };
        }

        public Dictionary<string, object> ToJson() => new Dictionary<string, object>
        {
            ["refSpeed"] = (double)RefSpeed,
            ["minSpeed"] = (double)MinSpeed,
            ["expP"]     = (double)ExpP,
            ["expD"]     = (double)ExpD,
            ["multMin"]  = (double)MultMin,
            ["multMax"]  = (double)MultMax,
        };

        public ScheduleParams Clone() => (ScheduleParams)MemberwiseClone();
    }

    internal class ProfileData
    {
        public string Name;

        public AxisGains ChasePitch;
        public AxisGains ChaseYaw;
        public AxisGains ChaseRoll;

        public AxisGains LevelPitch;
        public AxisGains LevelRoll;

        public Chase303Params Chase303;

        public ScheduleParams Schedule;

        public float AggressiveAngle;
        public float TargetSmoothing;
        public float OutputSmoothing;

        public bool UseYaw;
        public bool InvertPitch;
        public bool InvertRoll;

        public static ProfileData Default()
        {
            return new ProfileData
            {
                Name       = "default",
                ChasePitch = new AxisGains { Kp = 5.0f, Ki = 0.05f, Kd =  0.18f, IntegralLimit = 1.0f },
                ChaseYaw   = new AxisGains { Kp = 5.0f, Ki = 0.05f, Kd =  0.15f, IntegralLimit = 1.0f },
                ChaseRoll  = new AxisGains { Kp = 5.0f, Ki = 0.05f, Kd = -0.35f, IntegralLimit = 1.0f },
                LevelPitch = new AxisGains { Kp = 5.0f, Ki = 0.02f, Kd =  0.20f, IntegralLimit = 0.5f },
                LevelRoll  = new AxisGains { Kp = 5.0f, Ki = 0.02f, Kd =  0.20f, IntegralLimit = 0.5f },
                Chase303   = Chase303Params.Defaults(),
                Schedule   = new ScheduleParams
                {
                    RefSpeed = 200f, MinSpeed = 30f,
                    ExpP = 0.5f, ExpD = 0.5f,
                    MultMin = 0.3f, MultMax = 3.0f
                },
                AggressiveAngle = 3.0f,
                TargetSmoothing = 3.0f,
                OutputSmoothing = 5.0f,
                UseYaw      = true,
                InvertPitch = false,
                InvertRoll  = false,
            };
        }

        public static ProfileData FromJson(string raw, string nameFallback)
        {
            ProfileData fb = Default();
            object root;
            try { root = MiniJson.Parse(raw); }
            catch (MiniJson.ParseException e)
            {
                Plugin.LogSource?.LogError($"[Profile] parse error in '{nameFallback}': {e.Message}");
                fb.Name = nameFallback;
                return fb;
            }

            var d = MiniJson.Obj(root);
            if (d == null)
            {
                Plugin.LogSource?.LogError($"[Profile] root not object in '{nameFallback}'");
                fb.Name = nameFallback;
                return fb;
            }

            var chase = MiniJson.Obj(MiniJson.Get(d, "chase"));
            var level = MiniJson.Obj(MiniJson.Get(d, "level"));
            var aim   = MiniJson.Obj(MiniJson.Get(d, "aim"));
            var sm    = MiniJson.Obj(MiniJson.Get(d, "smoothing"));
            var ax    = MiniJson.Obj(MiniJson.Get(d, "axes"));

            return new ProfileData
            {
                Name = MiniJson.Str(MiniJson.Get(d, "name"), nameFallback),

                ChasePitch = AxisGains.FromJson(MiniJson.Get(chase, "pitch"), fb.ChasePitch),
                ChaseYaw   = AxisGains.FromJson(MiniJson.Get(chase, "yaw"),   fb.ChaseYaw),
                ChaseRoll  = AxisGains.FromJson(MiniJson.Get(chase, "roll"),  fb.ChaseRoll),

                LevelPitch = AxisGains.FromJson(MiniJson.Get(level, "pitch"), fb.LevelPitch),
                LevelRoll  = AxisGains.FromJson(MiniJson.Get(level, "roll"),  fb.LevelRoll),

                Chase303   = Chase303Params.FromJson(MiniJson.Get(d, "chase303"), fb.Chase303),

                Schedule   = ScheduleParams.FromJson(MiniJson.Get(d, "schedule"), fb.Schedule),

                AggressiveAngle = MiniJson.NumF(MiniJson.Get(aim, "aggressiveAngle"), fb.AggressiveAngle),
                TargetSmoothing = MiniJson.NumF(MiniJson.Get(sm, "target"),           fb.TargetSmoothing),
                OutputSmoothing = MiniJson.NumF(MiniJson.Get(sm, "output"),           fb.OutputSmoothing),

                UseYaw      = MiniJson.Bool(MiniJson.Get(ax, "useYaw"),      fb.UseYaw),
                InvertPitch = MiniJson.Bool(MiniJson.Get(ax, "invertPitch"), fb.InvertPitch),
                InvertRoll  = MiniJson.Bool(MiniJson.Get(ax, "invertRoll"),  fb.InvertRoll),
            };
        }

        public string SerializeJson()
        {
            var d = new Dictionary<string, object>
            {
                ["name"] = Name,
                ["chase"] = new Dictionary<string, object>
                {
                    ["pitch"] = ChasePitch.ToJson(),
                    ["yaw"]   = ChaseYaw.ToJson(),
                    ["roll"]  = ChaseRoll.ToJson(),
                },
                ["level"] = new Dictionary<string, object>
                {
                    ["pitch"] = LevelPitch.ToJson(),
                    ["roll"]  = LevelRoll.ToJson(),
                },
                ["chase303"] = Chase303.ToJson(),
                ["schedule"] = Schedule.ToJson(),
                ["aim"] = new Dictionary<string, object>
                {
                    ["aggressiveAngle"] = (double)AggressiveAngle,
                },
                ["smoothing"] = new Dictionary<string, object>
                {
                    ["target"] = (double)TargetSmoothing,
                    ["output"] = (double)OutputSmoothing,
                },
                ["axes"] = new Dictionary<string, object>
                {
                    ["useYaw"]      = UseYaw,
                    ["invertPitch"] = InvertPitch,
                    ["invertRoll"]  = InvertRoll,
                },
            };
            return MiniJson.Write(d);
        }
    }

    internal static class ProfileStore
    {
        private const string SubDir = "NOCursorPilot.profiles";

        private static readonly Dictionary<string, ProfileData> _profiles =
            new Dictionary<string, ProfileData>(StringComparer.OrdinalIgnoreCase);

        private static ProfileData _active = ProfileData.Default();

        public static ProfileData Active => _active;
        public static string ProfileDir => Path.Combine(Paths.ConfigPath, SubDir);

        // Walks the profile folder, parses every *.json into the dictionary.
        // If folder missing, creates it + writes default.json.
        // Re-resolves active profile from BepInEx config "ActiveProfile".
        public static void LoadAll(string activeName)
        {
            _profiles.Clear();
            string dir = ProfileDir;

            try
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                    WriteDefaultProfile(dir);
                }
            }
            catch (Exception e)
            {
                Plugin.LogSource?.LogError($"[Profile] cannot init dir {dir}: {e.Message}");
                _active = ProfileData.Default();
                return;
            }

            foreach (string path in Directory.GetFiles(dir, "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                try
                {
                    string raw = File.ReadAllText(path);
                    ProfileData p = ProfileData.FromJson(raw, name);
                    _profiles[name] = p;
                }
                catch (Exception e)
                {
                    Plugin.LogSource?.LogError($"[Profile] failed to load {path}: {e.Message}");
                }
            }

            // If folder was empty (race), seed default.
            if (_profiles.Count == 0)
            {
                ProfileData def = ProfileData.Default();
                _profiles[def.Name] = def;
                WriteDefaultProfile(dir);
            }

            ResolveActive(activeName);
            Plugin.LogSource?.LogInfo($"[Profile] loaded {_profiles.Count} profile(s), active='{_active.Name}'");
        }

        public static void ResolveActive(string activeName)
        {
            if (!string.IsNullOrEmpty(activeName) && _profiles.TryGetValue(activeName, out ProfileData p))
            {
                _active = p;
                return;
            }
            if (_profiles.TryGetValue("default", out ProfileData def))
            {
                _active = def;
                Plugin.LogSource?.LogWarning($"[Profile] active='{activeName}' not found, falling back to 'default'");
                return;
            }
            foreach (var kv in _profiles) { _active = kv.Value; return; }
            _active = ProfileData.Default();
        }

        private static void WriteDefaultProfile(string dir)
        {
            try
            {
                ProfileData def = ProfileData.Default();
                File.WriteAllText(Path.Combine(dir, "default.json"), def.SerializeJson());
                Plugin.LogSource?.LogInfo($"[Profile] seeded default.json at {dir}");
            }
            catch (Exception e)
            {
                Plugin.LogSource?.LogError($"[Profile] failed to seed default.json: {e.Message}");
            }
        }

        public static bool TryGet(string name, out ProfileData p) => _profiles.TryGetValue(name, out p);
        public static IEnumerable<string> Names => _profiles.Keys;
    }
}
