using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace NOCursorPilot
{
    internal static class TelemetryRecorder
    {
        public struct Snapshot
        {
            public float time;
            public Vector3 camForwardRaw;
            public Vector3 camForwardSmoothed;
            public Vector3 planeForward;
            public Vector3 planeRight;
            public Vector3 planeUp;
            public Vector3 velocity;
            public Vector3 angVel;
            public float speed;
            public float radarAlt;
            public float stickPitch, stickRoll, stickYaw;
            public Vector3 localTarget;
            public float angleOff;
            public float fade;
            public float outPitch, outRoll, outYaw;
            public string camMode;
            public string gate;
            public float iPitch, iYaw, iRoll;
        }

        private const int CAP = 60;
        private static readonly Queue<Snapshot> buffer = new Queue<Snapshot>(CAP);
        private static int frameCounter;

        public static int Count => buffer.Count;

        public static void TryRecord(Snapshot snap)
        {
            frameCounter++;
            int interval = Mathf.Max(1, Plugin.TelemetryIntervalFrames.Value);
            if (frameCounter < interval) return;
            frameCounter = 0;

            if (buffer.Count >= CAP) buffer.Dequeue();
            buffer.Enqueue(snap);
        }

        public static void Dump(ManualLogSource logger, string reason)
        {
            if (buffer.Count == 0)
            {
                logger.LogInfo(
                    $"[Telemetry] dump ({reason}) -- buffer empty. " +
                    $"Recording requires: Plugin.Enabled=true (F9 toggle), aircraft present, " +
                    $"flight controls enabled, no cursor/chat/map open, orbit cam mode. " +
                    $"Current: Enabled={Plugin.Enabled}, camMode={CameraStateManager.cameraMode}.");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[Telemetry] dump ({reason}) -- {buffer.Count} entries:");

            int idx = 0;
            foreach (var s in buffer)
            {
                sb.AppendLine(
                    $"#{idx++:00} t={s.time:F2}s mode={s.camMode} gate={s.gate}\n" +
                    $"  camRaw   =({s.camForwardRaw.x:F3},{s.camForwardRaw.y:F3},{s.camForwardRaw.z:F3})\n" +
                    $"  camSmooth=({s.camForwardSmoothed.x:F3},{s.camForwardSmoothed.y:F3},{s.camForwardSmoothed.z:F3})\n" +
                    $"  planeFwd =({s.planeForward.x:F3},{s.planeForward.y:F3},{s.planeForward.z:F3})\n" +
                    $"  planeR   =({s.planeRight.x:F3},{s.planeRight.y:F3},{s.planeRight.z:F3})  planeU=({s.planeUp.x:F3},{s.planeUp.y:F3},{s.planeUp.z:F3})\n" +
                    $"  vel      =({s.velocity.x:F1},{s.velocity.y:F1},{s.velocity.z:F1}) |v|={s.velocity.magnitude:F1}m/s  bodyAngVel(p,y,r)=({s.angVel.x:F2},{s.angVel.y:F2},{s.angVel.z:F2}) rad/s\n" +
                    $"  spd={s.speed:F1}m/s  radAlt={s.radarAlt:F1}m\n" +
                    $"  STICK    pitch={s.stickPitch:+0.000;-0.000} roll={s.stickRoll:+0.000;-0.000} yaw={s.stickYaw:+0.000;-0.000}\n" +
                    $"  localTgt =({s.localTarget.x:F3},{s.localTarget.y:F3},{s.localTarget.z:F3})  angleOff={s.angleOff:F1}deg  fade={s.fade:F2}\n" +
                    $"  WRITTEN  pitch={s.outPitch:+0.000;-0.000} roll={s.outRoll:+0.000;-0.000} yaw={s.outYaw:+0.000;-0.000}\n" +
                    $"  PID-I    pitch={s.iPitch:+0.000;-0.000} roll={s.iRoll:+0.000;-0.000} yaw={s.iYaw:+0.000;-0.000}"
                );
            }

            logger.LogInfo(sb.ToString());
            // No auto-clear: keep history so multiple dumps in a row still work.
        }

        public static void Clear()
        {
            buffer.Clear();
            frameCounter = 0;
        }
    }
}
