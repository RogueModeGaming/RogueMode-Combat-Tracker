# Building RogueMode Combat Tracker

These instructions describe the desktop application source included in this repository for **v0.9.0.3 Beta**.

## Requirements

- Windows x64
- Visual Studio with .NET desktop development support, or a compatible .NET SDK
- .NET 10 SDK capable of building `net10.0-windows`

## Project

```text
RogueModeDpsMeter\RogueModeDpsMeter.csproj
```

## Version metadata

The project file for this source snapshot uses:

```text
Version:              0.9.0.3
AssemblyVersion:      0.9.0.3
FileVersion:          0.9.0.3
InformationalVersion: 0.9.0.3-beta
```

## Visual Studio build

1. Open `RogueModeDpsMeter.csproj` in Visual Studio.
2. Select **Release** configuration.
3. Build the project.

## Self-contained Windows x64 publish

From a terminal at the repository root:

```powershell
dotnet publish .\RogueModeDpsMeter\RogueModeDpsMeter.csproj -c Release -r win-x64 --self-contained true
```

Typical output is under a path similar to:

```text
RogueModeDpsMeter\bin\Release\net10.0-windows\win-x64\publish\
```

The exact path may vary with SDK or publish-profile settings.

## Distribution

Distribute the **complete publish output**, not only `RogueModeDpsMeter.exe`. A self-contained WPF publish includes the application and required .NET runtime/framework files.

Do not include development-only files such as:

```text
.vs\
bin\
obj\
*.user
*.pubxml.user
*.pdb
```

unless they are intentionally required for a specific debugging release.

## UE4SS component

`UE4SS/RogueModeTelemetry` is a separate runtime component and is not built by the .NET project. Install it under the Palworld UE4SS `Mods` directory as described in `README.md`.
