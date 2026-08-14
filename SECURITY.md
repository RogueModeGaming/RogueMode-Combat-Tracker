# Security and Data-Flow Notes

This document describes the v0.9.0.3 Beta source in this repository for transparency and security review.

## Local runtime data flow

RMCT uses two components:

1. `RogueModeTelemetry` runs through UE4SS inside Palworld.
2. The RMCT desktop application reads the local telemetry file produced by that component.

The expected combat-feed path is:

```text
Pal\Binaries\Win64\ue4ss\RogueModeTelemetry.txt
```

The desktop application uses this local data for combat attribution, encounter handling, history, comparison, UI rendering, and diagnostics.

## Network behavior

The application source in this repository does not implement a built-in updater, downloader, HTTP client, remote analytics client, account/login system, or cloud telemetry service.

The `Process.Start` uses in the desktop source open local folders through the Windows shell for telemetry/diagnostic access.

## Local data

Runtime and diagnostic data can contain combat-related information such as:

- Player display names
- Pal names
- Target names
- Damage amounts
- Skill/weapon/source information
- Combat timing
- Runtime actor identifiers
- Local file/path information in private diagnostic output

Public-support diagnostic generation includes privacy/sanitization logic; private/developer diagnostics may intentionally retain more detail for troubleshooting.

## Game modification scope

RMCT is intended as an analysis tool. The tracker source reads and processes combat information; it is not designed to alter character stats, inventory, Pals, save files, damage values, enemy health values, or game balance.

## UE4SS

UE4SS is required for the Palworld-side Lua component but is not included in this source repository. UE4SS is a separate third-party project with its own code, distribution, and security considerations.

## Reporting a security issue

If you identify a security problem in RMCT, report the affected version, the relevant file/component, and steps required to reproduce the issue. Avoid posting private diagnostic data publicly.
