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

        // 2. Hook Level.ZoomSnap, ZoomTo, ZoomAcross
        Step("level zoom");
        HookMethod(typeof(Level), "ZoomSnap", PatchZoom);
        HookMethod(typeof(Level), "ZoomTo", PatchZoom);
        HookMethod(typeof(Level), "ZoomAcross", PatchZoom);

        // 3. Hook GameplayRenderer constructor (camera origin offset)
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
        HookMethod(typeof(Parallax), "Render", PatchParallaxRender);

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

    private static void PatchZoom(ILContext il) {
        var cursor = new ILCursor(il);
        var yField = typeof(Vector2).GetField("Y")!;
        cursor.Emit(OpCodes.Ldarga_S, (byte)1);
        cursor.Emit(OpCodes.Dup);
        cursor.Emit(OpCodes.Ldfld, yField);
        cursor.EmitDelegate<Func<float>>(() => CameraPadding);
        cursor.Emit(OpCodes.Add);
        cursor.Emit(OpCodes.Stfld, yField);
    }

    // the level camera *is* this camera (LevelLoader assigns Level.Camera = GameplayRenderer.Camera),
    // and the world is drawn into GameplayBuffers.Gameplay. that buffer grows to GameHeight, so the
    // camera viewport has to grow with it: everything that clips against Camera.Top/Bottom uses it as
    // the view size, and a 180 view culls every tile below the 180px line out of the 240px screen.
    private static void PatchGameplayRendererCtor(ILContext il) {
        var cursor = new ILCursor(il);
        while (cursor.TryGotoNext(i => (i.OpCode == OpCodes.Ldc_I4 && (int)i.Operand == 180)
                                    || (i.OpCode == OpCodes.Ldc_R4 && Math.Abs((float)i.Operand - 180f) < 0.01f))) {
            bool isFloat = cursor.Next.OpCode == OpCodes.Ldc_R4;
            cursor.Remove();
            if (isFloat) cursor.EmitDelegate<Func<float>>(() => (float)GameHeight);
            else cursor.EmitDelegate<Func<int>>(() => GameHeight);
        }

        while (cursor.TryGotoNext(MoveType.Before, i => i.OpCode == OpCodes.Ret)) {
            cursor.Emit(OpCodes.Ldarg_0);
            cursor.EmitDelegate<Action<GameplayRenderer>>(renderer => {
                renderer.Camera.Origin = new Vector2(0f, CameraPadding);
            });
            cursor.Index++;
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
                    $"camera y {cam.Top:F0}..{cam.Bottom:F0} (= {(cam.Bottom - cam.Top) * cam.Zoom:F0}px at zoom {cam.Zoom:F1})");
            });
            cursor.Index++;
        }
    }

    private static void PatchParallaxRender(ILContext il) {
        var cursor = new ILCursor(il);
        if (cursor.TryGotoNext(MoveType.After, i => i.OpCode == OpCodes.Stloc_1)) {
            cursor.Emit(OpCodes.Ldloca_S, (byte)1);
            var yField = typeof(Vector2).GetField("Y")!;
            cursor.Emit(OpCodes.Dup);
            cursor.Emit(OpCodes.Ldfld, yField);
            cursor.EmitDelegate<Func<float>>(() => -CameraPadding);
            cursor.Emit(OpCodes.Add);
            cursor.Emit(OpCodes.Stfld, yField);
        }

        cursor.Index = 0;
        while (cursor.TryGotoNext(i => i.MatchLdfld<Backdrop>("LoopY"))) {
            cursor.Remove();
            cursor.EmitDelegate<Func<Backdrop, bool>>(backdrop => {
                if (GameHeight > 180 && backdrop is Parallax p) {
                    var path = p.Texture?.AtlasPath;
                    if (path == "bgs/03/bg2" || path == "bgs/03/bg2b" || path == "bgs/03/bg3" || path == "bgs/03/bg3b") {
                        return false;
                    }
                }
                return backdrop.LoopY;
            });
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
            // only listed for their nested screen-space overlays
            typeof(Cassette), typeof(Lookout),
            // everest's own menus and overlays
            typeof(OuiGenericMenu), typeof(OuiLoggedProgress), typeof(OuiMapList),
            typeof(OuiMapSearch), typeof(OuiModOptions), typeof(OuiModOptionString),
            typeof(OuiNumberEntry), typeof(OuiOOBE), typeof(OuiSoundTest), typeof(OuiVersionList),
            typeof(AssetReloadHelper), typeof(CustomHeightDisplay), typeof(CustomTextVignette),
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
        }
        Logger.Log(LogLevel.Warn, "CelesteFit", $"Could not find type {typeName}");
        return null;
    }

    #endregion
}
