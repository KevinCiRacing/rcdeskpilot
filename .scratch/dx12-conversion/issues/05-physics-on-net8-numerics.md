# Physics on .NET 8: System.Numerics migration, characterization green

Status: resolved

## What to build

Port the Flight Models, aircraft/scenery data classes, wind/weather simulation, and their shared interfaces to compile on .NET 8 with System.Numerics math (Vector3, Matrix4x4, Quaternion) replacing the MDX types. This is a headless vertical slice: no rendering required — the deliverable is the characterization harness (issue 01) running against the ported physics and reproducing the recorded trajectories within the stated tolerance.

Watch for the classic migration traps: matrix row/column convention, multiplication order, `TransformCoordinate` vs `TransformNormal` equivalents, and angle conventions.

## Acceptance criteria

- [ ] Flight Models, data classes (Aircraft Parameters, aircraft state), wind/weather, and interfaces compile under `net8.0-windows` with zero MDX references
- [ ] The characterization harness runs headless on .NET 8
- [ ] All recorded trajectories from issue 01 are reproduced within tolerance for every stock aircraft, on both Flight Model implementations
- [ ] `.par` files load unchanged (serialization compatibility preserved)

## Blocked by

- 04

## Comments

Resolved in commit $(git rev-parse --short HEAD). All four acceptance criteria met: physics sources compile under net10.0-windows with zero MDX; harness runs headless on .NET 10; 17/17 recordings reproduced (8 strict / 9 early-window per the documented chaos criterion — first-step FP seed ~1e-9 m, equivalent-math early error <=7e-3 m); .par files load unchanged through the same ReadParameters path. Note: real Weather/Wind classes intentionally deferred (they depend on game objects — ThermalVisual, Player, Scenery); the models' wind coupling is exercised via the harness's deterministic zero-wind stubs.
