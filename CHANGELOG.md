# Changelog

## 1.0.1

Everything found after 1.0.0 shipped: the seam in the ascend scenes, backdrops and overlays that stopped short of the bottom edge, and a constant pass that was silently doing nothing.

- **The seam in the ascend scenes.** The level camera's *origin* was shifted down by the amount the view grew. Everything drawn in world space — the fader, the streak strips, the cloud wash — moved down with the camera, while the parallax sky behind it is drawn in screen space and stayed put. The visible result was an unpainted band at the top of the screen during `AscendManager` scenes (the start of Summit being the worst of them). The origin is back at zero, so buffer row 0 is the top of the view and the extra rows fall below it; the compensating offset on the zoom focus points went with it.
- **Backdrops that stopped short.** Chapter backdrops are authored to be painted exactly once (`LoopY = false`), so the tiler draws a single 180-tall tile and no loop bound can ever fill a 240-tall view. That one tile is now stretched to the full height, keeping the authored top edge, instead of leaving a band below it.
- **Screen covers, bloom and wipes.** The 16:9 code covers the screen with rects a little larger than the view on every side. Those rects are now grown for the taller view — most visibly the bloom base fill, which is the brightness the whole screen is lifted by, so an unpainted bottom quarter reads as a dark band. The strips that fade the screen edge (`AscendManager` streaks, `Fader`, `BgFlash`, credits, moon intro, heat wave) are matched by shape against the shipped assembly, and private nested types are found by their `+`-spelled names.
- **The pause menu** re-anchors its items to the real screen bottom.
- **Star and particle fields.** `GetMethod(".ctor")` never matches a constructor, so the low-type constructor pass hooked nothing at all and logged a warning per type. Stars, dust, the aurora, glass-block and fall effects were still spawning inside the top 180 rows of a 240-row view, and `BlackholeBG`'s centre sat 30px above the middle of the screen. Constructors are now looked up as `ConstructorInfo`, and every type whose constructor holds a design-height `180` is patched (verified against the game assembly).

## 1.0.0

First release: the 320×240 gameplay buffer, the camera viewport and anchoring, the culling and clipping that goes with it, the hires UI buffers, wipes and the full-screen covers, and the Parallax, dust-edge and title-vignette backdrops.
