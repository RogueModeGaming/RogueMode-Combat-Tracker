# Building RogueMode Combat Tracker v0.9.0 Beta

These instructions describe how to build the Windows application from the source in this repository and how to install the UE4SS component.

## Requirements

- Windows 10 or Windows 11 x64
- Visual Studio with the **.NET desktop development** workload
- .NET 10 SDK

The application targets:

```text
net10.0-windows
win-x64
```

## Option A — Visual Studio

1. Clone or download this repository.
2. Open:

   ```text
   RogueModeDpsMeter/RogueModeDpsMeter.csproj
   ```

3. Select the `Release` configuration.
4. Build the project, or use **Publish** with the included `NexusRelease` folder profile.
5. Publish for `win-x64` as **self-contained**.

The included project configuration sets the public version metadata to:

```text
Version:              0.9.0
AssemblyVersion:      0.9.0.0
FileVersion:          0.9.0.0
InformationalVersion: 0.9.0-beta
```

Release builds disable PDB/debug-symbol generation.

## Option B — Command line

From the repository root, run on Windows:

```powershell
dotnet publish .\RogueModeDpsMeter\RogueModeDpsMeter.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true
```

The default command-line publish output is:

RogueModeDpsMeter\bin\Release\net10.0-windows\win-x64\publish\

## UE4SS component

The source for the runtime component is:

```text
UE4SS/RogueModeTelemetry/Scripts/main.lua
```

To install it in Palworld, copy the `RogueModeTelemetry` folder to:

```text
Palworld/Pal/Binaries/Win64/ue4ss/Mods/
```

The resulting path should be:

```text
Palworld/Pal/Binaries/Win64/ue4ss/Mods/RogueModeTelemetry/Scripts/main.lua
```

`enabled.txt` is included so UE4SS can load the mod.

## Running

1. Start Palworld with UE4SS and `RogueModeTelemetry` enabled.
2. Start the published RogueMode Combat Tracker application.
3. Enter combat and deal damage.
4. The UE4SS Lua writes the local `RogueModeTelemetry.txt` feed and the desktop tracker reads it.

## Notes for review

- Character Analyzer source/probes are intentionally excluded from this combat-only release.
- The Combat Tracker does not require the experimental character-stat database.
- UE4SS is a separate dependency and is not redistributed here.
