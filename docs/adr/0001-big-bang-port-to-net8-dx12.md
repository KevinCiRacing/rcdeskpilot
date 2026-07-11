# Big-bang port to modern .NET + DirectX 12 via Vortice

The codebase targeted .NET Framework 2.0 x86 on Managed DirectX 9 — a dead 2005-era API that will never run on modern .NET, making an incremental runtime upgrade impossible without keeping MDX9 alive. We decided to retarget everything to .NET 8+ in one move and write the replacement renderer directly against DirectX 12 using Vortice.Windows bindings, with no interim .NET Framework 4.8 step and no multi-backend render abstraction (DX11 was considered and rejected: the abstraction tax plus a likely second migration outweighed DX12's higher up-front complexity, and DX12 was explicitly desired).

## Consequences

- The sim does not run from the retarget until the DX12 renderer, Vortice.DirectInput input, and Vortice.XAudio2 audio all land. The last MDX9-working commit is the behavioral reference (git history), backed by flight-characterization recordings captured before the bang.
- Math types migrate to System.Numerics across engine *and* flight models — the MDX types are load-bearing in the physics (42 of 62 RCSim files).
- DirectInput remains the input *protocol* (via Vortice) because USB R/C transmitters are generic HID joysticks; XInput/Windows.Gaming.Input cannot enumerate them.
