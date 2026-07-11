# Runtime Assimp import instead of converting .X assets to glTF

Every model is a DirectX `.X` file (including one skinned/animated path), loaded via D3DX which no longer exists, and aircraft/scenery `.par` files reference the `.X` parts by filename — as does years of community-made content. We decided to import meshes at runtime through Assimp (which parses `.X` natively) rather than batch-converting the repo's assets to glTF, because conversion would break `.par` references and orphan all community content, while runtime Assimp keeps everything loading unchanged and gives glTF/OBJ support for new content free.

## Consequences

- Assimp becomes a native runtime dependency; GPU buffers are built from its scene output, and skinning is rebuilt from its bone data rather than D3DX's AnimationController.
- Content smoke tests must load every `.par` in the repo through the new pipeline to guard the compatibility promise.
