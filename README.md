# RogueMode Combat Tracker

**Current public version: v0.9.0.3 Beta**

RogueMode Combat Tracker (RMCT) is a real-time combat analysis tool for **Palworld**. It separates and attributes combat damage from players, Pals, supported skills, weapons, status effects, and other tracked combat sources, then displays the results in a standalone Windows desktop application.

This repository contains the source corresponding to the **v0.9.0.3 Beta** release.

## Repository contents

- `RogueModeDpsMeter/` — WPF/.NET source for the RMCT desktop application.
- `UE4SS/RogueModeTelemetry/` — UE4SS Lua component that reads Palworld combat events and writes the local RMCT combat feed.
- `BUILD.md` — build and publish instructions.
- `SECURITY.md` — local data-flow and security-review notes.
- `THIRD_PARTY_NOTICES.md` — third-party framework/dependency notes.

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
   | local combat feed
   v
RogueModeTelemetry.txt
   |
   v
RogueMode Combat Tracker
```

The UE4SS component writes combat records to a local text file. The desktop application reads that file and performs encounter tracking, source attribution, history, comparison, diagnostics, and UI rendering.

## Main features

- Real-time Player and Pal DPS tracking
- Team damage totals
- Multiplayer ownership grouping
- Raid Team grouping for supported raid targets
- Weapon and damage-source classification
- Pal skill attribution
- Supported status-damage attribution
- Supported Partner Skill attribution
- Live target HP
- Automatic encounter handling
- Safety Stop same-target encounter resume
- Encounter history and organization tools
- Encounter comparison UI
- Local Combat Feed diagnostics
- Multiple UI themes

### Status attribution validation

The v0.9.0.3 Beta status-attribution architecture is generic for damaging status effects that use the supported runtime path. **Burn has been live-validated.** Additional status types continue to be tested.

## Runtime requirements

- Windows x64
- Palworld
- A Palworld-compatible UE4SS installation

UE4SS itself is not included in this repository.

## Installation

### Desktop application

Use the published RMCT application package from Nexus Mods or the GitHub Releases page. Extract the complete application folder and run:

```text
RogueModeDpsMeter.exe
```

Keep the application files together. The desktop application does not need to be placed inside the Palworld directory.

### RogueModeTelemetry

Install the `RogueModeTelemetry` folder into:

```text
Pal\Binaries\Win64\ue4ss\Mods\
```

Expected structure:

```text
Pal\Binaries\Win64\ue4ss\Mods\RogueModeTelemetry\enabled.txt
Pal\Binaries\Win64\ue4ss\Mods\RogueModeTelemetry\Scripts\main.lua
```

The telemetry component writes its combat feed to:

```text
Pal\Binaries\Win64\ue4ss\RogueModeTelemetry.txt
```

Recommended startup order:

1. Start Palworld.
2. Load into a world.
3. Start RogueMode Combat Tracker.
4. Enter combat.

## Build

See [`BUILD.md`](BUILD.md).

## Security / privacy

See [`SECURITY.md`](SECURITY.md).

## Source-use notice

This repository is published for transparency, security review, and build verification. No separate open-source license is granted by this repository. Third-party components remain subject to their respective licenses.
