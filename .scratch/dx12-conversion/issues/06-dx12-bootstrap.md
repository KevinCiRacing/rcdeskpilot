# DX12 bootstrap: window, device, swapchain, first triangle

Status: claimed

## What to build

The foundation of the new Bonsai renderer per ADR 0002: a raw Win32 window and message loop for the Sim, and a DX12 device layer — adapter selection, command queue, flip-model swapchain, per-frame fence synchronization, depth buffer, and a frame loop that clears and presents. Prove the pipeline end-to-end by rendering a hardcoded triangle (root signature, PSO, vertex buffer, HLSL SM6 shaders via DXC).

The renderer must be host-agnostic: it accepts an HWND and size, never referencing any UI framework (the Editors will later hand it WinForms panel handles). Include resize handling and a debug-layer toggle.

## Acceptance criteria

- [ ] Sim executable opens a Win32 window (windowed and borderless-fullscreen) and renders a cleared, vsynced frame loop with a visible triangle
- [ ] Renderer initialization takes only HWND + size; no WinForms/WPF types appear in renderer code
- [ ] Window resize and minimize/restore work without device errors; debug layer reports no live-object leaks on shutdown
- [ ] Frame pacing uses flip-model presentation with N-buffered fences

## Blocked by

- 04
