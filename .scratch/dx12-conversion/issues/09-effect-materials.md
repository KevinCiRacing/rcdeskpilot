# Effect Materials: water, cloth, transparency, particles

Status: claimed

## What to build

The specialty Materials that made the old sim look alive, ported from their SM2/SM3 `.fx` effects to HLSL SM6 PSOs: animated water with reflections and ripples, cloth animation for the flag and windsock (wind-driven vertex animation), correct back-to-front transparency sorting for transparent meshes, and the particle systems (smoke trails, thermal visuals) as dynamic-buffer Scene Nodes.

## Acceptance criteria

- [ ] Water renders with animated waves, reflection, and ripple response comparable to the old build
- [ ] Flag and windsock animate with wind direction/strength and read correctly as wind indicators
- [ ] Transparent objects (prop disks, canopies, tree billboards, particles) sort and blend correctly against each other and the world
- [ ] Smoke and thermal particle systems emit, animate, and expire as dynamic Scene Nodes without per-frame buffer stalls

## Blocked by

- 08
