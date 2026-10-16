# CelesteFit

**Full-screen 4:3 Celeste on PC, with the original pixel proportions.**

CelesteFit is an [Everest](https://everestapi.github.io/) mod that expands Celeste's vertical view so a 4:3 (or any taller-than-16:9) display is filled without stretching, letterboxing or cropping. It is a port of the 4:3 patching work in [Celeste Fit](https://github.com/mikumaker/Celeste-Fit), which targets the Android runtime, re-implemented as runtime IL hooks so it works with Steam, cloud saves and other Everest mods.

## What changes

- **Taller gameplay buffer.** The internal view becomes 320×240 instead of 320×180 (a 1920×1440 UI space). The world is still drawn 1:1 and scaled uniformly to the display, so pixels stay square.
- **Camera anchoring.** Room bottoms and Madeline stay anchored to the bottom edge while the extra vertical space is revealed above, including during cutscene zooms.
- **Clipping that agrees with the screen.** The gameplay camera's viewport is grown to match the world buffer. Everything that culls against `Camera.Top`/`Camera.Bottom` — tiles, animated tiles, ice overlays, dream blocks, lightning bloom, `IsInCamera` — now covers the full visible area instead of stopping 25% up the screen.
- **UI and menus.** The hires UI buffers (HUD, menus, wipes, full-screen dimming rects) are allocated from the measured screen height instead of a hardcoded 1080.
- **Backdrops and effects.** Parallax layers, dust edges, the title screen mountain vignette and every wipe (curtain, angled, fall, doors, dream, wind, mountain) cover the full vertical extent.

## Requirements

| Requirement | Details |
| --- | --- |
| Game | Celeste 1.4.0.0, **FNA** branch (Steam → properties → betas → `beta 1.3.3.0`, or the itch.io FNA build) |
| Loader | Everest 1.4.x (tested on 4449) |
| Display | A landscape screen taller than 16:9. 1280×960 tested on a Retroid Pocket Nova and a DroidDeck handheld |

## Install

1. Install Everest on your Celeste installation.
2. Download `CelesteFit.zip` from [Releases](../../releases) — or build it yourself, see [BUILD.md](BUILD.md).
3. Drop the zip into `Celeste/Mods/` and launch the game. Everest installs it on startup; no "install missing dependencies" step is needed.

Saves, Steam Cloud and achievements are untouched. To uninstall, delete the mod from the Everest mod list (or the zip from `Mods/`).

## Known limitations

- The expanded view is meant for displays taller than 16:9. On a 16:9 or wider display the mod is a no-op (`GameHeight` stays 180 and nothing is patched).
- A few Core-chapter backdrop constructors build their geometry at a fixed `320×180` and are not patched yet, because the constant pass only walks methods, not constructors. Cosmetic only: the aurora (`NorthernLights`) and glass background (`GlassBlockBg`) stop short of the bottom edge.
- Some cutscenes frame slightly higher than authored; the fix favours keeping Madeline and readable dialogue on screen.

## Layout

| Path | Purpose |
| --- | --- |
| `Source/CelesteFitModule.cs` | the whole mod: every hook, in ten steps |
| `Source/CelesteFit.csproj` | `net472` build, references assembled from `libs/` |
| `libs/` | your own game assemblies, git-ignored, see [BUILD.md](BUILD.md) |
| `scripts/` | fetch the reference assemblies, build and package |
| `tools/mdinfo` | the Mono/IL inspector used to find every hook point |
| `docs/PORTING-NOTES.md` | how the port works and why each patch exists |

## Credits and license

The 4:3 viewport, camera, backdrop and transition behaviour comes from **[mikumaker/Celeste-Fit](https://github.com/mikumaker/Celeste-Fit)** (MIT), itself a modification of [BelmanteGu/CelesteAndroid](https://github.com/BelmanteGu/CelesteAndroid). Thanks to the Everest, MonoMod, Mono.Cecil and FNA projects.

Celeste is by Maddy Thorson, Noel Berry and the Celeste team, © Maddy Makes Games Inc. CelesteFit is an unofficial community modification and is not affiliated with the original developers.

Modification source is provided under the [MIT License](LICENSE). Game files are not included and never will be; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
