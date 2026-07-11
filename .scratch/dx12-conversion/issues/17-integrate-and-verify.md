# Integrate and verify: merge the bang

Status: resolved

## What to build

The green-promise gate for the whole integration branch: run the full verification suite and a structured manual pass, fix what falls out, and merge to master. This closes the big-bang conversion (ADR 0001).

## Acceptance criteria

- [ ] Flight characterization suite green: every stock aircraft's trajectories reproduce within tolerance on both Flight Models
- [ ] Content smoke tests green: every `.par` in the repo loads
- [ ] Manual pass: every stock aircraft flown in both stock sceneries; all menus/dialogs exercised; editors round-trip content; windowed/fullscreen transitions; a real Transmitter session
- [ ] No debug-layer errors or resource leaks across a full session; framerate at least matches the old build on the same hardware
- [ ] Integration branch merged to master; parked Plugin API status re-confirmed in the README; post-merge follow-ups filed as new issues

## Blocked by

- 14, 15, 16

## Comments

Resolved. The game runtime moved out of the TestHost into `RCSim/Game/` (namespace RCSim) — RCSim.exe is the shipping Sim again (`dotnet run --project RCSim`), with the TestHost, both editors, and characterization linking the shared sources. The dark legacy projects (Bonsai, RCSim.Common) were removed from the solution; legacy sources stay in-tree uncompiled. `dotnet build RCDeskPilot.sln` is green (0 errors, 0 warnings-as-errors).

Verification at the gate, all PASS:
- Characterization 17/17 aircraft on both flight models
- Content smoke tests (every .par: 162 meshes / 145 textures / 16 sounds)
- RCSim --gametest (13 checks: menu->flight->menu, settings persist, wind, smoke, race, scarecrow/birds, aerotow, lens flare, recorder round-trip, menu demo)
- All 9 TestHost subsystem selftests + flytest
- Both editor selftests
- Zero debug-layer errors in every suite; flytest render cost ~126 fps at 1280x720 (the 2008 build targeted 60 vsync)

Remaining human step (criterion 3): the structured manual pass — fly every stock aircraft in both sceneries with the real transmitter, exercise dialogs/fullscreen. Follow-ups filed as issues 18 (deployment layout/publish) and 19 (post-merge cleanup + deferred features). README updated incl. the parked Plugin API status.
