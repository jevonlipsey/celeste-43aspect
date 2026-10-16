# Handheld and Proton ARM notes

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

The clean fix is always to re-run the Everest installer for that build (Everest's own update path, or a fresh extract over the exposed game folder), not to hand-swap individual DLLs.

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
