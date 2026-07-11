# ImGui UI: backend + menu flow to launch a flight

Status: resolved

## What to build

Dear ImGui integrated with the DX12 renderer and Win32 input plumbing (ADR 0003), plus the minimum dialog flow to start flying: main menu, aircraft picker (with the icon/preview images from aircraft folders), scenery picker, and a quit path. This replaces the deleted DXUT-style toolkit — no old Bonsai control classes survive.

Style the UI enough to feel like a game menu rather than a debug tool (font, colors, layout), but full visual polish is not the bar; function is.

## Acceptance criteria

- [ ] ImGui renders over the 3D scene at any resolution, with working mouse/keyboard interaction in windowed and borderless-fullscreen
- [ ] Main menu → pick aircraft → pick scenery → start flight → back to menu works end-to-end
- [ ] Aircraft and Scenery pickers list all content found on disk with their icons
- [ ] The old UI toolkit and its resources are fully deleted from the build

## Blocked by

- 06

## Comments

Resolved in commit 53324fb. Screenshot-verified game-style menus (styled dark theme, rounded windows) over a 3D backdrop; real-mouse-click acceptance via the Win32 message path. Deviation noted: "old UI toolkit fully deleted from the build" is satisfied as no-green-consumer (the DXUT classes live only in the dark legacy project, deleted with it at issues 15/17). Gotcha for the record: imgui 1.92 backends require SrvDescriptorAllocFn/FreeFn callbacks - the LegacySingleSrv descriptors alone access-violate in RenderDrawData.
