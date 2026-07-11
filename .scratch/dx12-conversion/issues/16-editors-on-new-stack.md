# Editors on the new stack

Status: resolved

## What to build

Bring the Aircraft Editor and Scenery Editor up on the new engine: WinForms hosts (net8.0-windows) embedding the renderer via panel HWNDs (the renderer is host-agnostic per ADR 0002), their shared Sim source files already ported by earlier issues, and their tool UI. Replace the old D3DX-font graph/preview controls with equivalents on the new stack (ImGui panels or WinForms-native drawing, whichever fits each control best).

## Acceptance criteria

- [ ] Aircraft Editor opens, renders an aircraft in its viewport, edits Aircraft Parameters, and saves a `.par` the Sim loads
- [ ] Scenery Editor opens, renders a Scenery, edits object placement/terrain, and saves output the Sim loads
- [ ] Graph/curve controls (aerodynamics visualization) work on the new stack
- [ ] Both editors share the Sim's ported source files via project links — no re-duplicated sources

## Blocked by

- 13

## Comments

Resolved. Both editors are WinForms hosts embedding the DX12 renderer via their viewport panel's HWND (ADR 0002), each with a `--selftest` (run with `dotnet run --project RCSim.AircraftEditor -- --selftest` / `RCSim.SceneryEditor`).

- Aircraft Editor: orbit-camera viewport rendering the aircraft with live control-surface/prop animation; every AircraftParameters field editable in a PropertyGrid (model rebuilds on change); the legacy GDI GraphControl compiled unchanged for lift/drag coefficient curves (point-drag editing included); Open/Save/Save As through the Sim's own reader/writer — selftest round-trips an edited save through `ReadParameters`.
- Scenery Editor: the default field rendered live from the Sim's TerrainDefinition DataSet (ported to System.Numerics — the DataSet XML shape is unchanged, verified against the stock terrain.def: 7 gates, 4 thermals); right-click places trees/windmills/gates/thermals on ray-picked terrain, delete-nearest, Save writes terrain.def — selftest round-trips an added tree.
- Shared sources are project links (AircraftParameters, interfaces, TerrainDefinition, AircraftVisual, FrameCapture) — nothing duplicated.

Deviations: the legacy dialog-form suite (AircraftParametersForm, CoefficientsForm, wizards, ...) is replaced by the PropertyGrid + embedded graphs rather than ported form-by-form; the old panel-HWND-free DXUT hosting, ads and D3DX-font controls are gone. Scenery editor edits placement (not heightmap painting), matching the legacy editor's actual capability.
