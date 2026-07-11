# Content smoke tests: every .par loads

Status: ready-for-agent

## What to build

An automated test suite that loads every Aircraft Parameters and Scenery `.par` file in the repo through the new asset pipeline — parameters parse, every referenced mesh imports via Assimp, every referenced texture and sound loads — and fails with a clear per-asset message on any breakage. This guards the ADR 0004 compatibility promise (all existing and community `.X` content keeps working) and runs headlessly so it can gate future changes.

## Acceptance criteria

- [ ] Every stock aircraft and scenery `.par` in the repo passes: parameters, meshes, textures, sounds all load
- [ ] Failures identify the exact asset and reason (file, referenced-by, error)
- [ ] Suite runs headless (no window/GPU swapchain required, or uses an offscreen device) in CI-friendly time
- [ ] Documented one-command way to run it locally

## Blocked by

- 07, 08
