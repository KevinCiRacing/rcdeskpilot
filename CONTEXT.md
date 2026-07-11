# RC Desk Pilot

An R/C flight simulator for Windows: realistic radio-controlled aircraft flight over photo or 3D scenery, flown with a USB transmitter. A custom C# engine (Bonsai) underneath, being modernized from Managed DirectX 9 to a DirectX 12 renderer on modern .NET.

## Language

### Structure

**Bonsai**:
The general-purpose game engine library — device/window lifecycle, scene rendering, input, sound, and the in-game UI toolkit. Knows nothing about aircraft.
_Avoid_: framework (that's one class inside it), engine core

**Sim**:
The RC Desk Pilot game itself (the RCSim project) — flight models, aircraft, scenery, weather, gameplay. Consumes Bonsai.
_Avoid_: game, app

**Editors**:
The two content-creation tools — Aircraft Editor and Scenery Editor — which host the same Bonsai renderer and share Sim source files.

**Plugin API**:
The RCDeskPilot.API surface that exposes flight telemetry to external applications. Currently parked (kept in-tree, not built) during the conversion.

### Flight domain

**Flight Model**:
The physics simulation that turns control inputs plus wind into aircraft motion. Implementations of `IFlightModel`.
_Avoid_: physics engine

**Aircraft Parameters**:
The serialized definition of an aircraft's geometry, aerodynamics, engine, and model files — stored in a `.par` file.
_Avoid_: aircraft config, plane definition

**Transmitter**:
The USB R/C radio (or dongle-connected real radio) used to fly — a generic HID joystick with 4–8 axes. The primary input device.
_Avoid_: controller, gamepad, joystick (reserve "joystick" for the generic HID device class)

**Scenery**:
A flyable environment — terrain, sky, objects, and lighting — defined by a `.par` file plus textures/heightmaps. Photo Scenery is the panoramic-photo variant.

### Rendering

**Scene Graph**:
The retained hierarchy of Scene Nodes the renderer traverses each frame. Game objects own nodes and update them; they do not issue draw calls.

**Scene Node**:
A single renderable (or grouping) entry in the Scene Graph: mesh + material + transform, parented for hierarchical motion (e.g. aileron under wing under aircraft).
_Avoid_: renderable, render object

**Material**:
The description of how a surface is shaded — shader selection plus its textures and parameters. Replaces fixed-function render/texture state.
