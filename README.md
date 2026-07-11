# rcdeskpilot

R/C Desk Pilot is an R/C flight simulator for Windows. You can get the original at http://rcdeskpilot.com.

This fork is modernized to **.NET 10 / DirectX 12**: the original Managed DirectX 9.0c engine has been replaced by a new renderer (Vortice.Windows D3D12), System.Numerics math, XAudio2 audio, DirectInput transmitter input, runtime Assimp import of the existing `.X` content, and Dear ImGui UI. All existing aircraft and scenery content — including community content — keeps working unchanged. The plan of record lives in `docs/adr/`; the conversion history in `.scratch/dx12-conversion/`.

## Building and running

Requires the .NET 10 SDK on Windows (x64, DirectX 12 capable GPU).

```
dotnet build RCDeskPilot.sln
dotnet run --project RCSim                    # the Sim
dotnet run --project RCSim.AircraftEditor     # Aircraft Editor
dotnet run --project RCSim.SceneryEditor      # Scenery Editor
```

In the Sim: pick an aircraft and scenery from the menu and fly with a USB transmitter (mapped in Settings → Controls) or the keyboard (arrows/numpad, PageUp/PageDown throttle). ESC returns to the menu. In flight: Weather panel, Game panel (pylon racing, scarecrow, bombing), flight recorder, S toggles smoke, T tows (gliders), R resets after a crash.

## Verification

Automated suites (run from the repo root):

```
dotnet run --project RCSim -- --gametest                       # end-to-end game flow
dotnet run --project Bonsai.Graphics.TestHost -- --contenttest # every .par loads
cd RCSim.Characterization && dotnet run -- verify ..\RCSim\Aircraft recordings
```

plus per-subsystem selftests in `Bonsai.Graphics.TestHost` (`--selftest`, `--scenetest`, `--scenerytest`, `--phototest`, `--effectstest`, `--menutest`, `--inputtest`, `--audiotest`, `--flytest`) and `--selftest` in both editors.

## Status notes

- **Plugin API**: parked. The `RCDeskPilot.API` assembly still builds (the flight-model plumbing compiles against its types) but the external-application compatibility promise and the sample are suspended until after the conversion settles.
- The legacy MDX9-era sources remain in-tree for reference but are no longer compiled; the Bonsai (legacy) and RCSim.Common projects were removed from the solution.

You can contact the original author at info@rcdeskpilot.com.
