# Repo hygiene: solution file, de-dupe, prune orphans

Status: resolved

## What to build

Basic repository hygiene on the working MDX9 build: add a solution file covering all buildable projects; eliminate the byte-identical duplicated WindSock source between the Sim and the Scenery Editor (replace the copy with a shared compile link, matching how the Editors already link other Sim sources); and remove files that exist on disk but are not referenced by any project.

## Acceptance criteria

- [ ] A `.sln` at the repo root builds all active projects in one `msbuild` invocation
- [ ] Exactly one WindSock source file remains; the Scenery Editor compiles it via a link, and both executables still build
- [ ] Orphaned files (on disk, in no csproj) are identified and deleted — or explicitly kept with a stated reason
- [ ] All projects still build and the sim still runs

## Blocked by

None - can start immediately

## Comments

Resolved in commit 70554f2. Orphans found and deleted: AircraftEditor MainForm.cs/.Designer.cs, SceneryEditor Terrain.cs (dead experiment using LINQ, never in csproj) and TerrainDefinition_backup.cs. WindSock.cs verified byte-identical before de-dupe; SceneryEditor now compile-links RCSim's copy. Full solution builds clean (7/7 projects).
