# World rendering: terrain, sky, Photo Scenery, static objects

Status: ready-for-agent

## What to build

Render a complete flyable world from existing Scenery content: heightmap terrain with the texture-splatting Material (ported from the splat effect to HLSL SM6), sky dome with the time-of-day sky textures, Photo Scenery (panoramic photo box with depth), and static scenery objects (trees as billboards/cross-quads, windmills, buildings, gates) placed from the Scenery `.par` definitions.

Both stock sceneries (the default 3D scenery and the Photo Scenery) should render recognizably compared to the old build.

## Acceptance criteria

- [ ] The default Scenery renders: splatted terrain, sky dome, vegetation, and placed objects, from unmodified `.par`/terrain definition files
- [ ] The Photo Scenery renders with correct panorama orientation and depth occlusion
- [ ] A free camera can fly around the world at interactive framerates
- [ ] Terrain height queries work (needed later for ground contact/collision)

## Blocked by

- 07
