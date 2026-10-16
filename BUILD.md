# Build notes

Builds a `CelesteFit.zip` that drops straight into `Mods/`. Game assemblies are **not** in this repository; you supply them from an installation you own.

## Requirements

- .NET SDK 8.0 or newer (the mod itself targets `net472`).
- Celeste 1.4.0.0 **FNA** with Everest installed — that is where `Celeste.Mod.mm.dll` and the MonoMod assemblies come from.

The mod targets `net472` on purpose. Everest runs on Wine-Mono on some handhelds, and Wine-Mono cannot load a `net8.0` mod assembly, so the output has to be a .NET Framework 4.7.2 assembly even though it is built with a modern SDK.

## 1. Collect the reference assemblies

```bash
scripts/fetch-libs.sh                      # finds Steam's Celeste folder
scripts/fetch-libs.sh /path/to/Celeste     # or point at it
```

That copies this set into the git-ignored `libs/`:

```text
Celeste.dll            Celeste.Mod.mm.dll     FNA.dll
MonoMod.Utils.dll      MonoMod.RuntimeDetour.dll   Mono.Cecil.dll
```

Two of them are easy to get wrong:

- `Celeste.dll` must be the **pristine game assembly**, not an Everest-merged one. A merged assembly exposes a newer API surface and the mod will fail to load at runtime with a `MissingMethodException` rather than a compile error.
- `Celeste.Mod.mm.dll` is the Everest assembly and must come from the same Everest build as the game assembly. It is a separate reference, not bundled into `Celeste.dll`.

Everything in `libs/` is git-ignored. Commit nothing from it: Celeste's assemblies are proprietary and the rest are third-party binaries.

## 2. Build and package

```bash
scripts/build.sh
```

This runs `dotnet build Source/CelesteFit.csproj -c Release` (output to `bin/`) and writes `dist/CelesteFit.zip` containing `everest.yaml` and `bin/CelesteFit.dll`.

To build against a game folder somewhere else, without copying anything:

```bash
dotnet build Source/CelesteFit.csproj -c Release -p:CelestePrefix=/path/to/Celeste
```

## 3. Install

Copy `dist/CelesteFit.zip` into `Celeste/Mods/` and start the game. The mod log is in `log.txt` next to the executable; the mod logs `[CelesteFit]` lines, one per phase, plus one `room camera:` line per room.

Starting a room should report `= 240px at zoom 1.0` (at 1280×960 output). If it reports 180, the camera viewport patch did not apply and tiles will be culled below a fixed line in the world.
