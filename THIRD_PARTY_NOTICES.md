# Third-party notices

CelesteFit is an Everest mod. This repository contains only the mod's source and build scripts; every dependency is supplied separately from a copy you own. Each component keeps its own license.

| Component | Purpose | License |
| --- | --- | --- |
| [Everest](https://github.com/EverestAPI/Everest) | Mod loader and runtime the mod is built against | MIT |
| [MonoMod](https://github.com/MonoMod/MonoMod) | IL hooking at runtime | MIT |
| [Mono.Cecil](https://github.com/jbevain/cecil) | Assembly inspection used by the hooks | MIT |
| [FNA](https://github.com/FNA-XNA/FNA) | XNA-compatible game framework Celeste runs on | Ms-PL |

## Celeste

Celeste is © Maddy Makes Games Inc. The game executable, levels, music, textures and other content are **not** included in this repository and are never redistributed by it. `libs/` is an empty, git-ignored folder that you fill from your own installation — see [BUILD.md](BUILD.md).

CelesteFit is an unofficial community modification and is not affiliated with or endorsed by Maddy Makes Games, Extremely OK Games or the Everest team. The MIT license covers this mod's source only, not the game or any third-party component.

## Attribution

The 4:3 viewport work originates in [Celeste Fit](https://github.com/mikumaker/Celeste-Fit) (MIT), which patches the [CelesteAndroid](https://github.com/BelmanteGu/CelesteAndroid) runtime for Android handhelds. CelesteFit re-implements the same behaviour as Everest hooks for the PC/Steam build. The upstream copyright notice is retained in [LICENSE](LICENSE).

The IL inspection tool in `tools/mdinfo` is this project's own work and shares the repository license.
