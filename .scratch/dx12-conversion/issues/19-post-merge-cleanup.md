# Post-merge cleanup and deferred features

Status: migrated to GitHub Issues — https://github.com/KevinCiRacing/rcdeskpilot/issues/2

## What to build

Follow-ups deferred at the issue 17 merge gate:

- Consume the persisted graphics detail settings (SceneryDetail, WaterDetail, WaterRipplesDetail, ReflectionDetail) in the renderer; currently only SmokeDetail and LensFlare are honored.
- Water in the default field at the `waterstartposition` (Water material and reflections exist since issue 09; the flight world doesn't place a lake yet) including float/water physics (Water stub always reports dry).
- Control expo settings (RollExpo/PitchExpo/YawExpo) and the aileron-for-rudder fallback for <4-channel aircraft (legacy Player behavior).
- Welcome dialog / first-run flow.
- Delete the no-longer-compiled legacy MDX9 sources (Bonsai\*, RCSim legacy .cs, RCSim.Common, RCDeskPilot.API.Sample) once nothing links against files in those trees, or fold the linked survivors (Utility, Heightmap, FrameworkTimer, NativeMethods) into Bonsai.Graphics.
- ScareCrow/Tractor wheel shadows and thermal visuals (ThermalVisual) if wanted for parity.

## Blocked by

- 17
