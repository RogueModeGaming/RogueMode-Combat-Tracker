# Security and privacy notes

This document describes the data flow of RogueMode Combat Tracker v0.9.0 Beta for review purposes.

## Local data flow

The UE4SS Lua component reads Palworld runtime combat information and writes a local file named:

```text
RogueModeTelemetry.txt
```

The WPF desktop application reads this local feed to calculate and display combat information.

The feed can contain gameplay-related values such as:

- player display names encountered in tracked combat
- Pal names
- target names
- damage values
- skill/action identifiers
- weapon/source identifiers
- encounter timing information

## Network behavior

RMCT does not implement remote analytics or gameplay-data uploading.

The v0.9.0 Beta source contains no HTTP client, socket client, webhook, updater, downloader, or remote telemetry service used to transmit the combat feed.

The application does use `Process.Start(..., UseShellExecute = true)` in two UI actions solely to open a local folder in the Windows shell:

- the local combat-feed folder
- the local diagnostics folder

It does not use those calls to launch command shells or scripts.

## Local files

The application can create/read local files for normal functionality, including:

- combat feed consumption
- encounter-history storage
- theme preference storage
- user-requested diagnostic bundles

Diagnostic-generation code contains privacy filtering intended to remove common local Windows user-path information from public support bundles.

## UE4SS

UE4SS is a separate runtime dependency and is not included in this repository.
