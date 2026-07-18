# Deployment layout and publish profile

Status: migrated to GitHub Issues — https://github.com/KevinCiRacing/rcdeskpilot/issues/1

## What to build

RCSim currently locates content by walking up to the repo root (RCSim\Aircraft, RCSim\data). Define the installed layout (content beside the executable), make GameShell's content paths root-relative rather than repo-shaped, and add a `dotnet publish` profile that produces a runnable, self-contained distribution incl. demo.dat and frameworkconfig defaults. Community content locations (My Documents\RC Desk Pilot\Aircraft, as the legacy editor used) should be honored by the pickers.

## Blocked by

- 17
