# Handheld and Proton ARM notes

> These notes are kept here because they explain why this mod's hooks look the way they do. The general version — getting **Everest** itself to run on this stack, and the patched Cecil build it needs — lives in [celeste-everest-proton-arm64](https://github.com/jevonlipsey/celeste-everest-proton-arm64). Start there if Everest will not boot.

CelesteFit was developed against a Windows Celeste install running under Steam + Proton on an ARM64 Android handheld (DroidDeck-style device, Adreno 740, FEX/box64 translation, Wine-Mono runtime). Celeste itself is happy on this stack; these are the things that bit us around it, and none of them are CelesteFit bugs, though all of them look like one from the outside.

## Requirements on the device

| Thing | Value | Why |
| --- | --- | --- |
| Game branch | FNA (`beta-1.3.3.0` on Steam, or the itch FNA build) | the XNA branch is a different framework and the mod targets FNA types |
| Loader | Everest 1.4.x (tested 4449) | hooks and `ModModule` API |
| Loader files | see [celeste-everest-proton-arm64](https://github.com/jevonlipsey/celeste-everest-proton-arm64) | Everest's own first-run patch step needs a Cecil build with the native PDB writer stubbed |
| Mod build | `net472` | Wine-Mono cannot load a `net8.0` assembly, so a modern SDK still has to emit .NET Framework 4.7.2 |

## Signature: InvalidProgramException in a DMD wrapper

The one that cost us an afternoon, and worth recognising on sight:

```text
System.InvalidProgramException: Invalid IL code in [wrapper dynamic-method]
Celeste.ScreenWipe:DMD<Celeste.ScreenWipe::get_Bottom> (Celeste.ScreenWipe): IL_000f: ret

  at [wrapper managed-to-native] System.RuntimeMethodHandle.GetFunctionPointer(intptr)
  at MonoMod.RuntimeDetour.Platforms.DetourRuntimeILPlatform.GetFunctionPointer(MethodBase, RuntimeMethodHandle)
  at MonoMod.RuntimeDetour.Platforms.DetourRuntimeILPlatform.GetNativeStart(MethodBase)
  at MonoMod.RuntimeDetour.Detour._TopApply()
  at MonoMod.RuntimeDetour.ILHook..ctor(MethodBase, ILContext+Manipulator, ILHookConfig)
  at Celeste.Mod.CelesteFit.CelesteFitModule.HookMethod(Type, String, ILContext+Manipulator)
```

Read it bottom-up. The mod applied an `ILHook` to a trivial getter (`get_Bottom` is `ldarg.0; ldfld; ret`), MonoMod built the dynamic wrapper for it, and the runtime rejected that wrapper. The exception surfaces at `GetFunctionPointer`, which is nowhere near the cause, and the method named in the message is simply whichever one the mod hooked first — a red herring both times we saw it.

A single unhookable method must not take the whole mod down, which is why `HookMethod` in `CelesteFitModule.cs` catches and logs instead of throwing. When `log.txt` has a `[warn]` naming a method, this is usually why.

## Signature: MissingMethodException in the mod's own Load

```text
System.MissingMethodException: Method not found: void .<>c..ctor(int,int,Celeste.Mod.LogLevel,string,bool)
  at Celeste.Mod.CelesteFit.CelesteFitModule.Load()
```

That is the mod built against an Everest-merged `Celeste.dll`: it compiles cleanly and dies at load, because the merged assembly exposes a newer API surface than the game provides. Reference the pristine `Celeste.dll` plus `Celeste.Mod.mm.dll` separately — see [BUILD.md](../BUILD.md).

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
