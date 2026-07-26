using System;
using Menu.Remix.MixedUI;
using UnityEngine;

namespace TrueResolution
{
    /// <summary>
    /// The mod's page in the in-game Remix config menu (main menu -> Remix -> True Resolution -> cog).
    ///
    /// One slider, because there is only one decision a player might want to make: how much to render.
    /// Its default (0 = automatic) picks the smallest integer scale that covers the displayed picture,
    /// so most people never touch even that. Everything else has one correct answer and is decided
    /// automatically; the config file keeps troubleshooting overrides.
    ///
    /// A second control appears only where it means something: on a non-16:9 display, where the player
    /// genuinely has to choose between black bars and a stretched picture.
    ///
    /// The BepInEx config file stays the storage. This page seeds from it on open and writes back on
    /// Apply, so hand-editing the .cfg and using this page cannot disagree.
    /// </summary>
    internal class TrueResolutionOptions : OptionInterface
    {
        internal readonly Configurable<int> quality;
        internal readonly Configurable<bool> stretchToFill;

        private OpLabel qualityValue;
        private OpLabel status;

        internal TrueResolutionOptions()
        {
            quality = config.Bind(
                "RenderQuality", 0,
                new ConfigurableInfo(
                    "How much detail to render. Auto (the default) picks the cheapest clean setting "
                    + "for your display and is the right choice for almost everyone. Higher fixed "
                    + "values render even denser pixels at a cost that grows with the square of the "
                    + "number - the image gets slightly crisper and steadier in motion. Room "
                    + "backgrounds are fixed artwork and never gain detail.",
                    new ConfigAcceptableRange<int>(0, 8)));

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
                                     "Works out of the box. Changes apply immediately.",
                                     FLabelAlignment.Left));

            // ---- Render quality
            y -= 60f;
            OpSlider slider = new OpSlider(quality, new Vector2(ctrlX, y), 160)
            {
                description = quality.info.description
            };
            qualityValue = new OpLabel(new Vector2(ctrlX + 172f, y), new Vector2(200f, 24f), "",
                                       FLabelAlignment.Left);
            tab.AddItems(Row(labelX, y, "Render quality"), slider, qualityValue);

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
                if (v == 0)
                    qualityValue.text = s != null && s.renderTexture != null
                        ? $"auto  ({s.renderTexture.width}x{s.renderTexture.height})"
                        : "auto";
                else
                {
                    string res = s != null && s.pixelWidth > 0
                        ? $"  ({s.pixelWidth * v}x{s.pixelHeight * v})"
                        : "";
                    qualityValue.text = v + "x" + res;
                }
            }

            if (status == null) return;
            if (s == null || s.renderTexture == null)
            {
                status.text = "not rendering yet";
                return;
            }

            status.text = $"rendering {s.renderTexture.width}x{s.renderTexture.height}"
                          + $"  ->  your screen {Screen.width}x{Screen.height}";
        }

        // ---------------------------------------------------------------- sync

        private void SeedFromPlugin()
        {
            try
            {
                quality.Value = Plugin.CurrentQuality;
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
                // Unticking must not stamp on a deliberate AspectBackbuffer choice made in the config
                // file, so only override when the checkbox actually disagrees.
                AspectMode am = Plugin.CurrentAspect;
                if (stretchToFill.Value) am = AspectMode.Stretch;
                else if (am == AspectMode.Stretch) am = AspectMode.Letterbox;

                Plugin.ApplyFromOptions(quality.Value, am);
                Refresh();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("options: applying settings failed: " + e);
            }
        }
    }
}
