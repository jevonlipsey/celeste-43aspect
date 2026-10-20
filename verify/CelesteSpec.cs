using System.Collections.Generic;

namespace Celeste43.Verify;

/// what the patch touches, shared by the patcher (which applies it) and the verifier (which
/// re-derives it from the output). a vanilla Celeste 1.4.0.0 FNA build matches ExpectedHits
/// exactly; anything else is an unknown build. ported from Source/CelesteFitModule.cs, with the
/// everest-only types left out.
public static class CelesteSpec
{
    public const string HelperType = "Celeste.Fit43";

    // the sentinel types the loader keys on
    public const string EverestType = "Celeste.Mod.Everest";
    public const string StaleType = "CelesteFit.ScreenFit";

    // low types: backdrops and gameplay buffers. every non-abstract method has 180 -> GameHeight
    // (GameplayBuffers also 244 -> GameHeight + 64)
    public static readonly string[] LowTypes =
    {
        "GameplayBuffers", "GameplayRenderer", "Snow",
        "Starfield", "StarsBG", "DreamStars", "HeatWave",
        "StardustFG", "CoreStarsFG", "ReflectionFG", "MirrorFG",
        "NorthernLights", "BlackholeBG", "GlassBlockBg", "FallEffects",
        "StarJumpController", "MirrorSurfaces", "WindSnowFG", "RainFG",
    };

    // these bake the 320x180 view into fields inside their constructors, so the ctors (and a
    // static constructor, if any) get the same swap
    public static readonly string[] CtorTypes =
    {
        "Starfield", "StarsBG", "DreamStars", "BlackholeBG",
        "NorthernLights", "GlassBlockBg", "FallEffects",
    };

    // ui types, swept together with their direct nested types: 1080/1082/540/1100 follow Engine.Height
    public static readonly string[] UiTypes =
    {
        "HiresSnow", "HudRenderer", "HiresRenderer", "MountainModel",
        "GameLoader",
        "AutoSavingNotice", "Textbox", "HeightDisplay",
        "IntroVignette", "CoreVignette", "Poem",
        "Overlay", "BreathingMinigame", "UnlockedPico8Message",
        "TextMenu", "OuiTitleScreen", "OuiOptions", "OuiCredits",
        "OuiFileSelect", "OuiFileSelectSlot", "OuiFileNaming",
        "OuiAssistMode", "LanguageSelectUI", "DisconnectedControllerUI",
        "ButtonConfigUI", "KeyboardConfigUI", "PreviewDialog",
        "PreviewPortrait", "Credits", "CS06_BossEnd", "CS07_Credits",
        "CS08_Ending", "CS10_Ending", "Pico8.Emulator",
        "CurtainWipe", "KeyDoorWipe", "AngledWipe", "DropWipe",
        "FallWipe", "StarfieldWipe", "DreamWipe", "WindWipe",
        "MountainWipe",
        "SpotlightWipe", "WaveDashPresentation", "GameplayStats",
        "MemorialText", "PreviewRecording",
        "Selfie", "CS00_Ending", "TestWipes",
        "Cassette", "Lookout",
        "Editor.MapEditor",
    };

    public static readonly int[] UiConstants = { 1080, 1082, 540, 1100 };

    // full-screen cover rects: width, height, rows the cover is taller than the view
    public static readonly (float width, float height, float extra)[] ScreenCovers =
    {
        (340f, 200f, 20f),
        (26f, 200f, 20f),
        (322f, 182f, 2f),
        (370f, 190f, 10f),
    };

    // methods that hold a cover rect (type, method). nested types use '/'
    public static readonly (string type, string method)[] CoverMethods =
    {
        ("BloomRenderer", "Apply"),
        ("BackdropRenderer", "Render"),
        ("AscendManager", "Render"),
        ("AscendManager/Fader", "Render"),
        ("AscendManager/Streaks", "Render"),
        ("BackgroundFadeIn", "Render"),
        ("SummitGem/BgFlash", "Render"),
        ("CS03_Ending/BgFlash", "Render"),
        ("CS04_MirrorPortal/Fader", "Render"),
        ("CS07_Credits/Fill", "Render"),
        ("CS10_MoonIntro", "Render"),
        ("Level", "Render"),
        ("CS10_Farewell", "Render"),
        ("FormationBackdrop", "Render"),
        ("GondolaDarkness/Blackness", "Render"),
        ("HeatWave", "RenderDisplacement"),
    };

