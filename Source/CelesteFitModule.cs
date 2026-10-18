using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Celeste;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Mono.Cecil;
using Mono.Cecil.Cil;

// everest's ui types sit under namespaces that begin with `Celeste`, which is also the name of
// the game's main class, so qualify them through aliases instead of fully qualified names.
using AssetReloadHelper = global::Celeste.Mod.AssetReloadHelper;
using CoreModule = global::Celeste.Mod.Core.CoreModule;
using CustomHeightDisplay = global::Celeste.Mod.Entities.CustomHeightDisplay;
using CustomTextVignette = global::Celeste.Mod.Entities.CustomTextVignette;
using OuiGenericMenu = global::Celeste.Mod.UI.OuiGenericMenu;
using OuiLoggedProgress = global::Celeste.Mod.UI.OuiLoggedProgress;
using OuiMapList = global::Celeste.Mod.UI.OuiMapList;
using OuiMapSearch = global::Celeste.Mod.UI.OuiMapSearch;
using OuiModOptions = global::Celeste.Mod.UI.OuiModOptions;
using OuiModOptionString = global::Celeste.Mod.UI.OuiModOptionString;
using OuiNumberEntry = global::Celeste.Mod.UI.OuiNumberEntry;
using OuiOOBE = global::Celeste.Mod.UI.OuiOOBE;
using OuiSoundTest = global::Celeste.Mod.UI.OuiSoundTest;
using OuiVersionList = global::Celeste.Mod.UI.OuiVersionList;
using Pico8Emulator = global::Celeste.Pico8.Emulator;
using SceneRenderer = global::Celeste.Mod.Entities.SceneRenderer;

namespace Celeste.Mod.CelesteFit;

public class CelesteFitModule : EverestModule {
    public static CelesteFitModule Instance { get; private set; } = null!;

