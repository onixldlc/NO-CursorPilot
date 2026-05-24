# NO-CursorPilot

A mod that turns your **Nuclear Option** into a more arcade-y game control. in short this mod allows you to flies your aircraft toward where the orbit camera is looking.

## Install

1. Install BepInEx 5 in the Nuclear Option game directory.
2. Drop `NO-CursorPilot.dll` into `/path/to/NO/BepInEx/plugins/`.
3. Launch the game. Press **F9** to toggle the mod on.

## Hotkeys

| Key | Action |
|-----|--------|
| F9  | Toggle cursor pilot on / off |
| F1  | Dump telemetry buffer to BepInEx log |
| F2  | Snapshot current PID + tuning to `BepInEx/config/NOCursorPilot.profiles/profile_<timestamp>.cfg` |
| F3  | Reload `BepInEx/config/com.cursorpilot.NOCursorPilot.cfg` from disk |

## How it works

Two Harmony patches:

### `CursorFlightPatch` — Postfix on `PilotPlayerState.PlayerAxisControls`
Each fixed-update tick (only when mod is enabled, aircraft is active, orbit cam is selected, and no UI / chat / map is open):

1. Reads camera forward, smooths it with `TargetSmoothing` lambda.
2. Projects a virtual fly-target `AimDistance` meters ahead of the plane.
3. Transforms the target into the aircraft's local frame; the resulting `(x, y)` becomes yaw / pitch error.
4. Roll error blends between wings-level and target-x by `AggressiveTurnAngle`.
5. Three PID controllers (P=1 fixed, Ki shared, Kd per axis) produce pitch / roll / yaw commands.
6. A small deadzone fade kills micro-corrections near target.
7. Output is smoothed (`OutputSmoothing` lambda) then written to `ControlInputs`.
8. Holding stick beyond a small threshold, or `Free Look`, hands control back to the player.

### `CameraOrbitPatch` — Transpiler + Postfix on `CameraOrbitState`
Nuclear Option's orbit camera has three smoothing layers that interact badly with cursor piloting (camera self-drift, lag on input):

| Game line | Smoothing call | What this patch replaces it with |
|-----------|---------------|-----------------------------------|
| `CameraMotion` line 170 | `Vector3.RotateTowards(flatVelSmoothed, target, ...)` | `target` (snap, no rate limit) |
| `CameraMotion` line 177 | `Vector3.SmoothDamp(followVector, flatVelSmoothed, ref cameraVelocity, viewSmoothing)` | `target` (snap, no damp) |
| `CameraMotion` line 182 | `Quaternion.Lerp(pivotRotationPrev, current, num*5)` (first Lerp only) | `current` (no low-pass on player input) |

Look-at-target Lerps (lines 214 / 227 / 230) are left untouched.

A separate Postfix on `UpdateState` subtracts the plane's heading delta from `panView` each frame. The removed Lerp on line 182 used to provide implicit world-absolute camera lock via its low-pass behavior; replacing it with discrete heading compensation restores absolute lock without re-introducing a filter that accumulates drift.

All three smoothing pass-throughs gate on `Plugin.Enabled` and call the original Unity functions when the mod is off, so disabling restores stock orbit-cam behavior.

## Tuning

See [TUNING.md](TUNING.md) for a symptom -> knob cheat sheet, recommended tuning order, and telemetry-dump interpretation guide.

Default config lives at `BepInEx/config/com.cursorpilot.NOCursorPilot.cfg` after first launch. Edit, save, press **F3** in game to apply without restarting.

## Building

All builds run inside a Docker / Podman container — no host `dotnet` install required.

```bash
podman build -f Dockerfile.ci -t no-cursorpilot-build .
cid=$(podman create no-cursorpilot-build)
podman cp "$cid":/build/bin/Release/NO-CursorPilot.dll ./NO-CursorPilot.dll
podman rm "$cid"
```

Output: `NO-CursorPilot.dll` at the repo root (or `bin/Release/NO-CursorPilot.dll` inside the container).

The build references stub assemblies under `.github/ci/stubs/` containing just the signatures of game / Rewired types the mod calls. No proprietary game code is bundled.

## Project layout

```
src/
  Plugin.cs              BepInEx entry, config binding, hotkeys, OnGUI label
  CursorFlightPatch.cs   PID flight controller patching PilotPlayerState
  CameraOrbitPatch.cs    Smoothing-removal + heading-compensation patches for orbit cam
  PidController.cs       P+I+D with conditional-integration anti-windup
  PidProfileDumper.cs    F2 handler: dumps live tuning to a .cfg
  TelemetryRecorder.cs   Ring buffer + F1 dump formatter

.github/ci/
  Dockerfile.ci          (at repo root) container build definition
  NOAutopilot.CI.csproj  netstandard2.1 project, BepInEx + UnityEngine.Modules
  stubs/                 Minimal signature stubs (Assembly-CSharp, Rewired_Core)
```