    // (type, method, constant, kind) for the single-constant hooks. all must be gone afterwards.
    public static readonly (string type, string method, float value)[] ConstantHooks =
    {
        ("ScreenWipe", "get_Bottom", 1090f),
        ("KeyDoorWipe", "Render", 1090f),
        ("CurtainWipe", "Render", 1090f),
        ("AngledWipe", "Render", 183.33333f),
        ("FallWipe", "Render", 2160f),
        ("OuiTitleScreen", "Render", 1100f),
        ("Level", "Render", 182f),
        ("Level", "Render", 180f),
        ("LightingRenderer", "BeforeRender", 180f),
        ("FakeWall", "Render", 180f),
        ("ExitBlock", "Render", 180f),
        ("Celeste", "get_TargetCenter", 1080f),
        ("LightningRenderer", "OnRenderBloom", 180f),
        ("Level", "EnforceBounds", 180f),
        ("Level", "ResetZoom", 180f),
        ("HiresRenderer", "get_DrawToBuffer", 1080f),
        ("Level", "IsInCamera", 180f),
        ("Lightning", "InView", 180f),
        ("CrystalStaticSpinner", "InView", 180f),
        ("Parallax", "Render", 180f),
    };

    // the culls compare the camera's view top, not its position
    public static readonly (string type, string method)[] CullMethods =
    {
        ("Level", "IsInCamera"), ("Lightning", "InView"), ("CrystalStaticSpinner", "InView"),
    };

    // methods hooked for the Fit43 calls below that the sweeps above do not cover
    public const string UpdateViewType = "Monocle.Engine";

