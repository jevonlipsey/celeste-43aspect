using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Monocle;

// runtime half of the 4:3 patch. the patcher clones this type into Celeste.exe as Celeste.Fit43
// and points the rewritten game methods at it, so nothing here may touch another assembly
// besides mscorlib, FNA and the game itself. no linq, no lambdas, no generics over game types:
// the cloner maps exactly those. ported from Source/CelesteFitModule.cs.
namespace Celeste
{
    public static class Fit43
    {
        // Engine.Height has a private setter and Engine.ViewPadding's setter calls UpdateView, so
        // setting it from inside UpdateView recurses forever. reach the setter and the field.
        static MethodInfo setHeight;
        static FieldInfo viewPaddingField;
        static bool reflected;

        // rendering is single threaded, so the current backdrop can live in statics
        static Parallax currentParallax;
        static bool parallaxLoopY;
        // the authored y of each parallax layer, captured the first time it renders. keyed as
        // object (reference equality) so the dictionary needs no game type
        static readonly Dictionary<object, float> authoredTop = new Dictionary<object, float>();

        // the view height in world units: 1440 / 6 = 240 on a 4:3 screen, 180 on 16:9
        public static int GameHeight
        {
            get { return (Engine.Height + 5) / 6; }
        }

        public static int ScreenHeight
        {
            get { return Engine.Height; }
        }

        public static float CameraPadding
        {
            get { return (float)(GameHeight - 180); }
        }

        // head of Engine.UpdateView: size the virtual screen from the real backbuffer
        public static void UpdateViewPrefix(Engine engine)
        {
            try
            {
                if (!reflected)
                {
                    reflected = true;
                    PropertyInfo height = typeof(Engine).GetProperty("Height", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (height != null) setHeight = height.GetSetMethod(true);
                    viewPaddingField = typeof(Engine).GetField("viewPadding", BindingFlags.NonPublic | BindingFlags.Static);
                }
                PresentationParameters pp = engine.GraphicsDevice.PresentationParameters;
                if (pp.BackBufferWidth > 0 && setHeight != null)
                    setHeight.Invoke(null, new object[] { pp.BackBufferHeight * 1920 / pp.BackBufferWidth });
                if (viewPaddingField != null) viewPaddingField.SetValue(null, 0);
            }
            catch (Exception)
            {
                // never take the game down over the display fit
            }
        }

        // the hires ui buffers follow the measured backbuffer, not Engine.Height: LoadContent can
        // run before the first UpdateView, when the height is still 1080
        public static int UIHeightFromBackbuffer()
        {
            PresentationParameters pp = Engine.Instance.GraphicsDevice.PresentationParameters;
            return pp.BackBufferWidth > 0 ? pp.BackBufferHeight * 1920 / pp.BackBufferWidth + 2 : Engine.Height + 2;
        }

        // bottom anchored hud: keep the distance from the bottom instead of from the top
        public static float BottomAnchor(float fromBottom)
        {
            return Engine.Height - fromBottom;
        }

        // head of Parallax.Render: remember the layer for the draw below
        public static void SetParallax(Parallax p)
        {
            currentParallax = p;
            parallaxLoopY = p.LoopY;
        }

        // replaces the MTexture.Draw in Parallax.Render. a layer authored to be painted once
        // (LoopY = false) only covers its own 180 rows; stretch it to the bottom of the view when
        // it was authored to reach the bottom of a 16:9 view and no longer reaches a 4:3 one.
        public static void ParallaxDraw(MTexture tex, Vector2 pos, Vector2 origin, Color color, float scale, float rotation, SpriteEffects flip)
        {
            float top = pos.Y;
            float authoredBottom = AuthoredTop(currentParallax, top) + tex.Height;
            int gh = GameHeight;
            if (!parallaxLoopY && tex.Height > 0 && authoredBottom >= 180f && authoredBottom < gh)
            {
                float scaleY = (gh - top) / tex.Height;
                tex.Draw(new Vector2(pos.X, top), origin, color, new Vector2(1f, scaleY), rotation, flip);
            }
            else
            {
                tex.Draw(pos, origin, color, scale, rotation, flip);
            }
        }

        // Parallax.Update adds drift to Position, so the first value seen is the authored one
        static float AuthoredTop(Parallax parallax, float fallback)
        {
            if (parallax == null) return fallback;
            float cached;
            if (authoredTop.TryGetValue(parallax, out cached)) return cached;
            if (authoredTop.Count > 512) authoredTop.Clear();
            authoredTop[parallax] = parallax.Position.Y;
            return parallax.Position.Y;
        }

        // replaces the title vignette's MTexture.Draw: stretch it over the whole screen
        public static void MountainDraw(MTexture texture, Vector2 pos, Vector2 origin, Color color)
        {
            Vector2 scale = new Vector2((float)Engine.Width / texture.Width, (float)Engine.Height / texture.Height);
            texture.Draw(pos, origin, color, scale);
        }

        // stands in for DustEdges.FlooredCamera in Render. consumes the `this` the call already had
        public static Vector2 DustEdgesCamera(DustEdges dust)
        {
            Level level = dust.Scene as Level;
            if (level != null) return level.Camera.ScreenToCamera(Vector2.Zero);
            return Vector2.Zero;
        }
    }
}
