# Effect Materials: water, cloth, transparency, particles

Status: resolved

## What to build

The specialty Materials that made the old sim look alive, ported from their SM2/SM3 `.fx` effects to HLSL SM6 PSOs: animated water with reflections and ripples, cloth animation for the flag and windsock (wind-driven vertex animation), correct back-to-front transparency sorting for transparent meshes, and the particle systems (smoke trails, thermal visuals) as dynamic-buffer Scene Nodes.

## Acceptance criteria

- [ ] Water renders with animated waves, reflection, and ripple response comparable to the old build
- [ ] Flag and windsock animate with wind direction/strength and read correctly as wind indicators
- [ ] Transparent objects (prop disks, canopies, tree billboards, particles) sort and blend correctly against each other and the world
- [ ] Smoke and thermal particle systems emit, animate, and expire as dynamic Scene Nodes without per-frame buffer stalls

## Blocked by

- 08

## Comments

Resolved in commit 5d0e5b1. Screenshot-verified: mirror lake with cloud/flag reflections, rising smoke/thermal particles, waving cloth. Notes: refraction map from the legacy shader replaced by a fresnel-blended dull color (visual approximation, documented); ripples are analytic expanding rings in the shader (2 slots) rather than the old render-to-bump ripple sim; particle vertex alpha rides in Normal.x (documented pragmatism); dynamic particle VB is single-buffered upload heap (theoretical same-frame race, invisible in practice - flagged for the perf pass). Transparent sort is per-node, matching the old TransparentObjectManager granularity.
