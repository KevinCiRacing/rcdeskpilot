# RCSim.Characterization

Headless flight-model characterization harness. It compiles the Sim's Flight
Model sources directly (same pattern as the Aircraft Editor), steps them with
a deterministic scripted autopilot, and records the resulting trajectories.
These recordings are the regression oracle for the physics port (System.Numerics
migration, .NET 8) — the ported models must reproduce them within tolerance.

## Usage

```
RCSim.Characterization.exe record [aircraftRoot] [recordingRoot]
RCSim.Characterization.exe verify [aircraftRoot] [recordingRoot] [toleranceMeters]
```

Defaults resolve relative to the repo (`RCSim\Aircraft`, `RCSim.Characterization\recordings`).
`verify` exits 0 when every aircraft stays within tolerance, 1 otherwise.

## How determinism is achieved

- The models' own physics thread (`Initialize()`/`ModelRun`, wall-clock timed)
  is bypassed; the harness calls `UpdateControls` + `MoveScene` directly at a
  fixed 2 ms step (mirroring the thread's cadence).
- `Program`/`Weather`/`Wind` are zero-wind stubs (see `Program.cs`); the real
  Wind's turbulence randomness never enters the loop.
- Ground is a flat, zero-height `Heightmap`; no water.
- The control script is a pure function of elapsed time and model state
  (PD altitude hold + wing leveler), so replays are bit-identical.
- Built without the `DEBUG` constant: the models guard a `Framework.Instance`
  access with `#if DEBUG` that would NRE headless.

## The script (40 s, sampled every 0.1 s → 400 rows per aircraft)

Fixed-wing: settle on gear → full-throttle takeoff roll and climb toward 60 m
(unpowered or unstable-at-liftoff airframes get a single mid-air hand launch at
t≥8 s instead) → aileron doublet → pitch pull → rudder input → power-off stall
ramp → recovery and descent toward 25 m. Helicopters fly a throttle-driven
climb/hover/translate/set-down profile instead.

## Pass criterion (two-tier)

Flight dynamics are chaotic — even a parked aircraft chatters on its gear —
so genuinely equivalent math with different FP rounding (SIMD vs x87) seeds a
~1e-9 m difference on the first step that can amplify exponentially. Verified
during the System.Numerics port: equivalent physics stays ≤ 7e-3 m through the
first 1.5 s on every stock aircraft, while late trajectories may diverge by
hundreds of meters with perfectly smooth error growth and agreeing
crash/touchdown states.

A verify run therefore passes an aircraft if either:

- **PASS (strict)** — position error ≤ **1.0 m at every 0.1 s sample** over
  the full 40 s, or
- **PASS~ (early-window)** — error ≤ **0.01 m through t = 1.5 s** (a genuine
  physics bug shows decimeters within the first second; chaos does not).

Same-build replays produce 0.0000 m everywhere. The .NET 10 + System.Numerics
port passed 8/17 strict (covering both FlightModelWind versions, helicopter,
gliders, and ground takeoffs end-to-end incl. stall) and 9/17 early-window.
When judging a future port: FAIL means a real regression; a fleet with zero
strict passes would also be suspicious even if early windows pass.

## CSV format

`t,throttle,elevator,ailerons,rudder,x,y,z,qw,qx,qy,qz,vx,vy,vz,crashed,touchedDown`

Positions/velocities are the model's inertial NED frame (X north, Y east,
Z down — altitude is `-z`); the quaternion is built from yaw/pitch/roll via
`Quaternion.RotationYawPitchRoll`. Floats use round-trip (`R`) formatting,
invariant culture.
