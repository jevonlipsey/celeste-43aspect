# Porting notes

How the mod works and why each patch exists. Written for whoever has to fix this again after a Celeste or Everest update.

## The three coordinate spaces

Getting these straight explains almost every patch in `CelesteFitModule.cs`.

| Space | Size at 1280×960 output | Notes |
| --- | --- | --- |
| Gameplay buffer | 320×240 | `GameplayBuffers.Gameplay`. The world is drawn here at 1:1 (`Camera.Zoom` is 1) and then scaled to the screen, so the mod only has to grow *this* buffer to see more world. |
| UI / design space | 1920×1440 | `Engine.Width`/`Engine.Height`. Menus, HUD and wipes are laid out in this space and multiplied by `Engine.ScreenMatrix` on the way out. |
| Backbuffer | 1280×960 | The real framebuffer. `ScreenMatrix` scales design space down onto it. |

`GameHeight = (Engine.Height + 5) / 6` is the first row of the table expressed in the second: 1440 / 6 = 240 world units. `CameraPadding = GameHeight - 180` is how much taller the view is than the original 180, in world units.

The two capacities must agree. A patch that grows a buffer but not the thing that culls against it produces a screen that *looks* right in the middle and is missing content at an edge.

## The ten steps

1. **`Engine.UpdateView`** - drives everything. Sets `Engine.Height` to the tallest 4:3-ish height the display can show at an integer-ish scale, and writes `Engine`'s private static `viewPadding` field directly. It must be the field: `Engine.ViewPadding`'s setter calls `UpdateView`, so calling the setter from inside the hook recurses until the stack dies.
2. **`Level.ZoomSnap/ZoomTo/ZoomAcross`** - cutscene zooms pass a world-space target; `CameraPadding` is added to its Y so the framing stays put in the taller view.
3. **`GameplayRenderer` ctor** - the level camera *is* this camera (`LevelLoader` assigns `Level.Camera = GameplayRenderer.Camera`). Its viewport height is grown to `GameHeight` and its origin offset by `CameraPadding`.
4. **`Parallax.Render`** - offsets background layers by `CameraPadding` and stops three Chapter 3 sky layers from tiling. This is why `try`/`catch` around each hook matters even though hook failures are logged as warnings.
5. **`MountainRenderer.Render`** - the title screen vignette is a single `MTexture.Draw` with a fixed rectangle; rewritten as a full-screen stretched draw.
6. **`DustEdges`** - the shader's screen coordinates are computed from 1080-based constants and the camera, so both are corrected.
7. **Wipes** - `ScreenWipe.get_Bottom` and the four wipe `Render`s use `1090`, `183.33…` and `2160` as "below the screen" values. They become `Engine.Height + n`, with `ReplaceConstant` emitting an `int` or a `float` to match the original instruction.
8. **Culling** - `Level.IsInCamera`, `Lightning.InView` and `CrystalStaticSpinner.InView` compared `Camera.Y` (the camera's *position*) against the view; they now compare `Camera.Top` (the *view*), which is the same number in vanilla but not in a padded view.
9. **Constant rewrites** - two passes, both walking every method of a type list and replacing a hardcoded value with a delegate that reads the live screen size. "Low" types are backdrops and gameplay buffers (`180` → `GameHeight`, `244` → `GameHeight + 64`); "UI" types are menus, HUD and wipes (`1080` → `Engine.Height`, `1082` → `+2`, `540` → half, `1100` → `+20`).
10. **`PatchHudBuffers`** - the hires UI buffers are allocated at a fixed size and drawn 1:1, so they are re-allocated from the *measured* backbuffer (`BackBufferHeight * 1920 / BackBufferWidth + 2`) rather than from `Engine.Height`: `LoadContent` can run before the first `UpdateView`, and then the height is still 1080.

## Traps that already bit us

- **`Type.GetMethods()` does not return constructors.** Both constant passes silently skipped every `.ctor`. `GameplayRenderer`'s ctor held `new Camera(320, 180)`: the world rendered into a 240-tall buffer while the camera still believed the view was 180 tall, and `TileGrid.GetClippedRenderTiles` culled every tile below the 180 line — tiles missing, entities still drawn, the cut moving with the camera. The ctor is patched explicitly now. `NorthernLights` and `GlassBlockBg` still build `320×180` geometry in their constructors (Core chapter, cosmetic).
- **A merged `Celeste.dll` compiles but does not load.** Building against an Everest-merged assembly binds to newer signatures and the mod dies at runtime with `MissingMethodException`. Reference the pristine game assembly plus `Celeste.Mod.mm.dll` separately.
- **Wine-Mono cannot load `net8.0` mods.** Hence `net472` even on a modern SDK.
- **`ReplaceConstant` must match the original instruction type.** Wipe code pushes `1090` as `ldc.i4` in one place and `1090.0f` as `ldc.r4` in another; the helper takes both forms and emits whichever the original pushed.
- **Hook failures are non-fatal by design.** Everest moves some methods' bodies to `orig_*` when something else has already patched them, so `HookMethod` hooks both the name and `orig_` + name, and a failure is logged as a warning instead of killing the mod load.
- **The log is the fastest diagnostic.** Each phase logs `--- <step>`, and each room logs `room camera: … (= 240px at zoom 1.0)`. That one line is the quickest way to tell whether a buffer and its culler still agree.

## Finding hook points

`tools/mdinfo` reads a managed assembly and prints types, method IL, field offsets and type visibility. It is what found the camera viewport bug and the exact constants above.

```bash
dotnet build tools/mdinfo/mdinfo.csproj -c Release
dotnet tools/mdinfo/bin/Release/net9.0/mdinfo.dll types Celeste.exe SolidTiles
dotnet tools/mdinfo/bin/Release/net9.0/mdinfo.dll il Celeste.exe GameplayRenderer
```

Analyse the **Everest-patched** `Celeste.exe` from the game folder, not `Celeste.dll` from the install: Everest rewrites method bodies (moving originals to `orig_*`), and that rewritten assembly is what actually runs.
