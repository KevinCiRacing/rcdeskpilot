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

## Tolerance

Default verify tolerance: **1.0 m of position error at any 0.1 s sample**.
Same-build replays produce 0.0000 m. Note for the port: flight dynamics are
chaotic — especially post-stall — so genuinely equivalent math with different
FP rounding (SIMD, reordering) may diverge late in the run. If the port fails
verification only late (`divergedAt` ≥ ~27 s, the stall phase) with a smooth
error growth, judge by divergence time and early-phase error rather than the
end-to-end max; a physics *bug* shows up as early divergence (takeoff/cruise).

## CSV format

`t,throttle,elevator,ailerons,rudder,x,y,z,qw,qx,qy,qz,vx,vy,vz,crashed,touchedDown`

Positions/velocities are the model's inertial NED frame (X north, Y east,
Z down — altitude is `-z`); the quaternion is built from yaw/pitch/roll via
`Quaternion.RotationYawPitchRoll`. Floats use round-trip (`R`) formatting,
invariant culture.
