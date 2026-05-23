# NOCursorPilot Tuning Guide

How to tune the PID + flight controller. Edit values in `BepInEx/config/com.cursorpilot.NOCursorPilot.cfg`, save, then press **F3** in-game to reload (or restart game).

## Hotkeys

| Key | Action |
|-----|--------|
| F9 | Toggle mod on/off |
| F1 | Dump telemetry buffer to BepInEx log |
| F2 | Snapshot current config to `BepInEx/config/NOCursorPilot.profiles/profile_<timestamp>.cfg` |
| F3 | Reload config file from disk |

## Symptom → Fix Cheat Sheet

| Symptom | What to twist | Direction |
|---|---|---|
| Rolls too much / over-banks | `KdRoll` | RAISE (0.08 → 0.15+) |
| Rolls too little / lazy bank | `KdRoll` | LOWER (0.08 → 0.04) |
| Roll oscillates back-and-forth | `KdRoll` | RAISE. If WORSE → flip sign (negate) |
| Plane just yaws instead of banking on small turns | `AggressiveTurnAngle` | LOWER (3 → 1.5) |
| Plane wobbles the bank during turn | `AggressiveTurnAngle` | RAISE (3 → 5) |
| Pitch overshoots target altitude / nose bobs | `KdPitch` | RAISE |
| Pitch lazy / nose slow to come up | `Sensitivity` | RAISE (5 → 7) |
| Yaw snappy / nose flicks | `KdYaw` | RAISE |
| Plane drifts in yaw with cursor "still" (orbit-cam pivot offset) | `IdleFadeStrength` | RAISE (0.85 → 0.95). Fades commands when cursor stops moving. |
| Plane won't follow tiny cursor adjustments | `IdleFadeStrength` LOWER (0.85 → 0.5) OR `IdleThresholdDegPerSec` LOWER (3 → 1.5) | |
| Plane twitches at random tiny mouse jitter | `IdleThresholdDegPerSec` | RAISE (3 → 6) |
| Yaw drifts after maneuvers (joystick-like drift) | `Ki` | LOWER (0.05 → 0 or 0.01). I-term windup. |
| Plane drifts off target in steady cruise | `Ki` | RAISE (0.05 → 0.1) |
| Plane lurches after camera held still long | `Ki` | LOWER. Integrator wound up. |
| Stick output jittery with mouse still | `OutputSmoothing` | LOWER (5 → 3) for more smoothing |
| Plane feels laggy / slow to respond | `OutputSmoothing` | RAISE (5 → 10) for less smoothing |
| Plane responds to mouse jitter | `TargetSmoothing` | LOWER (3 → 1.5) for more smoothing |
| Plane feels detached / camera-tracking lazy | `TargetSmoothing` | RAISE (3 → 6) |
| Plane never settles, micro-corrects forever | `DeadzoneAngle` | RAISE (2 → 5) -- careful, kills fine adjust |
| Plane dives off course through deadzone | `DeadzoneAngle` | LOWER (2 → 1) |
| Plane pitches wrong way | `InvertPitch` | flip to `true` |
| Plane banks wrong way | `InvertRoll` | flip to `true` |

## About The Yaw Drift Symptom

The "joystick-like drift" after maneuvers happens when the **I-term accumulates** during sustained mild corrections. Even when the camera is re-centered, the integrator still pushes a small persistent command in the old direction. Plane drifts.

**Recommended starting Ki = 0** (disable I-term). Run the system as PD-only. P+D handles "fly toward camera" perfectly fine. I-term only matters for compensating gravity-induced steady-state offsets, which you usually don't need for a cursor-pilot.

Re-enable `Ki = 0.01-0.05` later ONLY if you notice the plane consistently drifts off-target in level cruise without correcting.

## Tuning Order

Get one knob right at a time. Order matters:

1. **Inversions** — `InvertPitch` / `InvertRoll`. If plane goes wrong way at all, fix this first.
2. **`AggressiveTurnAngle`** — get the bank-vs-yaw balance feeling right.
3. **Kd per axis** — eliminate oscillation. Start one axis at a time. Roll first (biggest oscillation source).
4. **`Sensitivity`** — overall responsiveness. Tune last because Kd values depend on Sensitivity scale.
5. **`Ki`** — only if you see drift after step 4. Default = 0 is fine.

## Quick Rules of Thumb

- **Oscillates** = D too low (most common)
- **Oscillates WORSE after raising D** = sign wrong → negate `Kd<axis>`
- **Sluggish** = P too low OR D too high
- **Drifts off-course** = I too low (rare)
- **Drifts after maneuver / lurches** = I too high or wound up → lower Ki

## Workflow

1. **F9** to enable mod
2. Pick ONE axis to focus on (start with roll)
3. Fly, note specific symptom
4. Alt-tab → edit `BepInEx/config/com.cursorpilot.NOCursorPilot.cfg`
5. Change ONE value
6. Save → alt-tab to game → **F3** to reload config
7. Test → repeat
8. When axis feels right → **F2** to snapshot profile
9. Move to next axis

## Snapshotting Profiles

**F2** dumps the current full config to `BepInEx/config/NOCursorPilot.profiles/profile_<timestamp>.cfg`.

Rename meaningful (`profile_fighter.cfg`, `profile_heavy.cfg`, etc).

To restore a profile: copy its `[Flight]` and `[Flight.PID]` sections back into the main config file, then **F3**.

## Why The Table Works

- **P-term** (driven by `Sensitivity`): how hard to push toward target → controls aggression
- **D-term** (`Kd<axis>`): anti-overshoot brake → controls oscillation
- **I-term** (`Ki`): corrects steady-state error → controls drift
- The blend angles (`AggressiveTurnAngle`, `DeadzoneAngle`) control WHEN each pathway activates

Same as any PID-controlled system.

## Telemetry For Diagnosis

Press **F1** to dump the last 60 frames to log. Each entry shows:

```
WRITTEN  pitch=+0.412 roll=-1.000 yaw=-1.000   <- what we wrote to controls
PID-I    pitch=-0.450 roll=+0.380 yaw=+1.000   <- I-term accumulated value per axis
bodyAngVel(p,y,r)=(-0.30,0.16,-2.63) rad/s     <- current rotation rate
localTgt=(-1.165,2.864,3.929) angleOff=38.2deg <- where target is in body frame
```

Use `PID-I` row to see if integrator is saturated (±1.0 = wound up). If it sits at ±1.0 for many frames → lower Ki or set to 0.

Use `bodyAngVel` to see if the plane is rotating fast (oscillation symptom).

Use `WRITTEN` vs target/angleOff to see whether commands are saturating (±1.000) at sane angles.
