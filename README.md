# NO-CursorPilot

A mod that turns your **Nuclear Option** into a more arcade-y game control. In short this mod allows you to fly your aircraft toward where the orbit camera is looking.

![preview](preview-cursorPilot.gif)

>[!NOTE]
> This preview uses 2 other mods: [ThirdPersonHUD](https://github.com/OverlordMIke/Nuclear-Option-Mod-ThirdPersonHUD) to show the HUD while in orbit camera mode, and [CustomOrbit](https://github.com/onixldlc/NO-CustomOrbit) so you can move the center point to the top of the jet while in orbit camera mode.

## Install

1. Install BepInEx 5 in the Nuclear Option game directory.
2. Drop `NO-CursorPilot.dll` into `/path/to/NO/BepInEx/plugins/`.
3. Launch the game. Cursor pilot is **ON by default** — press **F9** to toggle it off / back on.

## Hotkeys

| Key | Action |
|-----|--------|
| F9       | Toggle cursor pilot on / off (default: ON at game start) |
| F1       | Reload `BepInEx/config/com.cursorpilot.NOCursorPilot.cfg` from disk |
| unbound  | `DumpKey` — dump telemetry buffer to BepInEx log (bind via config) |
| unbound  | `DumpProfileKey` — snapshot current PID + tuning to `BepInEx/config/NOCursorPilot.profiles/profile_<timestamp>.cfg` (bind via config) |

Auto-dump of telemetry on camera-mode change is enabled by default (`[Telemetry] DumpOnCameraModeChange = true`).

## Free Look behavior

Holding the game's **Free Look** key changes what the mod tracks. Two configurable behaviors via `[Flight] TurnToFreelook`:

- **`TurnToFreelook = false` (default)** — the moment you press Free Look, the mod snapshots the cursor-pilot target direction. While held you can mouse around freely; the plane stays committed to the saved spot. On release the camera animates back to that exact saved direction over `FreelookRecoverySeconds`, then a `FreelookGraceSeconds` window keeps the mod tracking the saved direction so no jerk happens when live-cam tracking resumes.
- **`TurnToFreelook = true`** — on release, plane immediately chases wherever the camera ended up (classic cursor-pilot chase).

Stick / keyboard flight input always wins over the mod — push the stick beyond a small threshold at any time and you regain manual control.

## How it works

Two Harmony patches.

### `CursorFlightPatch` — Postfix on `PilotPlayerState.PlayerAxisControls`

Each fixed-update tick (only when mod is enabled, aircraft is active, orbit cam is selected, and no UI / chat / map is open):

1. Reads camera forward — either live `cam.transform.forward` or the saved Free-Look snapshot, depending on state.
2. Smooths it with `TargetSmoothing` lambda (skipped during Free Look hold: snap to saved direction).
3. Projects a virtual fly-target `AimDistance` meters ahead of the plane.
4. Transforms the target into the aircraft's local frame; the resulting `(x, y)` becomes yaw / pitch error.
5. Computes `angleOff` from the plane's **velocity direction** (not nose forward) so the controller closes the lag between nose attitude and actual flight path.
6. Roll error blends between wings-level and target-x by `AggressiveTurnAngle`.
7. Three PID controllers (P = 1 fixed, Ki shared, Kd per axis) produce pitch / roll / yaw commands.
8. Output is smoothed (`OutputSmoothing` lambda) then written to `ControlInputs`.
9. Holding stick beyond a small threshold hands control back to the player at any time.

### `CameraOrbitPatch` — Transpiler + Postfix on `CameraOrbitState`

Nuclear Option's orbit camera has three smoothing layers that interact badly with cursor piloting (camera self-drift, lag on input). A Harmony Transpiler replaces only those three call sites with no-op pass-throughs:

| Game line | Smoothing call | Replacement |
|-----------|---------------|-------------|
| `CameraMotion` line 170 | `Vector3.RotateTowards(flatVelSmoothed, target, ...)` | `target` (snap, no rate limit) |
| `CameraMotion` line 177 | `Vector3.SmoothDamp(followVector, flatVelSmoothed, ref cameraVelocity, viewSmoothing)` | `target` (snap, no damp) |
| `CameraMotion` line 182 | `Quaternion.Lerp(pivotRotationPrev, current, num*5)` (first Lerp only) | `current` (no low-pass on player input) |

Look-at-target Lerps (lines 214 / 227 / 230) are left untouched.

A Postfix on `UpdateState` handles two things:

- **Heading compensation** — subtracts the plane's per-frame yaw delta from `panView` so the camera stays world-absolute. The removed Lerp on line 182 used to provide implicit world-absolute lock via low-pass behavior; this discrete subtraction restores it without any filter (no drift source).
- **Free Look snapshot & recovery** — captures `cam.cameraPivot.rotation * forward` and `cam.transform.forward` at press transition. On release (when `TurnToFreelook = false`), lerps `panView` / `tiltView` back to values that reproduce the saved pivot direction given the plane's current heading. After recovery, a grace window keeps the mod tracking the saved cursor-pilot direction.

All three smoothing pass-throughs gate on `Plugin.Enabled` and call the original Unity functions when the mod is off, so disabling restores stock orbit-cam behavior.

## Tuning

See [TUNING.md](TUNING.md) for a symptom -> knob cheat sheet, recommended tuning order, and telemetry-dump interpretation guide.

Default config lives at `BepInEx/config/com.cursorpilot.NOCursorPilot.cfg` after first launch. Edit, save, press **F1** in game to apply without restarting.

Default PID tuning (`[Flight.PID]`):

| Key | Default |
|-----|---------|
| `Ki` | `0.05` |
| `IntegralLimit` | `1.0` |
| `KdPitch` | `0.18` |
| `KdYaw` | `0.15` |
| `KdRoll` | `-0.35` (note: negative) |

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
  CameraOrbitPatch.cs    Smoothing-removal + heading-compensation + Free Look recovery
  PidController.cs       P+I+D with conditional-integration anti-windup
  PidProfileDumper.cs    Dumps live tuning to a .cfg (invoked by DumpProfileKey)
  TelemetryRecorder.cs   Ring buffer + dump formatter (invoked by DumpKey or auto-dump)

.github/ci/
  NOAutopilot.CI.csproj  netstandard2.1 project, BepInEx + UnityEngine.Modules
  stubs/                 Minimal signature stubs (Assembly-CSharp, Rewired_Core)

Dockerfile.ci            (repo root) container build definition
```