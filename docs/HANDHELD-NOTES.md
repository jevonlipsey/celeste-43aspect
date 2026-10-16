# Handheld and Proton ARM notes

> These notes are kept here because they explain why this mod's hooks look the way they do. The general version, for any Celeste mod on this stack, lives in [celeste-proton-arm64](https://github.com/jevonlipsey/celeste-proton-arm64) — start there if Everest itself will not boot.

CelesteFit was developed against a Windows Celeste install running under Steam + Proton on an ARM64 Android handheld (DroidDeck-style device, Adreno 740, FEX/box64 translation, Wine-Mono runtime). These are the things that bit us there; none of them are CelesteFit bugs, but all of them look like one from the outside.

## Requirements on the device

| Thing | Value | Why |
| --- | --- | --- |
| Game branch | FNA (`beta-1.3.3.0` on Steam, or the itch FNA build) | the XNA branch is a different framework and the mod targets FNA types |
| Loader | Everest 1.4.x (tested 4449) | hooks and `ModModule` API |
| Mod build | `net472` | Wine-Mono cannot load a `net8.0` assembly, so a modern SDK still has to emit .NET Framework 4.7.2 |

## Keep the MonoMod set version-matched

Everest's first launch patches the game with MonoMod, which is a *library set*, not one file: `MonoMod.exe`, `MonoMod.Patcher.dll`, `MonoMod.Core.dll`, `MonoMod.Utils.dll`, `MonoMod.RuntimeDetour.dll`, `Mono.Cecil.dll` and `Mono.Cecil.Pdb.dll`.

Those files must all come from **one** Everest build. Mixing builds (for example taking `Mono.Cecil.dll` from one release and `Mono.Cecil.Pdb.dll` from another) makes MonoMod fail while loading, which on this device looks like a game that never gets past launch: no crash dialog, no `[Everest] Booting` line in `log.txt`, and if you watch the process it sits at ~0% CPU.

Worth knowing: the same release ships *different binaries per platform*, so byte sizes differ legitimately. The macOS 4449 build's `Mono.Cecil.Pdb.dll` is 89600 bytes; the Windows 4449 build's is 88064. Match by build, not by file size, and never cross-copy between a Mac and a Windows install.

The clean fix is always to re-extract the whole set for that build (Everest's own update path, or a fresh install over the game folder) rather than hand-swapping one file. When a swap is the only way forward, move the two files that carry the PDB path together — `MonoMod.exe` and `Mono.Cecil.Pdb.dll` — and keep them from the same build as everything else.

### Signature: InvalidProgramException in a DMD wrapper

This is the one that sent us down the PDB path, and it is worth recognising on sight:

```text
System.InvalidProgramException: Invalid IL code in [wrapper dynamic-method]
Celeste.ScreenWipe:DMD<Celeste.ScreenWipe::get_Bottom> (Celeste.ScreenWipe): IL_000f: ret

  at [wrapper managed-to-native] System.RuntimeMethodHandle.GetFunctionPointer(intptr)
  at MonoMod.RuntimeDetour.Platforms.DetourRuntimeILPlatform.GetFunctionPointer(MethodBase, RuntimeMethodHandle)
  at MonoMod.RuntimeDetour.Platforms.DetourRuntimeILPlatform.GetNativeStart(MethodBase)
  at MonoMod.RuntimeDetour.Detour._TopApply()
  at MonoMod.RuntimeDetour.ILHook..ctor(MethodBase, ILContext+Manipulator, ILHookConfig)
```

Read it bottom-up. An `ILHook` was applied to a trivial getter (`get_Bottom` is `ldarg.0; ldfld; ret`), MonoMod built the dynamic wrapper for it by copying that original body out of the assembly through Cecil, and the CLR rejected the generated wrapper. The exception surfaces at `GetFunctionPointer`, which is nowhere near the cause: the cause is the Cecil/PDB pair in the install not matching each other, so the body was read as garbage.

Two consequences that shaped this mod:

- the Cecil/PDB pair has to match the build, which is why installing the mismatched `MonoMod.exe` + `Mono.Cecil.Pdb.dll` was the fix rather than a reinstall ritual; and
- a single unhookable method must not take the whole mod down, so `HookMethod` in `CelesteFitModule.cs` catches and logs instead of throwing. When you see a `[warn]` in `log.txt` naming a method, this is almost always why.

### Signature: MissingMethodException in the mod's own Load

```text
System.MissingMethodException: Method not found: void .<>c..ctor(int,int,Celeste.Mod.LogLevel,string,bool)
  at Celeste.Mod.CelesteFit.CelesteFitModule.Load()
```

That is the mod built against an Everest-merged `Celeste.dll`. It compiles cleanly and dies at load, because the merged assembly exposes a newer API surface than the game provides. Reference the pristine `Celeste.dll` plus `Celeste.Mod.mm.dll` separately — see [BUILD.md](../BUILD.md).

## Deploying the mod on the device

`adb` runs as the unprivileged `shell` user there, so it cannot write into Steam's game folder. The loop is:

```bash
adb push dist/CelesteFit.zip /sdcard/Download/
```

then copy the zip from `/sdcard/Download` into `Celeste/Mods/` from a terminal *on the device*, and start the game. Everest installs it on boot.

## Verifying it actually applied

Watch `log.txt` next to the executable:

- one `[CelesteFit]` line per phase, and
- one `room camera: engine 1920x1440 camera y a..b (= 240px at zoom 1.0)` line per room.

If the phase lines are missing, the mod did not load (check the Everest mod list). If the camera line says `180px`, the camera viewport patch did not apply, and world tiles will be culled below a fixed line on screen — see [PORTING-NOTES.md](PORTING-NOTES.md).

## Traps

- **Steam "verify integrity" and auto-update overwrite the patched `Celeste.exe`.** Keep auto-update off for this game, or re-run Everest afterwards.
- **A launch option cannot fix the runtime choice.** `WINEDLLOVERRIDES="mscoree=b" %command%` prepends to a value the handheld's own config already sets, so it does not win.
- **A dead Wine-Mono process looks like a slow boot.** 0% CPU and state `S` means blocked or crashed, not "still loading". Restart Steam rather than waiting.
- **Everest's "install missing dependencies" is not needed** by this mod: it targets only APIs the game itself provides.
