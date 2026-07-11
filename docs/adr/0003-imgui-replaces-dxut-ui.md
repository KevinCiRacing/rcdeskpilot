# Dear ImGui replaces the DXUT-style UI toolkit

All in-game UI (menus, dialogs, HUD) was rendered by a DXUT-derived control toolkit inside Bonsai (~5,000+ lines: Dialog, Button, ComboBox, Slider, etc.) drawing via D3DX Sprite/Font, which have no DX12 equivalent. Rather than porting the toolkit's render layer (considered — it would have preserved the ~15 existing sim dialogs unchanged), we decided to delete the toolkit entirely and rewrite all game and editor UI as Dear ImGui panels (Hexa.NET.ImGui, DX12 backend).

## Consequences

- All ~15 RCSim dialog classes are rewritten by hand, and the player-facing menu look changes.
- One UI stack serves the sim, the editors' tool panels, and future debug overlays.
- `Bonsai/Core/Controls/*`, `DialogResourceManager`, the DX settings dialog, and the `uicontrols.dds` atlas are deleted rather than ported.
