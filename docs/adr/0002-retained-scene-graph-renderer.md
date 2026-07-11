# Retained scene graph renderer, host-agnostic

The old engine had no render abstraction: the raw D3D9 device was passed into every game object's `OnFrameRender`, which issued its own fixed-function state and draw calls (~71 device-API sites in RCSim alone, 127 RenderState + 85 TextureState sites overall). We decided the new renderer is a retained scene graph: game objects own Scene Nodes (mesh + Material + transform, parented hierarchically) and update them; only the renderer records DX12 command lists. A lighter immediate-mode submission API was considered and rejected in favor of the cleaner long-term shape, which also serves the editors' add/remove/inspect workloads best.

## Consequences

- Every game object's render callback is rewritten as node ownership/updates; dynamic content (smoke, thermals, water ripples, flag cloth) becomes dynamic-buffer nodes.
- The renderer is host-agnostic — it accepts an HWND and size, never referencing any UI framework. The sim supplies a raw Win32 window; the editors supply WinForms panel handles.
- Fixed-function material state collapses into a small closed set of Materials/PSOs (lit-textured, transparent, terrain-splat, water, flag, sky, etc.).
