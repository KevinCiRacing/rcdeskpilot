# Integrate and verify: merge the bang

Status: ready-for-agent

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
