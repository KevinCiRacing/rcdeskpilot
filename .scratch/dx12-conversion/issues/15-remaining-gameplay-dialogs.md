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
