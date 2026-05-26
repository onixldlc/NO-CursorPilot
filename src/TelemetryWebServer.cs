using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;

namespace NOCursorPilot
{
    // Lightweight HTTP server that exposes live plane state for PID tuning.
    // Self-bootstraps on first UpdateState() call — no Plugin.Awake wiring required.
    //
    // Endpoints:
    //   GET /       -> HTML dashboard, polls /data every 100ms
    //   GET /data   -> JSON snapshot: pitch, yaw, roll, speed, alt
    //
    // To feed it: call TelemetryWebServer.UpdateState(aircraft) from CursorFlightPatch.Postfix
    // (or anywhere with an Aircraft ref). It derives angles from the transform.
    //
    // Port defaults to 7070. Override with env NO_CURSORPILOT_WEB_PORT, e.g. "7080".
    internal static class TelemetryWebServer
    {
        private const int DefaultPort = 8111;

        private static HttpListener _listener;
        private static Thread       _thread;
        private static volatile bool _running;
        private static int          _port;

        // Latest state (single writer = patch thread, multi reader = HTTP threads).
        // Floats are atomic on 32-bit-aligned writes on x86/x64; struct snapshot below
        // copies into locals before serialization to keep the JSON internally consistent.
        private static float _ctrlPitch, _ctrlYaw, _ctrlRoll;  // -1..1, what mod writes to ControlInputs
        private static float _speed, _alt;
        private static float _heading, _incline;               // plane attitude (degrees)
        private static float _camHeading, _camIncline;         // cursor-pilot target direction (degrees)
        private static float _velHeading, _velIncline;         // velocity vector direction (degrees)
        private static bool  _camValid;

        public static void Start() => EnsureStarted();

        public static void UpdateState(Aircraft aircraft, ControlInputs inputs, Vector3? camForward = null)
        {
            if (aircraft == null) return;
            EnsureStarted();

            Transform t = aircraft.transform;
            Vector3 fwd = t.forward;

            _heading = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;                       // -180..180
            _incline = Mathf.Asin(Mathf.Clamp(fwd.y, -1f, 1f)) * Mathf.Rad2Deg;          // -90..90
            _speed   = aircraft.speed;
            _alt     = aircraft.radarAlt;

            // Velocity-vector direction (flight path), separate from nose direction.
            if (aircraft.rb != null && aircraft.rb.velocity.sqrMagnitude > 1f)
            {
                Vector3 v = aircraft.rb.velocity.normalized;
                _velHeading = Mathf.Atan2(v.x, v.z) * Mathf.Rad2Deg;
                _velIncline = Mathf.Asin(Mathf.Clamp(v.y, -1f, 1f)) * Mathf.Rad2Deg;
            }

            if (inputs != null)
            {
                _ctrlPitch = inputs.pitch;
                _ctrlYaw   = inputs.yaw;
                _ctrlRoll  = inputs.roll;
            }

            if (camForward.HasValue)
            {
                Vector3 c = camForward.Value;
                _camHeading = Mathf.Atan2(c.x, c.z) * Mathf.Rad2Deg;
                _camIncline = Mathf.Asin(Mathf.Clamp(c.y, -1f, 1f)) * Mathf.Rad2Deg;
                _camValid   = true;
            }
            else
            {
                _camValid = false;
            }
        }

        public static void Stop()
        {
            _running = false;
            try { _listener?.Stop();  } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
        }

        private static void EnsureStarted()
        {
            if (_listener != null) return;
            lock (typeof(TelemetryWebServer))
            {
                if (_listener != null) return;

                _port = DefaultPort;
                var envPort = System.Environment.GetEnvironmentVariable("NO_CURSORPILOT_WEB_PORT");
                if (!string.IsNullOrEmpty(envPort) && int.TryParse(envPort, out int p)) _port = p;

                try
                {
                    _listener = new HttpListener();
                    _listener.Prefixes.Add($"http://localhost:{_port}/");
                    _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                    _listener.Start();
                    _running = true;

                    _thread = new Thread(Loop) { IsBackground = true, Name = "NOCursorPilot.Web" };
                    _thread.Start();

                    Plugin.LogSource?.LogInfo($"[Web] listening at http://localhost:{_port}/");
                }
                catch (System.Exception ex)
                {
                    Plugin.LogSource?.LogError($"[Web] failed to bind on port {_port}: {ex.Message}");
                    _listener = null;
                }
            }
        }

        private static void Loop()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { break; }
                ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
            }
        }

        private static void Handle(HttpListenerContext ctx)
        {
            try
            {
                string path = ctx.Request.Url.AbsolutePath;
                if (path == "/data")
                {
                    // Snapshot to local copies so JSON fields are mutually consistent.
                    float cp = _ctrlPitch, cy = _ctrlYaw, cr = _ctrlRoll;
                    float s  = _speed, a = _alt;
                    float h  = _heading, inc = _incline;
                    float ch = _camHeading, ci = _camIncline;
                    bool  cv = _camValid;
                    string json = "{\"ctrlPitch\":" + F(cp) +
                                  ",\"ctrlYaw\":"   + F(cy) +
                                  ",\"ctrlRoll\":"  + F(cr) +
                                  ",\"speed\":"     + F(s)  +
                                  ",\"alt\":"       + F(a)  +
                                  ",\"heading\":"   + F(h)  +
                                  ",\"incline\":"   + F(inc) +
                                  ",\"camHeading\":" + F(ch) +
                                  ",\"camIncline\":" + F(ci) +
                                  ",\"velHeading\":" + F(_velHeading) +
                                  ",\"velIncline\":" + F(_velIncline) + "}";
                    Send(ctx, "application/json", json);
                    return;
                }

                // Map URL path to embedded resource. "/" -> pages/index.html, "/foo.js" -> pages/foo.js.
                string resourceName = path == "/" || path == ""
                    ? "pages/index.html"
                    : "pages" + path;
                string body = LoadResource(resourceName);
                if (body == null)
                {
                    ctx.Response.StatusCode = 404;
                    Send(ctx, "text/plain", "Not found: " + resourceName);
                    return;
                }
                Send(ctx, ContentTypeFor(resourceName), body);
            }
            catch { }
            finally
            {
                try { ctx.Response.OutputStream.Close(); } catch { }
                try { ctx.Response.Close(); } catch { }
            }
        }

        private static string LoadResource(string name)
        {
            var asm = typeof(TelemetryWebServer).Assembly;
            using (Stream s = asm.GetManifestResourceStream(name))
            {
                if (s == null) return null;
                using (var r = new StreamReader(s, Encoding.UTF8))
                    return r.ReadToEnd();
            }
        }

        private static string ContentTypeFor(string name)
        {
            if (name.EndsWith(".html", System.StringComparison.OrdinalIgnoreCase)) return "text/html; charset=utf-8";
            if (name.EndsWith(".js",   System.StringComparison.OrdinalIgnoreCase)) return "application/javascript; charset=utf-8";
            if (name.EndsWith(".css",  System.StringComparison.OrdinalIgnoreCase)) return "text/css; charset=utf-8";
            if (name.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase)) return "application/json";
            if (name.EndsWith(".svg",  System.StringComparison.OrdinalIgnoreCase)) return "image/svg+xml";
            return "application/octet-stream";
        }

        private static void Send(HttpListenerContext ctx, string contentType, string body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.Headers["Cache-Control"] = "no-store";
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }

        // Invariant-culture float -> string (no commas as decimal separator).
        private static string F(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }
}
