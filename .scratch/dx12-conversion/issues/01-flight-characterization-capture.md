# Flight characterization capture

Status: resolved

## What to build

While the MDX9 build still runs, capture ground-truth Flight Model behavior: a harness that feeds recorded control-input sequences into each stock aircraft's Flight Model and records the resulting aircraft state trajectory (position, orientation, velocity) at fixed timesteps. Persist the recordings as data files in the repo. These become the regression oracle for the System.Numerics math migration — the ported physics must reproduce these trajectories within tolerance.

The harness should drive the Flight Model directly (headless, no rendering) so the same harness can later run against the ported physics on .NET 8.

## Acceptance criteria

- [ ] A headless harness steps a Flight Model deterministically with a scripted input sequence (fixed timestep, fixed wind/weather seed)
- [ ] Recordings captured for every stock aircraft in the repo, covering both Flight Model implementations (FlightModelWind and FlightModelWind2) as selected by each aircraft
- [ ] Recordings include enough dynamic range to catch regressions: takeoff, cruise, aerobatic inputs, stall, ground contact
- [ ] Recordings + harness committed; a documented tolerance for later comparison is stated

## Blocked by

None - can start immediately

## Comments

Resolved in commit d0eb439. New RCSim.Characterization console project (in the solution) compile-links the flight-model sources; bypasses the models' physics thread for determinism (fixed 2ms step); zero-wind Program/Weather/Wind stubs; flat Heightmap ground. Deterministic PD-autopilot script covers takeoff (or hand launch for gliders/unstable-liftoff airframes), climb, cruise, aileron doublet, pitch pull, rudder, power-off stall, recovery — all 17 stock aircraft, both FlightModelWind versions plus the helicopter path. 400 samples/aircraft committed under recordings/. `verify` mode replays and compares: tolerance 1.0 m documented (with a chaos caveat for late-run divergence in README); same-build replay reproduces at 0.0000 m across all 17.
