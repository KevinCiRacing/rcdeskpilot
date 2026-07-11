# Remaining gameplay + dialogs

Status: ready-for-agent

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

## Remaining (candidates to split off)

- Towing (towplane AI + rope physics; towing.dat exists for demo)
- Bombing, Birds, ScareCrow, Tractor (ambient/gameplay actors)
- Lens flare (renderer feature)
- Graphics detail settings currently persist but only SmokeDetail is consumed
