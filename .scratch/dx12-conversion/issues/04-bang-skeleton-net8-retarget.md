# Bang skeleton: .NET 8 retarget, dependencies, integration branch

Status: claimed

## What to build

Open the big-bang integration branch (all issues 05–17 land on it; green is only promised at issue 17). Convert every active project to SDK-style csproj targeting `net8.0-windows`; remove all Managed DirectX assembly references; add the new dependency set (Vortice.Windows D3D12/DXGI/DirectInput/XAudio2, AssimpNet or equivalent, Hexa.NET.ImGui); park the Plugin API and its sample (kept in-tree, excluded from the solution build).

Code that no longer compiles is expected — mark broken subsystems clearly (e.g. conditional exclusion) rather than deleting them, so subsequent issues can port file-by-file. The deliverable is the new project skeleton, not working code.

## Acceptance criteria

- [ ] All active projects are SDK-style, `net8.0-windows`, x64, with no Microsoft.DirectX references anywhere
- [ ] New dependencies restore from NuGet; the solution's dependency graph is documented in the PRD or branch README
- [ ] Plugin API + Sample are excluded from the solution build but remain in-tree
- [ ] `dotnet build` runs and reports errors only in code awaiting porting (the skeleton itself — props, targets, references — introduces no errors)

## Blocked by

- 01, 02, 03
