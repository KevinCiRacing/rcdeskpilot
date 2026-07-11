# Delete dropped features: in-scenery ads and anaglyph stereo

Status: resolved

## What to build

Remove the two features decided as dropped (see ADR 0001 context and the grilling record): the in-scenery ad system (AdManager and its download/billboard path) and the anaglyph red/cyan stereo-3D render path (including its compile-time define and post-effect shader). Remove associated assets and settings entries so no dead toggles remain in the UI or config.

## Acceptance criteria

- [ ] AdManager and all its call sites, assets, and settings are gone
- [ ] The anaglyph render path, its `ANAGLYPH` define, effect file, and any settings/UI toggles are gone
- [ ] All projects still build and the sim still runs, renders, and flies as before
- [ ] No orphaned assets or settings keys remain from either feature

## Blocked by

None - can start immediately

## Comments

Resolved in commit 0582e65 (1,106 lines removed). AdManager + Downloader + ApplyAds paths + ad assets gone; anaglyph render path, CameraBase per-eye methods, GraphicsDialog checkbox, anaglyph.fx, and ANAGLYPH defines gone. Solution rebuilds clean; sim launch-verified after removal.