    // Fit43 call count per "Type::method" (aggregated over overloads) in a vanilla Celeste 1.4.0.0
    // FNA build. one replacement is one call.
    public static readonly Dictionary<string, int> ExpectedHits = new()
    {
        ["Celeste.AngledWipe::Render"] = 2,
        ["Celeste.AscendManager/Fader::Render"] = 1,
        ["Celeste.AscendManager/Streaks::Render"] = 2,
        ["Celeste.AscendManager::Render"] = 1,
        ["Celeste.AutoSavingNotice::Render"] = 1,
        ["Celeste.BackdropRenderer::Render"] = 1,
        ["Celeste.BackgroundFadeIn::Render"] = 1,
        ["Celeste.BlackholeBG::.ctor"] = 1,
        ["Celeste.BlackholeBG::BeforeRender"] = 1,
        ["Celeste.BlackholeBG::Update"] = 1,
        ["Celeste.BloomRenderer::Apply"] = 1,
        ["Celeste.BreathingMinigame/Particle::Reset"] = 1,
        ["Celeste.BreathingMinigame::BeforeRender"] = 1,
        ["Celeste.BreathingMinigame::Render"] = 1,
        ["Celeste.BreathingMinigame::get_screenCenter"] = 1,
        ["Celeste.ButtonConfigUI::Render"] = 5,
        ["Celeste.CS00_Ending/<Cutscene>d__7::MoveNext"] = 2,
        ["Celeste.CS00_Ending::OnEnd"] = 1,
        ["Celeste.CS03_Ending/BgFlash::Render"] = 1,
        ["Celeste.CS04_MirrorPortal/Fader::Render"] = 1,
        ["Celeste.CS06_BossEnd::Render"] = 2,
        ["Celeste.CS07_Credits/Fill::Render"] = 1,
        ["Celeste.CS07_Credits::Render"] = 3,
        ["Celeste.CS08_Ending::Render"] = 1,
        ["Celeste.CS10_Ending::Render"] = 3,
        ["Celeste.CS10_Farewell::Render"] = 1,
        ["Celeste.CS10_MoonIntro::Render"] = 1,
        ["Celeste.Cassette/UnlockedBSide::Render"] = 1,
        ["Celeste.Celeste::LoadContent"] = 2,
        ["Celeste.Celeste::get_TargetCenter"] = 1,
        ["Celeste.CoreStarsFG::Render"] = 1,
        ["Celeste.CoreStarsFG::Reset"] = 1,
        ["Celeste.CoreVignette::Render"] = 2,
        ["Celeste.Credits/Ending::Height"] = 1,
        ["Celeste.Credits/Ending::Render"] = 1,
        ["Celeste.Credits::CreateCredits"] = 1,
        ["Celeste.Credits::Render"] = 3,
        ["Celeste.CrystalStaticSpinner::InView"] = 1,
        ["Celeste.CurtainWipe::Render"] = 5,
        ["Celeste.DisconnectedControllerUI::Render"] = 1,
        ["Celeste.DreamStars::.ctor"] = 1,
        ["Celeste.DreamStars::Render"] = 1,
        ["Celeste.DropWipe::Render"] = 3,
        ["Celeste.DustEdges::BeforeRender"] = 3,
        ["Celeste.DustEdges::Render"] = 1,
        ["Celeste.Editor.MapEditor::Render"] = 2,
        ["Celeste.ExitBlock::Render"] = 1,
        ["Celeste.FakeWall::Render"] = 1,
        ["Celeste.FallEffects::.ctor"] = 1,
        ["Celeste.FallWipe::Render"] = 12,
        ["Celeste.FormationBackdrop::Render"] = 1,
        ["Celeste.GameLoader/<IntroRoutine>d__16::MoveNext"] = 1,
        ["Celeste.GameplayBuffers::Create"] = 9,
        ["Celeste.GameplayRenderer::.ctor"] = 1,
        ["Celeste.GlassBlockBg::.ctor"] = 2,
        ["Celeste.GlassBlockBg::BeforeRender"] = 3,
        ["Celeste.GondolaDarkness/Blackness::Render"] = 1,
        ["Celeste.HeatWave::Render"] = 1,
        ["Celeste.HeatWave::RenderDisplacement"] = 1,
        ["Celeste.HeatWave::Reset"] = 1,
        ["Celeste.HeightDisplay::Render"] = 1,
        ["Celeste.HiresRenderer::get_DrawToBuffer"] = 1,
        ["Celeste.HiresSnow::Render"] = 1,
        ["Celeste.HudRenderer::RenderContent"] = 1,
        ["Celeste.IntroVignette::Render"] = 2,
        ["Celeste.KeyDoorWipe::Render"] = 2,
        ["Celeste.KeyboardConfigUI::Render"] = 4,
        ["Celeste.LanguageSelectUI::Render"] = 1,
        ["Celeste.Level::EnforceBounds"] = 1,
        ["Celeste.Level::IsInCamera"] = 1,
        ["Celeste.Level::Render"] = 3,
        ["Celeste.Level::ResetZoom"] = 1,
        ["Celeste.LightingRenderer::BeforeRender"] = 1,
        ["Celeste.Lightning::InView"] = 1,
        ["Celeste.LightningRenderer::OnRenderBloom"] = 1,
        ["Celeste.Lookout/Hud::Render"] = 8,
        ["Celeste.MirrorFG::Render"] = 1,
        ["Celeste.MirrorFG::Reset"] = 1,
        ["Celeste.MirrorSurfaces::BeforeRender"] = 1,
        ["Celeste.MountainModel::ResetRenderTargets"] = 1,
        ["Celeste.MountainRenderer::Render"] = 1,
        ["Celeste.MountainWipe::Render"] = 1,
        ["Celeste.NorthernLights::.ctor"] = 4,
        ["Celeste.NorthernLights::BeforeRender"] = 2,
        ["Celeste.OuiAssistMode::Render"] = 2,
        ["Celeste.OuiCredits::Render"] = 1,
        ["Celeste.OuiFileNaming/<Leave>d__51::MoveNext"] = 1,
        ["Celeste.OuiFileNaming::Render"] = 1,
        ["Celeste.OuiFileSelectSlot::HiddenPosition"] = 3,
        ["Celeste.OuiFileSelectSlot::Render"] = 3,
        ["Celeste.OuiFileSelectSlot::get_IdlePosition"] = 1,
        ["Celeste.OuiOptions::Render"] = 1,
        ["Celeste.OuiTitleScreen::Render"] = 2,
        ["Celeste.Overlay::RenderFade"] = 1,
        ["Celeste.Parallax::Render"] = 3,
        ["Celeste.Pico8.Emulator::Render"] = 2,
        ["Celeste.Poem::BeforeRender"] = 1,
        ["Celeste.Poem::DrawPoem"] = 1,
        ["Celeste.PreviewDialog::RenderContent"] = 1,
        ["Celeste.PreviewPortrait::Render"] = 2,
        ["Celeste.QuickResetHint::Render"] = 1,
        ["Celeste.ReflectionFG::Render"] = 1,
        ["Celeste.ReflectionFG::Reset"] = 1,
        ["Celeste.ReturnMapHint::Render"] = 2,
        ["Celeste.SaveLoadIcon::Render"] = 1,
        ["Celeste.ScreenWipe::get_Bottom"] = 1,
        ["Celeste.Selfie/<EndRoutine>d__11::MoveNext"] = 1,
        ["Celeste.Selfie/<OpenRoutine>d__9::MoveNext"] = 2,
        ["Celeste.Snow::Render"] = 1,
        ["Celeste.StarJumpController::BeforeRender"] = 1,
        ["Celeste.StardustFG::Render"] = 1,
        ["Celeste.StardustFG::Reset"] = 1,
        ["Celeste.Starfield::.ctor"] = 1,
        ["Celeste.StarfieldWipe::BeforeRender"] = 3,
        ["Celeste.StarfieldWipe::Render"] = 1,
        ["Celeste.StarsBG::.ctor"] = 1,
        ["Celeste.StarsBG::Render"] = 3,
        ["Celeste.SummitGem/BgFlash::Render"] = 1,
        ["Celeste.TestWipes::Render"] = 1,
        ["Celeste.TextMenu::Added"] = 1,
        ["Celeste.TextMenu::Update"] = 1,
        ["Celeste.Textbox::Render"] = 1,
        ["Celeste.UnlockedPico8Message::Render"] = 1,
        ["Celeste.WaveDashPresentation::Render"] = 1,
        ["Monocle.Engine::UpdateView"] = 1,
    };
}
