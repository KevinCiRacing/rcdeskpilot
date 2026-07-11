# Editors on the new stack

Status: ready-for-agent

## What to build

Bring the Aircraft Editor and Scenery Editor up on the new engine: WinForms hosts (net8.0-windows) embedding the renderer via panel HWNDs (the renderer is host-agnostic per ADR 0002), their shared Sim source files already ported by earlier issues, and their tool UI. Replace the old D3DX-font graph/preview controls with equivalents on the new stack (ImGui panels or WinForms-native drawing, whichever fits each control best).

## Acceptance criteria

- [ ] Aircraft Editor opens, renders an aircraft in its viewport, edits Aircraft Parameters, and saves a `.par` the Sim loads
- [ ] Scenery Editor opens, renders a Scenery, edits object placement/terrain, and saves output the Sim loads
- [ ] Graph/curve controls (aerodynamics visualization) work on the new stack
- [ ] Both editors share the Sim's ported source files via project links — no re-duplicated sources

## Blocked by

- 13
