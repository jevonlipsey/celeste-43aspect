# CelesteFit

**4:3 Celeste on PC, with original pixel proportions.**

CelesteFit is an [Everest](https://everestapi.github.io/) mod that expands Celeste's vertical view so a 4:3 (or any taller-than-16:9) display is filled without stretching, letterboxing or cropping. It is a port of the 4:3 patching work in [Celeste Fit](https://github.com/mikumaker/Celeste-Fit), which targets the Android runtime, re-implemented as runtime IL hooks so it works with Steam, cloud saves and other Everest mods.

**No Everest? [Patch your Celeste.exe in your browser.](https://jevonlipsey.github.io/celeste-43aspect/)** The web patcher applies the same changes directly to your own `Celeste.exe` (FNA 1.4.0.0). It runs entirely on your device, so nothing is uploaded and no game code is distributed. This is the route for Android handhelds (DroidDeck, GameNative, Winlator) where Everest is impractical.

## What changes

- **Taller gameplay buffer.** The internal view becomes 320×240 instead of 320×180 (a 1920×1440 UI space). The world is still drawn 1:1 and scaled uniformly to the display, so pixels stay square.
- **Camera anchoring.** Room bottoms and Madeline stay anchored to the bottom edge while the extra vertical space is revealed above.
- **Clipping that agrees with the screen.** The gameplay camera's viewport is grown to match the world buffer. Everything that culls against `Camera.Top`/`Camera.Bottom` — tiles, animated tiles, ice overlays, dream blocks, lightning bloom, `IsInCamera` — now covers the full visible area instead of stopping 25% up the screen.
- **UI and menus.** The hires UI buffers (HUD, menus, wipes, full-screen dimming rects) are allocated from the measured screen height instead of a hardcoded 1080.
- **Backdrops and effects.** Parallax layers, dust edges, the title screen mountain vignette and every wipe (curtain, angled, fall, doors, dream, wind, mountain) cover the full vertical extent.

## Requirements

| Requirement | Details                                                                                                 |
| ----------- | ------------------------------------------------------------------------------------------------------- |
| Game        | Celeste 1.4.0.0, **FNA** branch (Steam → properties → betas → `beta 1.3.3.0`, or the itch.io FNA build) |
| Loader      | Everest 1.4.x (tested on Stable Build 4449)                                                             |
| Display     | A 4:3 screen. 1280×960 tested on a Retroid Pocket Nova through DroidDeck.                               |

On an ARM handheld that runs Celeste through Steam + Proton, Everest needs a patched Cecil build (`Mono.Cecil.Pdb.dll` with the native PDB writer stubbed) before it can patch the game at all, and mods must be `net472`. If Everest will not boot, see [celeste-everest-proton-arm64](https://github.com/jevonlipsey/celeste-everest-proton-arm64) first: the symptoms look exactly like a broken mod, and are not.

## Install

1. Install Everest on your Celeste installation.
2. Download [CelesteFit.zip](../../releases/latest/download/CelesteFit.zip) — or see [Releases](../../releases) for older versions, or build it yourself, see [BUILD.md](BUILD.md).
3. Drop the zip into `Celeste/Mods/` and launch the game. Everest installs it on startup; no "install missing dependencies" step is needed.

Saves, Steam Cloud and achievements are untouched. To uninstall, delete the mod from the Everest mod list (or the zip from `Mods/`).

### Without Everest: the web patcher

For Android handhelds (DroidDeck, GameNative, Winlator) or anyone who'd rather not install a mod loader:

1. Open the **[Celeste 4:3 Patcher](https://jevonlipsey.github.io/celeste-43aspect/)** and choose the `Celeste.exe` from your game folder (FNA 1.4.0.0; on Steam, Properties → Betas → `beta 1.3.3.0`). The page lists where the file lives on each platform.
2. Download the patched `Celeste.exe`. Your file is patched in the browser and never uploaded.
3. Rename your original to `Celeste.exe.bak` and put the patched one in its place.

Steam's "Verify integrity" and game updates restore the original, so re-patch after either. To uninstall, restore the `.bak`. The web patcher refuses a `Celeste.exe` that already has Everest installed; use the mod above for that.

## Known limitations

- The expanded view is meant for displays taller than 16:9. On a 16:9 or wider display the mod is a no-op (`GameHeight` stays 180 and nothing is patched).
- A few Core-chapter backdrop and particle effects build their geometry at a fixed `320×180` inside their constructors, where the constant pass cannot see it. Those constructors are patched by name; the ones that matter (star and particle spawn ranges, the aurora and glass-block backgrounds, the chapter 9 black-hole centre) are listed and verified against the shipped game assembly.
- Some cutscenes frame slightly higher than authored; the fix favours keeping Madeline and readable dialogue on screen.

## Layout

| Path                         | Purpose                                                         |
| ---------------------------- | --------------------------------------------------------------- |
| `Source/CelesteFitModule.cs` | the whole mod: every hook, in ten steps                         |
| `Source/CelesteFit.csproj`   | `net472` build, references assembled from `libs/`               |
| `libs/`                      | your own game assemblies, git-ignored, see [BUILD.md](BUILD.md) |
| `scripts/`                   | fetch the reference assemblies, build and package               |
| `tools/mdinfo`               | the Mono/IL inspector used to find every hook point             |
| `docs/PORTING-NOTES.md`      | how the port works and why each patch exists                    |
| `docs/HANDHELD-NOTES.md`     | running this under Steam + Proton on an ARM64 handheld          |
| `patcher/`                   | the web patcher's core: the mod's hooks as a static Cecil rewrite of `Celeste.exe`; `patcher/helper/` is the runtime half cloned into the exe |
| `verify/`                    | the checks every patched exe must pass before it is handed back |
| `web/`                       | the patcher page, its .NET WebAssembly build and browser tests; `tools/build_web.sh` builds it and `.github/workflows/pages.yml` deploys it |

## Credits and license

The 4:3 viewport, camera, backdrop and transition behaviour comes from **[mikumaker/Celeste-Fit](https://github.com/mikumaker/Celeste-Fit)** (MIT), itself a modification of [BelmanteGu/CelesteAndroid](https://github.com/BelmanteGu/CelesteAndroid). Thanks to the Everest, MonoMod, Mono.Cecil and FNA projects.

Celeste is by Maddy Thorson, Noel Berry and the Celeste team, © Maddy Makes Games Inc. CelesteFit is an unofficial community modification and is not affiliated with the original developers.

Modification source is provided under the [MIT License](LICENSE). Game files are not included and never will be; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
