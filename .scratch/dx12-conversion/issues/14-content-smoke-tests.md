# Content smoke tests: every .par loads

Status: resolved

## What to build

An automated test suite that loads every Aircraft Parameters and Scenery `.par` file in the repo through the new asset pipeline — parameters parse, every referenced mesh imports via Assimp, every referenced texture and sound loads — and fails with a clear per-asset message on any breakage. This guards the ADR 0004 compatibility promise (all existing and community `.X` content keeps working) and runs headlessly so it can gate future changes.

## Acceptance criteria

- [ ] Every stock aircraft and scenery `.par` in the repo passes: parameters, meshes, textures, sounds all load
- [ ] Failures identify the exact asset and reason (file, referenced-by, error)
- [ ] Suite runs headless (no window/GPU swapchain required, or uses an offscreen device) in CI-friendly time
- [ ] Documented one-command way to run it locally

## Blocked by

- 07, 08

## Comments

Resolved. `ContentSmokeTest.cs` in Bonsai.Graphics.TestHost, dispatched via `--contenttest`; runs on a new headless GraphicsDevice mode (device+queue+fence, no window/swapchain) so real GPU uploads are exercised windowless. Coverage: 17 aircraft .par + 2 scenery .par -> 162 meshes (Assimp), 145 textures (DX12 upload), 16 sounds (XAudio2), 0 debug-layer errors, PASS. Failures print asset + referencing .par + reason; references the legacy engine tolerated when absent (control surfaces, icons, sounds, placed scenery objects) are reported as skips — the only one is the intentionally removed `data/ad1.x` ad billboard. One-command run: `dotnet run --project Bonsai.Graphics.TestHost -- --contenttest` (exit code 0/1).