    private readonly List<IDisposable> hooks = new();
    private static readonly MethodInfo setHeightMethod = typeof(Engine).GetProperty("Height", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetSetMethod(true)!;

    // Engine.ViewPadding's setter calls UpdateView, so setting it from inside UpdateView recurses
    // forever. poke the backing field instead.
    private static readonly FieldInfo viewPaddingField = typeof(Engine).GetField("viewPadding", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static bool _hiresSnowRan;
    private static bool _lightingRan;
    private static readonly HashSet<string> _seenBackdrops = new();
    private static Action<Parallax, Scene>? parallaxTiler;
    // rendering is single threaded, so the current backdrop's loop flag can live in a static
    private static bool _parallaxLoopY;
    private static Parallax? _parallax;
    // the screen-covering rects the 16:9 codebase draws. the game paints the whole view with a rect
    // 10px bigger than 320x180 on every side, so the height is always the view height plus a few rows.
    // keyed on the (width, height) pair, because either number alone is too common to replace safely.
    private static readonly (float width, float height, float extra)[] ScreenCovers = {
        (340f, 200f, 20f), // -10,-10 340x200: fades, bloom base fill, the ascend background
        (26f, 200f, 20f),  // the ascend launch edge strips
        (322f, 182f, 2f),  // -1,-1 322x182: the level flash, farewell/gondola/formation darkness
        (370f, 190f, 10f), // heat wave displacement fill, -5,-5
    };
    // last bloom state logged, so the probe only writes when something changes
    private static string? _lastBloomProbe;
    // the authored y of each parallax layer, captured the first time that layer renders. Parallax.Update
    // moves Position for layers with drift, so the first value seen is the authored one and the only
    // stable way to tell a layer authored to fill the view (y 0) from one authored to hang off the top
    // of it (summit's distant layers sit at -45 / -60 / -150)
    private static readonly Dictionary<Parallax, float> _authoredTop = new();
    // one log line per distinct layer/scale, so the stretch decisions are checkable from the device log
    private static readonly HashSet<string> _loggedStretches = new();

    public CelesteFitModule() {
        Instance = this;
    }

    public static int GameHeight => (Engine.Height + 5) / 6;
    public static float CameraPadding => (float)(GameHeight - 180);

    public override void Load() {
        Logger.Log(LogLevel.Info, "CelesteFit", "Applying 4:3 / aspect ratio patches...");

        // 1. Hook Engine.UpdateView
        Step("engine.UpdateView");
        HookMethod(typeof(Engine), "UpdateView", PatchEngineUpdateView);

        // 2. (removed) Level.ZoomSnap/ZoomTo/ZoomAcross used to get CameraPadding added to the y of
        // their screen space focus point, which only made sense while the camera origin was shifted
        // down by the same amount. with the origin back at zero the zoom framing is vanilla's again:
        // the focus point lands where 16:9 puts it and the extra rows fall below it.

        // 3. Hook GameplayRenderer constructor (camera viewport height)
        Step("gameplayrenderer ctor");
        foreach (var ctor in typeof(GameplayRenderer).GetConstructors()) {
            try {
                hooks.Add(new ILHook(ctor, PatchGameplayRendererCtor));
            } catch (Exception e) {
                Logger.Log(LogLevel.Warn, "CelesteFit", $"Failed to hook GameplayRenderer ctor: {e.GetType().Name}: {e.Message}");
            }
        }

        // 3b. one line per room so a wrong camera viewport is visible in the log
        Step("camera probe");
        HookMethod(typeof(Level), "LoadLevel", PatchCameraProbe);

        // 4. Hook Parallax.Render
        Step("parallax.Render");
        PatchParallaxRenderer();

        // 4b. the 16:9 code covers the screen with a rect a little larger than the view on every
        // side (340x200 at -10,-10), or with a 26px edge strip, so -10..190 gets painted and the
        // bottom of a 4:3 view stays clear. bloom's base fill is the one that shows up in gameplay:
        // it is the brightness the whole screen is lifted by, so an unpainted bottom 25% reads as a
        // dark band that is invisible on chapters with BloomBase 0 and obvious on bright ones.
        // every entry here is one of the 16 rects of that shape in Celeste.exe, and most of them live
        // in a nested class (the fade helpers), which is why they are spelled out with the nesting:
        // a plain "Celeste.CS03_Ending" resolves but has no Render of its own, so its cover stayed
        // 200 rows tall and the bottom 40 rows of that scene never faded.
        Step("screen fade rects");
        foreach (var (typeName, methodName) in new[] {
            ("Celeste.BloomRenderer", "Apply"),
            ("Celeste.BackdropRenderer", "Render"),
            ("Celeste.AscendManager", "Render"),
            ("Celeste.AscendManager+Fader", "Render"),
            ("Celeste.AscendManager+Streaks", "Render"),
            ("Celeste.BackgroundFadeIn", "Render"),
            ("Celeste.SummitGem+BgFlash", "Render"),
            ("Celeste.CS03_Ending+BgFlash", "Render"),
            ("Celeste.CS04_MirrorPortal+Fader", "Render"),
            ("Celeste.CS07_Credits+Fill", "Render"),
            ("Celeste.CS10_MoonIntro", "Render"),
            // 322x182 and 370x190 covers: the level flash is drawn in buffer space, the rest start
            // one or five pixels above the camera top, so all of them stop short of a 240 row view
            ("Celeste.Level", "Render"),
            ("Celeste.CS10_Farewell", "Render"),
            ("Celeste.FormationBackdrop", "Render"),
            ("Celeste.GondolaDarkness+Blackness", "Render"),
            ("Celeste.HeatWave", "RenderDisplacement"),
        }) {
            var type = FindType(typeName);
            if (type == null) continue;
            HookMethod(type, methodName, PatchScreenFadeRects);
        }

        // 4c. one line per backdrop so a mis-sized one shows up in the log without a debug build
        Step("backdrop probe");
        HookMethod(typeof(BackdropRenderer), "Update", il => {
            var cursor = new ILCursor(il);
            cursor.Emit(OpCodes.Ldarg_0);
            cursor.EmitDelegate<Action<BackdropRenderer>>(r => {
                foreach (var backdrop in r.Backdrops) {
                    if (backdrop is not Parallax p || p.Texture == null) continue;
                    if (!_seenBackdrops.Add(p.Texture.AtlasPath ?? "?")) continue;
                    var cam = (Engine.Scene as Level)?.Camera;
                    var y = cam != null ? (p.Position - (cam.Position + p.CameraOffset).Floor() * p.Scroll).Floor().Y : 0f;
                    Logger.Log(LogLevel.Info, "CelesteFit",
                        $"backdrop: {p.Texture.AtlasPath} loopY={p.LoopY} tex={p.Texture.Width}x{p.Texture.Height} pos={p.Position} scroll={p.Scroll} screenY={y:F0}");
                }
            });
        });

        // 4c2. one line per distinct bloom state so the base fill and the screen size behind it are
        // provable from the log. base is the screen-wide brightness lift: 0 on most chapters (the
        // band is invisible) and positive on the bright ones (where it shows up).
        Step("bloom probe");
        HookMethod(typeof(BloomRenderer), "Apply", il => {
            var cursor = new ILCursor(il);
            cursor.Emit(OpCodes.Ldarg_0);
            cursor.EmitDelegate<Action<BloomRenderer>>(b => {
                var line = $"bloom: base={b.Base:F3} strength={b.Strength:F2} screen={Engine.Width}x{Engine.Height}";
                if (line == _lastBloomProbe) return;
                _lastBloomProbe = line;
                Logger.Log(LogLevel.Info, "CelesteFit", line);
            });
        });

        // 4d. hud text and icons placed by a bare y literal that only means anything against a 1080
        // tall screen. the value is not one of the screen constants, so the ui sweep cannot see it,
        // and once the screen is 1440 tall the element lands in the middle of the menu instead of
        // below it (return to map hint and the quick restart hint both overlap the menu text).
        Step("bottom anchored hud");
        HookMethod(typeof(ReturnMapHint), "Render", il => ReanchorToBottom(il, 730f, 350f));
        HookMethod(typeof(QuickResetHint), "Render", il => ReanchorToBottom(il, 980f, 100f));
        HookMethod(typeof(SaveLoadIcon), "Render", il => ReanchorToBottom(il, 920f, 160f));

        // 5. Hook MountainRenderer.Render for menu vignette
        Step("mountainrenderer.Render");
        HookMethod(typeof(MountainRenderer), "Render", PatchMountainRendererRender);

        // 6. Hook DustEdges
        Step("dustedges");
        HookMethod(typeof(DustEdges), "BeforeRender", PatchDustEdgesBeforeRender);
        HookMethod(typeof(DustEdges), "Render", PatchDustEdgesRender);

        // 7. Hook Wipes
        Step("wipes");
        HookMethod(typeof(ScreenWipe), "get_Bottom", il => ReplaceConstant(il, 1090f, () => Engine.Height + 10f, () => Engine.Height + 10));
        HookMethod(typeof(KeyDoorWipe), "Render", il => ReplaceConstant(il, 1090f, () => Engine.Height + 10f, () => Engine.Height + 10));
        HookMethod(typeof(CurtainWipe), "Render", il => ReplaceConstant(il, 1090f, () => Engine.Height + 10f, () => Engine.Height + 10));
        HookMethod(typeof(AngledWipe), "Render", il => ReplaceConstant(il, 183.33333f, () => (Engine.Height + 20f) / 6f, () => (Engine.Height + 20) / 6));
        HookMethod(typeof(FallWipe), "Render", il => ReplaceConstant(il, 2160f, () => Engine.Height * 2f, () => Engine.Height * 2));
        HookMethod(typeof(OuiTitleScreen), "Render", il => ReplaceConstant(il, 1100f, () => Engine.Height + 20f, () => Engine.Height + 20));

        // 7b. the damage flash rect is the old 320x180 design size with a 2px bleed, leaving the
        // bottom of the taller gameplay buffer unlit while the flash is up
        Step("level flash");
        HookMethod(typeof(Level), "Render", il => ReplaceConstant(il, 182f, () => GameHeight + 2f, () => GameHeight + 2));

        // 7b2. the zoom anchor uses 180 as the design height; grow it to 4:3
        HookMethod(typeof(Level), "Render", il => ReplaceConstant(il, 180f, () => (float)GameHeight, () => GameHeight));

        // 7c. lighting and wall culls that assumed a 180-tall view: lights/walls below the old
        // 16:9 line were dropped, leaving the bottom of the screen dark or empty
        Step("lighting/wall culls");
        HookMethod(typeof(LightingRenderer), "BeforeRender", il => ReplaceConstant(il, 180f, () => GameHeight, () => GameHeight));
        HookMethod(typeof(FakeWall), "Render", il => ReplaceConstant(il, 180f, () => GameHeight, () => GameHeight));
        HookMethod(typeof(ExitBlock), "Render", il => ReplaceConstant(il, 180f, () => GameHeight, () => GameHeight));

        // runtime probes: confirm these methods actually run with the new dims
        Step("runtime probes");
        // HiresSnow is handled by the UI sweep (1080 -> Engine.Height)
        HookMethod(typeof(LightingRenderer), "BeforeRender", il => {
            var cursor = new ILCursor(il);
            cursor.Emit(OpCodes.Ldarg_0);
            cursor.EmitDelegate<Action<LightingRenderer>>(lr => {
                if (!_lightingRan) {
                    _lightingRan = true;
                    Logger.Log(LogLevel.Info, "CelesteFit", $"LightingRenderer.BeforeRender ran: GameHeight={GameHeight}");
                }
            });
        });

        // 7e. target center, lighting bloom, camera bounds, and zoom use the old 180/1080 design size
        Step("target center + bloom + bounds");
        HookMethod(typeof(Celeste), "get_TargetCenter", il => ReplaceConstant(il, 1080f, () => (float)Engine.Height, () => Engine.Height));
        HookMethod(typeof(LightningRenderer), "OnRenderBloom", il => ReplaceConstant(il, 180f, () => (float)GameHeight, () => GameHeight));
        HookMethod(typeof(Level), "EnforceBounds", il => ReplaceConstant(il, 180f, () => (float)GameHeight, () => GameHeight));
        HookMethod(typeof(Level), "ResetZoom", il => ReplaceConstant(il, 180f, () => (float)GameHeight, () => GameHeight));
        // ScreenToWorld and WorldToScreen carry their own copy of the zoom blit maths, so their design
        // size has to stay 320x180 to agree with Level.Render's copy: the focus point is a screen space
        // point, and a 320x240 design size would put zoomed content 30 rows lower than the blit does.
        // what they do need is the padding aspect, see PatchScreenPaddingAspect.
        HookMethod(typeof(Level), "ScreenToWorld", PatchScreenPaddingAspect);
        HookMethod(typeof(Level), "WorldToScreen", PatchScreenPaddingAspect);
        HookMethod(typeof(Level), "Render", PatchScreenPaddingAspect);
        HookMethod(typeof(HiresRenderer), "get_DrawToBuffer", il => ReplaceConstant(il, 1080f, () => (float)Engine.Height, () => Engine.Height));

        // 8. Hook Culling checks
        Step("culling");
        HookMethod(typeof(Level), "IsInCamera", PatchCulling);
        HookMethod(typeof(Lightning), "InView", PatchCulling);
        HookMethod(typeof(CrystalStaticSpinner), "InView", PatchCulling);

        // 9. Low & UI rendering buffers/views
        Step("low types");
        PatchConstantsInLowTypes();
        Step("ui types");
        PatchConstantsInUITypes();

        // 10. the hires ui buffers (menus, hud, wipes) are allocated at a fixed size and drawn
        // into 1:1, so they have to follow the virtual screen height as well.
        Step("ui buffers");
        PatchHudBuffers();

        Logger.Log(LogLevel.Info, "CelesteFit", $"CelesteFit patches applied successfully! ({hooks.Count} hooks)");
    }

    private static void Step(string what) {
        Logger.Log(LogLevel.Info, "CelesteFit", $"--- step: {what}");
    }

    public override void Unload() {
        foreach (var hook in hooks) {
            hook.Dispose();
        }
        hooks.Clear();
    }

    private void HookMethod(Type type, string methodName, ILContext.Manipulator manipulator) {
        // everest's installer moves some method bodies aside, leaving `name` calling `orig_name`.
        // the real code (and therefore the constants we look for) lives in orig_*, so try both.
        bool hooked = TryHook(type, methodName, manipulator);
        hooked |= TryHook(type, $"orig_{methodName}", manipulator);

        if (!hooked) {
            Logger.Log(LogLevel.Warn, "CelesteFit", $"Could not find method {type.FullName}.{methodName} to hook");
        }
    }

    private bool TryHook(Type type, string methodName, ILContext.Manipulator manipulator) {
        MethodInfo? method;
        try {
            method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        } catch (AmbiguousMatchException) {
            method = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == methodName);
        }

        if (method == null) return false;

        // one bad hook shouldn't take the whole mod (and the game) down with it
        try {
            hooks.Add(new ILHook(method, manipulator));
            return true;
        } catch (Exception e) {
            Logger.Log(LogLevel.Warn, "CelesteFit", $"Failed to hook {type.FullName}.{methodName}: {e.GetType().Name}: {e.Message}");
            return false;
        }
    }

    #region IL Manipulators

    private static void PatchEngineUpdateView(ILContext il) {
        var cursor = new ILCursor(il);
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.EmitDelegate<Action<Engine>>(engine => {
            var pp = engine.GraphicsDevice.PresentationParameters;
            if (pp.BackBufferWidth > 0) {
                int newHeight = pp.BackBufferHeight * 1920 / pp.BackBufferWidth;
                setHeightMethod.Invoke(null, new object[] { newHeight });
            }
            viewPaddingField.SetValue(null, 0);
        });
    }

    // the level camera *is* this camera (LevelLoader assigns Level.Camera = GameplayRenderer.Camera),
    // and the world is drawn into GameplayBuffers.Gameplay. that buffer grows to GameHeight, so the
    // camera viewport has to grow with it: everything that clips against Camera.Top/Bottom uses it as
    // the view size, and a 180 view culls every tile below the 180px line out of the 240px screen.
    //
    // origin is left at zero on purpose. camera matrix is translate(-position) * scale(zoom) *
    // translate(+origin), so any origin shift moves the world inside the buffer but not the parallax,
    // which is drawn in screen space with an identity matrix. shifting down by CameraPadding also
    // pushes every world-space screen cover (the ascend fader starts at camera.Y - 10 and is 260 tall,
    // so it would begin 50px down the screen) and leaves the world misaligned with the art behind it.
    // with origin zero the camera top is buffer row 0 exactly as in 16:9, so the extra 60 rows land
    // below the view and every one of those rects still covers the whole screen.
    private static void PatchGameplayRendererCtor(ILContext il) {
        var cursor = new ILCursor(il);
        while (cursor.TryGotoNext(i => (i.OpCode == OpCodes.Ldc_I4 && (int)i.Operand == 180)
                                    || (i.OpCode == OpCodes.Ldc_R4 && Math.Abs((float)i.Operand - 180f) < 0.01f))) {
            bool isFloat = cursor.Next.OpCode == OpCodes.Ldc_R4;
            cursor.Remove();
            if (isFloat) cursor.EmitDelegate<Func<float>>(() => (float)GameHeight);
            else cursor.EmitDelegate<Func<int>>(() => GameHeight);
        }
    }

    /// logs the numbers the world clipping depends on, once per room, so a bad camera shows up in
    /// the log without having to guess (bottom - top) * zoom is the viewport height the tiles use.
    private static void PatchCameraProbe(ILContext il) {
        var cursor = new ILCursor(il);
        while (cursor.TryGotoNext(MoveType.Before, i => i.OpCode == OpCodes.Ret)) {
            cursor.Emit(OpCodes.Ldarg_0);
            cursor.EmitDelegate<Action<Level>>(level => {
                var cam = level?.Camera;
                if (cam == null) return;
                Logger.Log(LogLevel.Info, "CelesteFit",
                    $"room camera: engine {Engine.Width}x{Engine.Height} " +
                    $"camera y {cam.Top:F0}..{cam.Bottom:F0} (= {(cam.Bottom - cam.Top) * cam.Zoom:F0}px at zoom {cam.Zoom:F1}) " +
                    $"pos y {cam.Position.Y:F0} origin y {cam.Origin.Y:F0} row0 {cam.ScreenToCamera(Vector2.Zero).Y:F0}");
            });
            cursor.Index++;
        }
    }

    /// the chapter backdrops are authored to be painted exactly once (LoopY = false on almost all
    /// of them), so the tiler draws a single 180-tall tile and breaks - no loop bound can ever fill
    /// a 240-tall view. stretch that one tile to the full height instead of leaving a band.
    /// motion smoothing (which is baked into the shipped Celeste.exe here, deleting the mod does
    /// not undo it) owns Parallax.Render and caps its own quad at 180 too, so route Render straight
    /// to the vanilla tiler, and fall back to forcing its PointWrap guard if the detour fails.
    private static void PatchParallaxRenderer() {
        var render = typeof(Parallax).GetMethod("Render", BindingFlags.Public | BindingFlags.Instance);
        var orig = typeof(Parallax).GetMethod("orig_Render", BindingFlags.Public | BindingFlags.Instance);
        if (render == null) return;

        // orig_Render is the untouched vanilla tiler, and it is a real method we can il-hook
        if (orig != null) {
            Instance.HookMethod(typeof(Parallax), "orig_Render", PatchParallaxRender);
            try {
                parallaxTiler = (Action<Parallax, Scene>)Delegate.CreateDelegate(typeof(Action<Parallax, Scene>), orig);
                Instance.hooks.Add(new Hook(render, (Action<Parallax, Scene>)ParallaxRenderDetour));
            } catch (Exception e) {
                Logger.Log(LogLevel.Warn, "CelesteFit", $"Failed to route Parallax.Render: {e.GetType().Name}: {e.Message}");
            }
        } else {
            Instance.HookMethod(typeof(Parallax), "Render", PatchParallaxRender);
        }

        // if the detour could not be installed: motion smoothing's Render delegates to the vanilla
        // tiler whenever the batch sampler is not PointWrap, so flip the backdrop batch to PointClamp
        Instance.HookMethod(typeof(BackdropRenderer), "StartSpritebatchLooping", il => {
            var cursor = new ILCursor(il);
            while (cursor.TryGotoNext(i => i.OpCode == OpCodes.Ldsfld
                                        && i.Operand is FieldReference f && f.Name == "PointWrap")) {
                cursor.Remove();
                cursor.EmitDelegate<Func<SamplerState>>(() => SamplerState.PointClamp);
            }
        });
    }

    private static void ParallaxRenderDetour(Parallax self, Scene scene) {
        parallaxTiler?.Invoke(self, scene);
    }

    private static void PatchParallaxRender(ILContext il) {
        // grow the tile loop to the 4:3 height
        ReplaceConstant(il, 180f, () => (float)GameHeight, () => GameHeight);

        var cursor = new ILCursor(il);
        // remember the current backdrop so the draw below can decide whether to stretch
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.EmitDelegate<Action<Parallax>>(p => {
            _parallax = p;
            _parallaxLoopY = p.LoopY;
        });

        while (cursor.TryGotoNext(i => i.OpCode == OpCodes.Callvirt && i.Operand is MethodReference m
                                    && m.DeclaringType.Name == "MTexture" && m.Name == "Draw" && m.Parameters.Count == 6)) {
            cursor.Remove();
            cursor.EmitDelegate<Action<MTexture, Vector2, Vector2, Color, float, float, SpriteEffects>>(
                (tex, pos, origin, color, scale, rot, flip) => {
                    // a backdrop authored to be painted once (LoopY = false) only ever covers its own
                    // 180 rows. stretch it down to the bottom of the view when it was authored to
                    // reach the bottom of a 16:9 view and no longer reaches the bottom of a 4:3 one.
                    // the authored top edge is kept, so the layer grows instead of shifting.
                    float top = pos.Y;
                    float authoredBottom = AuthoredTop(_parallax, top) + tex.Height;
                    if (!_parallaxLoopY && tex.Height > 0 && authoredBottom >= 180f && authoredBottom < GameHeight) {
                        float scaleY = (GameHeight - top) / tex.Height;
                        tex.Draw(new Vector2(pos.X, top), origin, color, new Vector2(1f, scaleY), rot, flip);
                        LogStretch(tex, top, authoredBottom, scaleY);
                    } else {
                        tex.Draw(pos, origin, color, scale, rot, flip);
                    }
                });
        }
    }

    /// the y a parallax layer was authored at. Parallax.Update adds drift to Position, so the first
    /// value seen for a layer is the authored one, and caching it keeps the stretch decision stable
    /// (a layer near the threshold would otherwise pop between stretched and vanilla as it drifts).
    private static float AuthoredTop(Parallax? parallax, float fallback) {
        if (parallax == null) return fallback;
        if (_authoredTop.TryGetValue(parallax, out var cached)) return cached;
        if (_authoredTop.Count > 512) _authoredTop.Clear();
        _authoredTop[parallax] = parallax.Position.Y;
        return parallax.Position.Y;
    }

    /// one line per layer/scale so the stretch decisions are checkable from the device log
    private static void LogStretch(MTexture tex, float top, float authoredBottom, float scaleY) {
        try {
            var key = $"{tex.AtlasPath}|{top:F0}|{scaleY:F2}";
            if (!_loggedStretches.Add(key)) return;
            Logger.Log(LogLevel.Info, "CelesteFit",
                $"backdrop stretch: {tex.AtlasPath} top={top:F0} authoredBottom={authoredBottom:F0} scaleY={scaleY:F2}");
        } catch {
            // a probe must never take the renderer down with it
        }
    }

    private static void PatchMountainRendererRender(ILContext il) {
        var cursor = new ILCursor(il);
        while (cursor.TryGotoNext(i => i.MatchCallvirt<MTexture>("Draw"))) {
            if (cursor.Next.Operand is MethodReference m && m.Parameters.Count == 3) {
                cursor.Remove();
                cursor.EmitDelegate<Action<MTexture, Vector2, Vector2, Color>>((texture, pos, orig, color) => {
                    var scale = new Vector2((float)Engine.Width / texture.Width, (float)Engine.Height / texture.Height);
                    texture.Draw(pos, orig, color, scale);
                });
            }
        }
    }

    private static void PatchDustEdgesBeforeRender(ILContext il) {
        var cursor = new ILCursor(il);
        while (cursor.TryGotoNext(i => i.OpCode == OpCodes.Ldc_R4 && Math.Abs((float)i.Operand - 180f) < 0.01f)) {
            cursor.Remove();
            cursor.EmitDelegate<Func<float>>(() => (float)GameHeight);
        }
        cursor.Index = 0;
        while (cursor.TryGotoNext(i => i.OpCode == OpCodes.Ldc_R4 && Math.Abs((float)i.Operand - (1f / 180f)) < 0.0001f)) {
            cursor.Remove();
            cursor.EmitDelegate<Func<float>>(() => 1f / GameHeight);
        }
    }

    private static void PatchDustEdgesRender(ILContext il) {
        var cursor = new ILCursor(il);
        if (cursor.TryGotoNext(i => i.MatchCall<DustEdges>("FlooredCamera"))) {
            cursor.Remove();
            cursor.Emit(OpCodes.Ldarg_0);
            cursor.EmitDelegate<Func<DustEdges, Vector2>>(dust => {
                if (dust.Scene is Level level) {
                    return level.Camera.ScreenToCamera(Vector2.Zero);
                }
                return Vector2.Zero;
            });
        }
    }

    /// a bottom anchored hud element is placed with a bare y literal, like 730 on a 1080 tall
    /// screen. keep the distance from the bottom instead of the distance from the top.
    private static void ReanchorToBottom(ILContext il, float original, float fromBottom) {
        var cursor = new ILCursor(il);
        int replaced = 0;
        while (cursor.TryGotoNext(i => i.OpCode == OpCodes.Ldc_R4 && Math.Abs((float)i.Operand - original) < 0.01f)) {
            cursor.Remove();
            cursor.EmitDelegate<Func<float>>(() => Engine.Height - fromBottom);
            replaced++;
        }
        if (replaced > 0) LogReplacements(il, replaced);
    }

    /// a full-screen fill in the 16:9 codebase is a rect drawn a little larger than the view on every
    /// side, so only its height needs the 4:3 treatment. guarded on the width, because the height
    /// constant (200) is a common number and blanket-replacing it would corrupt unrelated maths.
    /// ScreenPadding insets the view by that many pixels on the left and right, and by the same
    /// fraction of the view height at the top and bottom: 16 * (180/320) = 9 rows in 16:9. the 4:3
    /// correction is 16 * (240/320) = 12, otherwise the inset is 9 rows at the top and 15 at the
    /// bottom. ScreenPadding is zero everywhere except the lookout, the seeker and the mirror portal.
    private static void PatchScreenPaddingAspect(ILContext il) {
        var cursor = new ILCursor(il);
        int replaced = 0;
        while (cursor.TryGotoNext(i => i.OpCode == OpCodes.Ldc_R4 && Math.Abs((float)i.Operand - 0.5625f) < 0.0001f)) {
            cursor.Remove();
            cursor.EmitDelegate<Func<float>>(() => (float)GameHeight / 320f);
            replaced++;
        }
        cursor.Index = 0;
        // only the ScreenPadding * 9f / 16f pattern: a 9 followed by a multiply by 16, so an
        // unrelated 9 in the same method cannot be rewritten
        while (cursor.TryGotoNext(i => i.OpCode == OpCodes.Ldc_R4 && Math.Abs((float)i.Operand - 9f) < 0.01f)) {
            if (cursor.Next?.Next?.OpCode != OpCodes.Mul) continue;
            if (cursor.Next?.Next?.Next?.OpCode != OpCodes.Ldc_R4) continue;
            if (Math.Abs((float)cursor.Next.Next.Next.Operand - 16f) > 0.01f) continue;
            cursor.Remove();
            cursor.EmitDelegate<Func<float>>(() => (float)GameHeight / 320f * 16f);
            replaced++;
        }
        if (replaced > 0) LogReplacements(il, replaced);
    }

    private static void PatchScreenFadeRects(ILContext il) {
        var cursor = new ILCursor(il);
        int replaced = 0;
        while (cursor.TryGotoNext(i => i.OpCode == OpCodes.Ldc_R4 && IsCoverHeight((float)i.Operand))) {
            float height = (float)cursor.Next!.Operand;
            var width = cursor.Previous;
            if (width == null || width.OpCode != OpCodes.Ldc_R4 || !IsCoverPair((float)width.Operand, height)) continue;
            float extra = ExtraFor(height);
            cursor.Remove();
            cursor.EmitDelegate<Func<float>>(() => GameHeight + extra);
            replaced++;
        }
        if (replaced > 0) LogReplacements(il, replaced);
    }

    /// true when width and height come from the same cover entry, so an unrelated pair in the
    /// same method (there are plenty of 340s and 200s around) can never be rewritten.
    private static bool IsCoverPair(float width, float height) {
        foreach (var cover in ScreenCovers) {
            if (Math.Abs(cover.width - width) < 0.01f && Math.Abs(cover.height - height) < 0.01f) return true;
        }
        return false;
    }

    private static bool IsCoverHeight(float height) {
        foreach (var cover in ScreenCovers) {
            if (Math.Abs(cover.height - height) < 0.01f) return true;
        }
        return false;
    }

    private static float ExtraFor(float height) {
        foreach (var cover in ScreenCovers) {
            if (Math.Abs(cover.height - height) < 0.01f) return cover.extra;
        }
        return 0f;
    }

    /// the same constant shows up as int in some methods and float in others, so the
    /// replacement has to emit whatever type the original instruction pushed.
    private static void ReplaceConstant(ILContext il, float original, Func<float> asFloat, Func<int> asInt) {
        var cursor = new ILCursor(il);
        int replaced = 0;
        while (cursor.TryGotoNext(i => (i.OpCode == OpCodes.Ldc_R4 && Math.Abs((float)i.Operand - original) < 0.01f)
                                    || (i.OpCode == OpCodes.Ldc_I4 && (int)i.Operand == (int)original))) {
            bool isFloat = cursor.Next.OpCode == OpCodes.Ldc_R4;
            cursor.Remove();
            replaced++;
            if (isFloat) {
                cursor.EmitDelegate(asFloat);
            } else {
                cursor.EmitDelegate(asInt);
            }
        }
        if (replaced > 0) LogReplacements(il, replaced);
    }

    private static void PatchCulling(ILContext il) {
        var cursor = new ILCursor(il);
        var topGetter = il.Module.ImportReference(typeof(Camera).GetProperty("Top")!.GetGetMethod()!);
        while (cursor.TryGotoNext(i => i.MatchCallvirt<Camera>("get_Y"))) {
            cursor.Remove();
            cursor.Emit(OpCodes.Callvirt, topGetter);
        }
        // the view rect height (180) also needs to grow to the 4:3 height
        cursor.Index = 0;
        ReplaceConstant(il, 180f, () => (float)GameHeight, () => GameHeight);
    }

    /// the hires buffers the hud, menus and wipes render into are allocated once at a fixed
    /// 1922x1082 and drawn into 1:1, then blitted at native size. on a virtual screen taller than
    /// 1080 that squeezes the whole ui into the top 1080 rows and clips anything drawn below it.
    private static void PatchHudBufferHeight(ILContext il) {
        var cursor = new ILCursor(il);
        while (cursor.TryGotoNext(i => i.OpCode == OpCodes.Ldc_I4 && (int)i.Operand == 1082)) {
            cursor.Remove();
            cursor.EmitDelegate<Func<int>>(UiBufferHeight);
        }
    }

    private static int UiBufferHeight() {
        var pp = Engine.Instance.GraphicsDevice.PresentationParameters;
        return pp.BackBufferWidth > 0 ? pp.BackBufferHeight * 1920 / pp.BackBufferWidth + 2 : Engine.Height + 2;
    }

    private static void PatchConstantsInLowTypes() {
        var lowTypes = new[] {
            typeof(GameplayBuffers), typeof(GameplayRenderer), typeof(Snow),
            typeof(Starfield), typeof(StarsBG), typeof(DreamStars), typeof(HeatWave),
            typeof(StardustFG), typeof(CoreStarsFG), typeof(ReflectionFG), typeof(MirrorFG),
            typeof(NorthernLights), typeof(BlackholeBG), typeof(GlassBlockBg), typeof(FallEffects),
            typeof(StarJumpController), typeof(MirrorSurfaces), typeof(WindSnowFG), typeof(RainFG)
        };

        // these effects bake the 320x180 view into fields inside their constructors, which
        // GetMethods() never enumerates, so their ctors get the same constant swap explicitly
        var ctorTypes = new[] {
            typeof(Starfield), typeof(NorthernLights), typeof(GlassBlockBg), typeof(FallEffects)
        };

        foreach (var type in lowTypes) {
            Logger.Log(LogLevel.Info, "CelesteFit", $"--- low: {type.Name}");
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)) {
                if (method.DeclaringType != type || method.IsAbstract) continue;
                Instance.HookMethod(type, method.Name, PatchLowScreenConstants);
            }
            if (Array.IndexOf(ctorTypes, type) < 0) continue;
            if (type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Length > 0) {
                Instance.HookMethod(type, ".ctor", PatchLowScreenConstants);
            }
            if (type.TypeInitializer != null) {
                Instance.HookMethod(type, ".cctor", PatchLowScreenConstants);
            }
        }
    }

    private static void PatchLowScreenConstants(ILContext il) {
        var cursor = new ILCursor(il);
        int replaced = 0;
        while (cursor.TryGotoNext(i => (i.OpCode == OpCodes.Ldc_I4 && (int)i.Operand == 180)
                                    || (i.OpCode == OpCodes.Ldc_R4 && Math.Abs((float)i.Operand - 180f) < 0.01f))) {
            bool isFloat = cursor.Next.OpCode == OpCodes.Ldc_R4;
            cursor.Remove();
            replaced++;
            if (isFloat) cursor.EmitDelegate<Func<float>>(() => (float)GameHeight);
            else cursor.EmitDelegate<Func<int>>(() => GameHeight);
        }
        if (il.Method.DeclaringType?.FullName == typeof(GameplayBuffers).FullName) {
            cursor.Index = 0;
            while (cursor.TryGotoNext(i => i.OpCode == OpCodes.Ldc_I4 && (int)i.Operand == 244)) {
                cursor.Remove();
                replaced++;
                cursor.EmitDelegate<Func<int>>(() => GameHeight + 64);
            }
        }
        if (replaced > 0) LogReplacements(il, replaced);
    }

    // the device log is the only way to prove a runtime il rewrite actually applied, so every
    // touched method gets one load-time log line listing how many constants were rewritten
    private static void LogReplacements(ILContext il, int count) {
        try {
            Logger.Log(LogLevel.Info, "CelesteFit", $"rewrote {count} screen constants in {il.Method?.FullName ?? "?"}");
        } catch {
            // logging must never break a hook
        }
    }

    private static void PatchConstantsInUITypes() {
        var uiTypes = new Type?[] {
            // hires renderers: hud, vignettes, the title mountain, the level name
            typeof(HiresSnow), typeof(HudRenderer), typeof(HiresRenderer), typeof(MountainModel),
            typeof(GameLoader),
            typeof(AutoSavingNotice), typeof(Textbox), typeof(HeightDisplay),
            typeof(IntroVignette), typeof(CoreVignette), typeof(Poem),
            typeof(Overlay), typeof(BreathingMinigame), typeof(UnlockedPico8Message),
            // menus, save file screens and config screens
            typeof(TextMenu), typeof(OuiTitleScreen), typeof(OuiOptions), typeof(OuiCredits),
            typeof(OuiFileSelect), typeof(OuiFileSelectSlot), typeof(OuiFileNaming),
            typeof(OuiAssistMode), typeof(LanguageSelectUI), typeof(DisconnectedControllerUI),
            typeof(ButtonConfigUI), typeof(KeyboardConfigUI), typeof(PreviewDialog),
            typeof(PreviewPortrait), typeof(Credits), typeof(CS06_BossEnd), typeof(CS07_Credits),
            typeof(CS08_Ending), typeof(CS10_Ending), typeof(Pico8Emulator),
            // wipes cover the whole screen
            typeof(CurtainWipe), typeof(KeyDoorWipe), typeof(AngledWipe), typeof(DropWipe),
            typeof(FallWipe), typeof(StarfieldWipe), typeof(DreamWipe), typeof(WindWipe),
            typeof(MountainWipe),
            // the wave-dash tutorial, chapter end screens and the leftover wipes are full screen too
            typeof(SpotlightWipe), typeof(WaveDashPresentation), typeof(GameplayStats),
            typeof(MemorialText), typeof(PreviewRecording),
            // cutscene text and the photo mode are laid out in the same 1080 space
            typeof(Selfie), typeof(CS00_Ending), typeof(TestWipes),
            // only listed for their nested screen-space overlays
            typeof(Cassette), typeof(Lookout),
            // everest's own menus and overlays
            typeof(OuiGenericMenu), typeof(OuiLoggedProgress), typeof(OuiMapList),
            typeof(OuiMapSearch), typeof(OuiModOptions), typeof(OuiModOptionString),
            typeof(OuiNumberEntry), typeof(OuiOOBE), typeof(OuiSoundTest), typeof(OuiVersionList),
            typeof(AssetReloadHelper), typeof(CustomHeightDisplay), typeof(CustomTextVignette),
            // everest's menu item widgets centre the whole menu on y 540
            FindType("Celeste.TextMenuExt"),
            // the editor's own ui, and everest's mod list (internal, so only reachable by name)
            FindType("Celeste.Editor.MapEditor"), FindType("Celeste.Mod.UI.OuiModUpdateList")
        };

        foreach (var type in uiTypes) {
            if (type == null) continue;
            PatchUIConstantsIn(type);
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) {
                PatchUIConstantsIn(nested);
            }
        }
    }

    private static void PatchUIConstantsIn(Type type) {
        Logger.Log(LogLevel.Info, "CelesteFit", $"--- ui: {type.FullName}");
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)) {
            if (method.DeclaringType != type || method.GetMethodBody() == null) continue;
            if (!MentionsScreenConstant(method)) continue;
            Instance.HookMethod(type, method.Name, PatchUIScreenConstants);
        }
    }

    private static readonly int[] SCREEN_CONSTANTS = { 1080, 1082, 540, 1100 };

    // the 32 bit forms of the same numbers, so a method can be checked before we hook it
    private static readonly int[] SCREEN_CONSTANT_BITS = {
        1080, 1082, 540, 1100,
        BitConverter.ToInt32(BitConverter.GetBytes(1080f), 0),
        BitConverter.ToInt32(BitConverter.GetBytes(1082f), 0),
        BitConverter.ToInt32(BitConverter.GetBytes(540f), 0),
        BitConverter.ToInt32(BitConverter.GetBytes(1100f), 0)
    };

    /// the ui is laid out in a 1920x1080 space, so every hardcoded 1080 based constant has to
    /// follow Engine.Height once the virtual screen is taller than 1080. the same number is an
    /// int in some methods and a float in others, so emit whichever the original pushed.
    private static void PatchUIScreenConstants(ILContext il) {
        var cursor = new ILCursor(il);
        int replaced = 0;
        while (cursor.TryGotoNext(IsScreenConstant)) {
            bool isFloat = cursor.Next.OpCode == OpCodes.Ldc_R4;
            int val = isFloat ? (int)(float)cursor.Next.Operand : (int)cursor.Next.Operand;
            cursor.Remove();
            replaced++;
            if (val == 540) {
                if (isFloat) cursor.EmitDelegate<Func<float>>(() => Engine.Height / 2f);
                else cursor.EmitDelegate<Func<int>>(() => Engine.Height / 2);
            } else if (val == 1080) {
                if (isFloat) cursor.EmitDelegate<Func<float>>(() => (float)Engine.Height);
                else cursor.EmitDelegate<Func<int>>(() => Engine.Height);
            } else if (val == 1082) {
                if (isFloat) cursor.EmitDelegate<Func<float>>(() => Engine.Height + 2f);
                else cursor.EmitDelegate<Func<int>>(() => Engine.Height + 2);
            } else if (val == 1100) {
                // the full screen dimming rects: 1080 plus a 20px bleed
                if (isFloat) cursor.EmitDelegate<Func<float>>(() => Engine.Height + 20f);
                else cursor.EmitDelegate<Func<int>>(() => Engine.Height + 20);
            }
        }
        if (replaced > 0) LogReplacements(il, replaced);
    }

    private static bool IsScreenConstant(Instruction i) {
        if (i.OpCode == OpCodes.Ldc_I4) {
            return Array.IndexOf(SCREEN_CONSTANTS, (int)i.Operand) >= 0;
        }
        if (i.OpCode == OpCodes.Ldc_R4) {
            float v = (float)i.Operand;
            return Math.Abs(v - 1080f) < 0.01f || Math.Abs(v - 1082f) < 0.01f
                || Math.Abs(v - 540f) < 0.01f || Math.Abs(v - 1100f) < 0.01f;
        }
        return false;
    }

    /// the game is full of methods that only look like they contain one of these numbers, and
    /// hooking all of them costs seconds of load time, so check the raw il first.
    private static bool MentionsScreenConstant(MethodBase method) {
        byte[]? bytes;
        try {
            bytes = method.GetMethodBody()?.GetILAsByteArray();
        } catch {
            return true;
        }
        if (bytes == null) return false;
        for (int i = 0; i + 4 < bytes.Length; i++) {
            if (bytes[i] != 0x20 && bytes[i] != 0x22) continue;   // ldc.i4 <int32> / ldc.r4 <float32>
            if (Array.IndexOf(SCREEN_CONSTANT_BITS, BitConverter.ToInt32(bytes, i + 1)) >= 0) return true;
        }
        return false;
    }

    private static void PatchHudBuffers() {
        PatchHudBuffer("Celeste.Celeste", typeof(Engine).Assembly);
        Instance.HookMethod(typeof(CoreModule), "LoadContent", PatchHudBufferHeight);
        Instance.HookMethod(typeof(SceneRenderer), "Alloc", PatchHudBufferHeight);
    }

    private static void PatchHudBuffer(string typeName, Assembly assembly) {
        var type = assembly.GetType(typeName);
        if (type == null) {
            Logger.Log(LogLevel.Warn, "CelesteFit", $"Could not find type {typeName} to hook");
            return;
        }
        Instance.HookMethod(type, "LoadContent", PatchHudBufferHeight);
    }

    private static Type? FindType(string typeName) {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
            var type = assembly.GetType(typeName, false);
            if (type != null) return type;

            // Assembly.GetType misses private nested types (AscendManager+Clouds+Fader), so walk the
            // nesting by hand before giving up
            if (!typeName.Contains('+')) continue;
            var parts = typeName.Split('+');
            var nested = assembly.GetType(parts[0], false);
            for (int i = 1; i < parts.Length && nested != null; i++) {
                nested = nested.GetNestedType(parts[i], BindingFlags.Public | BindingFlags.NonPublic);
            }
            if (nested != null) return nested;
        }
        Logger.Log(LogLevel.Warn, "CelesteFit", $"Could not find type {typeName}");
        return null;
    }

    #endregion
}
