# RogueMode Combat Tracker

RogueMode Combat Tracker (RMCT) is a real-time combat analysis tool for **Palworld**. It separates and attributes combat damage from players, Pals, skills, supported status effects, and other tracked combat sources, then presents the results in a standalone Windows desktop application.

This repository contains the source corresponding to the **v0.9.0 Beta** Nexus release of the combat-only tracker.

## Repository contents

- `RogueModeDpsMeter/` — WPF/.NET source for the desktop Combat Tracker.
- `UE4SS/RogueModeTelemetry/` — UE4SS Lua component used to read combat events from Palworld and write the local combat feed.
- `BUILD.md` — reproducible build and publish instructions.
- `SECURITY.md` — data-flow and privacy notes for security review.
- `THIRD_PARTY_NOTICES.md` — third-party dependency information.

The experimental RogueMode Character Analyzer is intentionally **not** part of this repository or release.

## How it works

```text
Palworld
   |
   v
UE4SS
   |
   v
RogueModeTelemetry / main.lua
   |
   | local file only
   v
RogueModeTelemetry.txt
   |
   v
RogueMode Combat Tracker
```

The UE4SS component writes combat records to a local text file. The desktop application reads that file and performs encounter tracking, source attribution, history, comparison, and UI rendering.

RMCT does not require an account or remote service. See `SECURITY.md` for more detail.

## Main features

- Real-time Player and Pal DPS tracking
- Team damage totals
- Pal skill attribution
- Supported Burn/Poison status attribution
- Weapon and damage-source classification
- Encounter start/end handling
- Encounter history
- Encounter comparison
- Local Combat Feed diagnostics
- Multiple UI themes
- Server/world-travel runtime-state reset protections

## Runtime requirements

- Windows 10/11 x64
- Palworld (Steam/Windows)
- A Palworld-compatible UE4SS installation

UE4SS itself is **not included** in this repository.

## Build

See [`BUILD.md`](BUILD.md).

## Version

Public release: **v0.9.0 Beta**

The production UE4SS Lua contains an internal RC12.4d marker for support/debug identification of the server-travel stability revision used by this release.

## Source-use notice

This repository is published for transparency, security review, and build verification. No separate open-source license is granted by this repository. Third-party components remain subject to their respective licenses.
