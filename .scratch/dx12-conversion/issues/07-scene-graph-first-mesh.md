# Scene Graph + first mesh: Assimp-loaded aircraft on screen

Status: resolved

## What to build

The retained Scene Graph core (ADR 0002) and the asset pipeline (ADR 0004), proven by rendering a real aircraft model: Scene Nodes with hierarchical transforms (parented parts — e.g. aileron under wing under aircraft), a lit-textured Material/PSO with a directional light, a camera, and mesh/texture loading through Assimp from the repo's existing `.X` files with their referenced textures.

Game objects own and update nodes; only the renderer walks the graph and records command lists. Include node add/remove, visibility flags, and a texture loader covering the formats the content uses (PNG/JPG/BMP/DDS).

## Acceptance criteria

- [ ] A stock aircraft's `.X` part files load via Assimp and render textured and lit, with correct part hierarchy (control surfaces move when their node transforms are animated programmatically)
- [ ] Scene Nodes support parenting, per-node world transforms, visibility, and dynamic add/remove without renderer stalls
- [ ] DDS (incl. compressed) and PNG/JPG/BMP textures load
- [ ] No game-object code records draw commands or touches the device

## Blocked by

- 06

## Comments

Resolved in commit 198f861. All four acceptance criteria verified by the TestHost scenetest (screenshots reviewed: textured, lit Extra 300 with correct part placement; control-surface deflection visible between captures). Notes: stock content ships only uncompressed DDS, so the BC path is validated with a synthetic DXT1 texture; meshes use upload-heap buffers for now (default-heap + staging is a perf refinement for later); mip chains not yet uploaded (level 0 only) - revisit with the world renderer (issue 08) where minification matters.
