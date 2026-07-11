# ImGui UI: backend + menu flow to launch a flight

Status: ready-for-agent

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
