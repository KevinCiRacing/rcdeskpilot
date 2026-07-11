# First flight: the integrating tracer bullet

Status: claimed

## What to build

Wire the verticals together into a flyable Sim: pick an aircraft and scenery from the menu, spawn on the field, and fly with the Transmitter (or keyboard) — Flight Model driving the aircraft's Scene Nodes (including animated control surfaces and prop), ground contact/crash/reset, the fixed-pilot camera looking at the aircraft, and a basic HUD (center text messages and essential indicators) in ImGui.

This is the milestone where the Sim is demonstrably RC Desk Pilot again. Flight feel is validated against the old build by hand, beyond the automated characterization tolerance.

## Acceptance criteria

- [ ] Menu → aircraft + scenery → flying with a Transmitter, at stable interactive framerates
- [ ] Control surfaces, prop, and aircraft attitude visually track the Flight Model; ground contact, crash, and reset work
- [ ] Pilot-position camera behavior matches the old sim (look-at tracking, zoom behavior)
- [ ] Basic HUD shows flight messages/indicators via ImGui
- [ ] A side-by-side manual comparison of flight feel against the pre-bang build finds no regressions beyond tolerance

## Blocked by

- 05, 07, 08, 10, 11
