using System;
using Menu.Remix.MixedUI;
using UnityEngine;

namespace TrueResolution
{
    /// <summary>
    /// The mod's page in the in-game Remix config menu (main menu -> Remix -> True Resolution -> cog).
    ///
    /// Two controls, because there are only two decisions a player actually has to make: how much to
    /// render, and whether they want the default hard pixels or a smoothed image. Everything else either has one correct
    /// answer (present at the display's real resolution) or can be worked out from the numbers (which
    /// filter to use), so it is decided automatically and left in the config file as an escape hatch.
    ///
    /// A third control appears only where it means something: on a non-16:9 display, where the player
    /// genuinely has to choose between black bars and a stretched picture.
    ///
    /// The BepInEx config file stays the storage. This page seeds from it on open and writes back on
    /// Apply, so hand-editing the .cfg and using this page cannot disagree.
    /// </summary>
    internal class TrueResolutionOptions : OptionInterface
    {
        internal readonly Configurable<int> quality;
        internal readonly Configurable<bool> smoothScaling;
        internal readonly Configurable<bool> stretchToFill;

        private OpLabel qualityValue;
        private OpLabel status;

        internal TrueResolutionOptions()
        {
            quality = config.Bind(
                "Supersample", 2,
                new ConfigurableInfo(
                    "How much detail to render. 2 is a good default and 1 still keeps the rest of the "
                    + "mod.\n"
                    + "With Smooth scaling OFF, higher values genuinely keep helping: the room artwork "
                    + "is magnified inside the engine with hard pixels instead of being stretched by "
                    + "your monitor, and a denser render places every pixel edge more precisely, so the "
                    + "image is crisper and stays steadier as the camera pans. Raise it until the "
                    + "framerate stops being comfortable.\n"
                    + "With Smooth scaling ON the returns fade much sooner, because filtering averages "
                    + "that precision away again.",
                    new ConfigAcceptableRange<int>(1, 8)));

            smoothScaling = config.Bind(
                "SmoothScaling", false,
                new ConfigurableInfo(
                    "Off (default) keeps hard pixel edges, which suits Rain World's pixel art and is "
                    + "what most people prefer. Turn it on for a smoothed, filtered image instead - the "
                    + "best filter for your resolution is then chosen automatically. With it off, keep "
                    + "Render quality near 2: that lands the render close to your screen's resolution, "
                    + "and much higher values discard most of the extra detail and shimmer in motion."));

            stretchToFill = config.Bind(
                "StretchToFill", false,
                new ConfigurableInfo(
                    "Only matters if your screen is not 16:9. Off keeps the picture the correct shape "
                    + "with black bars at the sides. On stretches it to fill the screen, which distorts "
                    + "it - Rain World cannot actually show more of the room."));

            OnConfigChanged += ApplyToPlugin;
        }

        // ---------------------------------------------------------------- UI

        public override void Initialize()
        {
            base.Initialize();
            SeedFromPlugin();

            OpTab tab = new OpTab(this, "Options");
            Tabs = new[] { tab };

            const float labelX = 40f;
            const float ctrlX = 300f;
            float y = 540f;

            tab.AddItems(new OpLabel(new Vector2(labelX, y), new Vector2(520f, 34f), "True Resolution",
                                     FLabelAlignment.Left, bigText: true));

            y -= 30f;
            tab.AddItems(new OpLabel(new Vector2(labelX, y), new Vector2(520f, 20f),
                                     "Changes apply immediately. Hover a setting for details.",
                                     FLabelAlignment.Left));

            // ---- Render quality
            y -= 60f;
            OpSlider slider = new OpSlider(quality, new Vector2(ctrlX, y), 160)
            {
                description = quality.info.description
            };
            qualityValue = new OpLabel(new Vector2(ctrlX + 172f, y), new Vector2(180f, 24f), "",
                                       FLabelAlignment.Left);
            tab.AddItems(Row(labelX, y, "Render quality"), slider, qualityValue);

            // ---- Sharp pixels
            y -= 50f;
            tab.AddItems(Row(labelX, y, "Smooth scaling"),
                         new OpCheckBox(smoothScaling, new Vector2(ctrlX, y))
                         {
                             description = smoothScaling.info.description
                         });

            // Only offer the aspect choice where it changes anything. On a 16:9 screen both settings look
            // identical, and a control that visibly does nothing is worse than no control.
            if (IsNon16By9())
            {
                y -= 50f;
                tab.AddItems(Row(labelX, y, "Stretch to fill screen"),
                             new OpCheckBox(stretchToFill, new Vector2(ctrlX, y))
                             {
                                 description = stretchToFill.info.description
                             });
            }

            // ---- live status
            y -= 70f;
            status = new OpLabel(new Vector2(labelX, y), new Vector2(520f, 20f), "", FLabelAlignment.Left);
            tab.AddItems(status);

            y -= 60f;
            tab.AddItems(new OpLabelLong(new Vector2(labelX, y), new Vector2(520f, 60f),
                "Everything else is automatic. Troubleshooting overrides live in "
                + "BepInEx/config/steinkoloss.trueresolution.cfg."));

            Refresh();
        }

        private static bool IsNon16By9()
        {
            if (Screen.width <= 0 || Screen.height <= 0) return false;
            float aspect = (float)Screen.width / Screen.height;
            return Mathf.Abs(aspect - 16f / 9f) > 0.02f;
        }

        private static OpLabel Row(float x, float y, string text)
        {
            return new OpLabel(new Vector2(x, y), new Vector2(250f, 24f), text, FLabelAlignment.Left);
        }

        public override void Update()
        {
            base.Update();
            Refresh();
        }

        private void Refresh()
        {
            FScreen s = Futile.screen;

            if (qualityValue != null)
            {
                int v = quality.Value;
                // Show the resolution the slider actually buys, so the number means something.
                string res = s != null && s.pixelWidth > 0
                    ? $"  ({s.pixelWidth * v}x{s.pixelHeight * v})"
                    : "";
                qualityValue.text = (v <= 1 ? "off" : v + "x") + res;
            }

            if (status == null) return;
            if (s == null || s.renderTexture == null)
            {
                status.text = "not rendering yet";
                return;
            }

            status.text = $"rendering {s.renderTexture.width}x{s.renderTexture.height}"
                          + $"  ->  your screen {Screen.width}x{Screen.height}"
                          + $"  ({s.renderTexture.filterMode})";
        }

        // ---------------------------------------------------------------- sync

        private void SeedFromPlugin()
        {
            try
            {
                quality.Value = Plugin.CurrentSupersample;
                smoothScaling.Value = Plugin.CurrentDownsample != DownsampleMode.Point;
                stretchToFill.Value = Plugin.CurrentAspect == AspectMode.Stretch;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("options: could not seed from the live config: " + e);
            }
        }

        private void ApplyToPlugin()
        {
            try
            {
                // Unchecking these must not stamp on a deliberate MipmapBox/Bilinear or AspectBackbuffer
                // choice made in the config file, so only override when the checkbox actually disagrees.
                DownsampleMode ds = Plugin.CurrentDownsample;
                if (!smoothScaling.Value) ds = DownsampleMode.Point;
                else if (ds == DownsampleMode.Point) ds = DownsampleMode.Auto;

                AspectMode am = Plugin.CurrentAspect;
                if (stretchToFill.Value) am = AspectMode.Stretch;
                else if (am == AspectMode.Stretch) am = AspectMode.Letterbox;

                Plugin.ApplyFromOptions(quality.Value, Plugin.CurrentNativeBackbuffer, ds, am);
                Refresh();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("options: applying settings failed: " + e);
            }
        }
    }
}
