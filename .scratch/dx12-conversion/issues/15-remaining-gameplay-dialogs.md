# Remaining gameplay + dialogs

Status: resolved

## What to build

Everything that makes the Sim complete beyond first flight, as ImGui panels and ported game systems: weather/wind dialog and simulation options; graphics and sound settings; controls setup (channel mapping + calibration UI); the remaining gameplay features — racing gates, towing, bombing, birds, scarecrow, tractor, lens flare, variometer; and the flight recorder/demo playback including the menu-background demo flight.

Work through features in whatever order keeps each landing demoable; split follow-up issues off this one if any feature proves large (likely candidates: controls/calibration UI, recorder/demo).

## Acceptance criteria

- [ ] All settings dialogs (weather, sim options, graphics, sound, controls incl. calibration) exist in ImGui and persist settings
- [ ] Racing, towing, bombing, birds, and remaining scenery/gameplay features work as in the old build
- [ ] Flight recorder records and replays; the menu-background demo flight plays
- [ ] No remaining references to deleted UI toolkit patterns; audio hooks (issue 12) integrated across features

## Blocked by

- 09, 12, 13

## Progress

Landed on `dx12` as four demoable increments, all verified by the `--gametest` suite (plus flytest/menutest/effectstest/contenttest regression):

1. 7580105 — Game shell (`--game`): chained menu -> aircraft -> scenery -> flight -> ESC -> menu; real Weather/Wind port (gust types, direction variance, turbulence, thermals + downdrafts, ground influence, wind sound) replacing the zero-wind stub; FlightSession extracted from FlightDemo; ImGui teardown crash fix.
2. 0acd452 — Settings: GameSettings (legacy Application.KeyValues in frameworkconfig.xml, table-preserving saves), ImGui Settings screen (Simulation/Graphics/Sound/Controls incl. move-the-stick axis assignment + live bars), weather persistence via the legacy 0-100 keys.
3. b455c01 + c15d9e6 — Gameplay: windsock (FlagCloth flag.x), variometer (legacy pitch/volume law), smoke trail (SmokeDetail-scaled, wind-drifted, S toggles), pylon racing (terrain.def gates, legacy crossing test, clock + arrow marker + gate.wav).
4. 9947b76 — Recorder/demo: legacy .dat format round-trips; stock demo.dat plays as the looping menu-background demo flight.

5. ac2da1f — Field actors: tractor (terrain-following drive pattern, spinning wheels), birds flock (legacy boids incl. aircraft scare), scarecrow game (cornfields, crop drain, arrow + status), bombing target; in-flight Game panel (Free flight / Racing / Scarecrow / Bombing).
6. 25324bc — Aerotow: SF260 towplane plays stock towing.dat; cable fed to the compiled-in UpdateCable physics in NED space; rope quad visual; T hooks up/releases; auto-release at 70 s.
7. (this commit) — Lens flare: five flare billboards along the sun line, frustum-gated, LensFlare setting + Graphics checkbox.

## Acceptance notes

- Settings dialogs: all present in ImGui and persisted (weather, sim, graphics, sound, controls incl. move-the-stick calibration).
- Racing, towing, bombing, birds (+ scarecrow, tractor, windsock, variometer, smoke, lens flare) work as ported from the legacy sources.
- Recorder records/replays the legacy .dat format; stock demo.dat is the menu-background demo flight.
- No DXUT patterns in any new code; audio (issue 12) integrated across engine/wind/variometer/gate/crash paths.

Deviations: everything is hosted in the TestHost GameShell until RCSim proper is resurrected at issue 17's gate; SceneryDetail/WaterDetail/ReflectionDetail persist but are not yet consumed by the renderer (water/reflection features read their own paths); RollExpo/PitchExpo/YawExpo and the welcome dialog were not ported.
