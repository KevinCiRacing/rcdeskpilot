# DX12 Conversion — PRD

Status: in progress (integration branch: `dx12`)

The plan of record lives in the ADRs ([0001](../../docs/adr/0001-big-bang-port-to-net8-dx12.md)–[0004](../../docs/adr/0004-runtime-assimp-for-x-assets.md))
and the issue breakdown in `issues/`. Pre-phase (issues 01–03) landed on `master`
at d0eb439 — the last MDX9-working commit. Everything after happens on `dx12`,
which is dark (does not fully compile) until issue 17's integrate-and-verify gate.

## Project/dependency graph (post issue 04)

All active projects: SDK-style, `net10.0-windows` (satisfies the "modern .NET"
decision; .NET 10 is the current LTS), x64, shared props in `Directory.Build.props`.

```
Bonsai.Graphics (NEW: clean DX12 renderer core, host-agnostic per ADR 0002)
├── Vortice.Direct3D12 / Vortice.DXGI / Vortice.Dxc   ← device layer (issue 06: DONE)
├── AssimpNet                                          ← .X/glTF import (issue 07)
└── Hexa.NET.ImGui                                     ← UI (issue 10)

Bonsai.Graphics.TestHost (NEW: WinExe smoke test; `--selftest` renders 80
frames through resize + borderless fullscreen + restore, verifies the
triangle in a readback screenshot and zero debug-layer errors)

Bonsai (legacy engine lib — DARK, ported/absorbed piece by piece)
├── Vortice.DirectInput                                ← input (issue 11)
└── Vortice.XAudio2                                    ← audio (issue 12)

RCDeskPilot.API (lib, no deps)      ← plugin surface PARKED*, assembly still
                                       builds: RCSim compiles against its types
RCSim (WinExe)                      → Bonsai, RCDeskPilot.API
RCSim.AircraftEditor (WinExe)       → Bonsai, RCDeskPilot.API + 18 linked RCSim sources
RCSim.SceneryEditor (WinExe)        → Bonsai + 18 linked RCSim sources
RCSim.Characterization (console)    → Bonsai, RCDeskPilot.API + 6 linked RCSim sources
RCSim.Common (lib)                  → Bonsai  (referenced by nothing — deletion candidate)

RCDeskPilot.API.Sample              ← PARKED: removed from solution, in-tree, unconverted
```

*Deviation from issue 04 as written: the plan said to park the whole Plugin API,
but RCSim's flight-model plumbing (`FlightModelApi`, `Player`, `AircraftParameters.ToApi`)
compiles against RCDeskPilot.API types, so the API *assembly* stays in the build.
What's parked is the compatibility promise and the Sample.

## Build state on `dx12` after issue 04

`dotnet build RCDeskPilot.sln`: restore succeeds (Vortice 3.6.2, AssimpNet 4.1.0,
Hexa.NET.ImGui 2.2.7); compile reports ~1,238 errors, all CS0246/CS0234 from the
removed Managed DirectX types — i.e. exclusively code awaiting porting. The
project skeleton itself (props, references, globs, resx) introduces no errors.
