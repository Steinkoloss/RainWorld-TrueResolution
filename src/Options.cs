using System;
using Menu.Remix.MixedUI;
using UnityEngine;

namespace TrueResolution
{
    /// <summary>
    /// The mod's page in the in-game Remix config menu (main menu -> Remix -> True Resolution ->
    /// the cog icon). Deliberately plain: one tab, one control per setting, a live status line so you can
    /// see what you actually got, and nothing else.
    ///
    /// The BepInEx config file stays the single source of truth for persistence. This page seeds its
    /// controls from it every time the menu opens, and writes back to it on Apply, so editing the .cfg by
    /// hand and using this page can never disagree. Remix's own saved copy of these values is ignored.
    /// </summary>
    internal class TrueResolutionOptions : OptionInterface
    {
        // Remix's combo boxes bind to Configurable<string>, not to an enum, so enum settings are stored
        // by name and parsed back with Enum.TryParse.
        private static readonly string[] DownsampleNames =
            Enum.GetNames(typeof(DownsampleMode));
        private static readonly string[] AspectNames =
            Enum.GetNames(typeof(AspectMode));

        internal readonly Configurable<int> supersample;
        internal readonly Configurable<bool> nativeBackbuffer;
        internal readonly Configurable<string> downsample;
        internal readonly Configurable<string> aspect;

        private OpLabel scaleValue;
        private OpLabel status;

        internal TrueResolutionOptions()
        {
            supersample = config.Bind(
                "Supersample", 2,
                new ConfigurableInfo(
                    "How many times the game's internal buffer to render at. 2 is the sweet spot and is "
                    + "already larger than most screens; past that you are only buying anti-aliasing. "
                    + "Cost grows with the square of this. Set 1 on a weak GPU - you keep the native "
                    + "backbuffer, which is the bigger improvement anyway.",
                    new ConfigAcceptableRange<int>(1, 8)));

            downsample = config.Bind(
                "Downsample", DownsampleMode.Auto.ToString(),
                new ConfigurableInfo(
                    "Filter used to bring the supersampled image down to your screen. Auto is right for "
                    + "almost everyone: it uses a mipmap box pyramid when that helps and plain bilinear "
                    + "when it would not. Point gives a hard pixelated look and aliases badly."));

            aspect = config.Bind(
                "AspectMode", AspectMode.Letterbox.ToString(),
                new ConfigurableInfo(
                    "How the game's ~16:9 picture is fitted to your display. Letterbox keeps it "
                    + "undistorted with black bars and is a no-op on a 16:9 screen. Stretch is vanilla "
                    + "behaviour and distorts on anything else."));

            nativeBackbuffer = config.Bind(
                "NativeBackbuffer", true,
                new ConfigurableInfo(
                    "Present at your display's real resolution in fullscreen instead of letting the game "
                    + "shrink the window to its internal buffer size. Costs almost nothing and is the "
                    + "single biggest visual improvement. There is no good reason to turn this off."));

            OnConfigChanged += ApplyToPlugin;
        }

        // ---------------------------------------------------------------- UI

        public override void Initialize()
        {
            base.Initialize();

            // Show what is actually in force, not whatever Remix last persisted.
            SeedFromPlugin();

            OpTab tab = new OpTab(this, "Options");
            Tabs = new[] { tab };

            const float labelX = 40f;
            const float ctrlX = 300f;
            float y = 540f;

            tab.AddItems(
                new OpLabel(new Vector2(labelX, y), new Vector2(520f, 34f), "True Resolution",
                            FLabelAlignment.Left, bigText: true));

            y -= 30f;
            tab.AddItems(
                new OpLabel(new Vector2(labelX, y), new Vector2(520f, 20f),
                            "Changes apply immediately. Hover a setting for details.",
                            FLabelAlignment.Left));

            // ---- Supersample
            y -= 56f;
            OpSlider slider = new OpSlider(supersample, new Vector2(ctrlX, y), 160)
            {
                description = supersample.info.description
            };
            scaleValue = new OpLabel(new Vector2(ctrlX + 172f, y), new Vector2(60f, 24f), "",
                                     FLabelAlignment.Left);
            tab.AddItems(Row(labelX, y, "Supersample"), slider, scaleValue);

            // ---- Downsample filter
            y -= 46f;
            OpComboBox dsBox = new OpComboBox(downsample, new Vector2(ctrlX, y), 160f, DownsampleNames)
            {
                description = downsample.info.description
            };
            tab.AddItems(Row(labelX, y, "Downsample filter"), dsBox);

            // ---- Aspect fit
            y -= 46f;
            OpComboBox aspectBox = new OpComboBox(aspect, new Vector2(ctrlX, y), 160f, AspectNames)
            {
                description = aspect.info.description
            };
            tab.AddItems(Row(labelX, y, "Aspect fit"), aspectBox);

            // ---- Native backbuffer
            y -= 46f;
            OpCheckBox check = new OpCheckBox(nativeBackbuffer, new Vector2(ctrlX, y))
            {
                description = nativeBackbuffer.info.description
            };
            tab.AddItems(Row(labelX, y, "Native backbuffer"), check);

            // ---- live status
            y -= 64f;
            status = new OpLabel(new Vector2(labelX, y), new Vector2(520f, 20f), "",
                                 FLabelAlignment.Left);
            tab.AddItems(status);

            y -= 46f;
            tab.AddItems(new OpLabelLong(new Vector2(labelX, y - 40f), new Vector2(520f, 60f),
                "Rarely-needed options (a manual TargetWidth/TargetHeight override, and a half-pixel "
                + "diagnostic switch) live in BepInEx/config/steinkoloss.trueresolution.cfg."));

            RefreshStatus();
        }

        private static OpLabel Row(float x, float y, string text)
        {
            return new OpLabel(new Vector2(x, y), new Vector2(250f, 24f), text, FLabelAlignment.Left);
        }

        public override void Update()
        {
            base.Update();
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            if (scaleValue != null)
            {
                int v = supersample.Value;
                scaleValue.text = v <= 1 ? "off" : v + "x";
            }

            if (status == null) return;

            FScreen s = Futile.screen;
            if (s == null || s.renderTexture == null)
            {
                status.text = "not rendering yet";
                return;
            }

            status.text = $"logical {s.pixelWidth}x{s.pixelHeight}  ->  render "
                          + $"{s.renderTexture.width}x{s.renderTexture.height} ({s.renderTexture.filterMode})"
                          + $"  ->  screen {Screen.width}x{Screen.height}";
        }

        // ---------------------------------------------------------------- sync

        /// <summary>Pull the values currently in force so the controls never show something stale.</summary>
        private void SeedFromPlugin()
        {
            try
            {
                supersample.Value = Plugin.CurrentSupersample;
                nativeBackbuffer.Value = Plugin.CurrentNativeBackbuffer;
                downsample.Value = Plugin.CurrentDownsample.ToString();
                aspect.Value = Plugin.CurrentAspect.ToString();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("options: could not seed from the live config: " + e);
            }
        }

        /// <summary>Fired by Remix when the user presses Apply.</summary>
        private void ApplyToPlugin()
        {
            try
            {
                DownsampleMode ds;
                if (!Enum.TryParse(downsample.Value, out ds)) ds = DownsampleMode.Auto;

                AspectMode am;
                if (!Enum.TryParse(aspect.Value, out am)) am = AspectMode.Letterbox;

                Plugin.ApplyFromOptions(supersample.Value, nativeBackbuffer.Value, ds, am);
                RefreshStatus();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("options: applying settings failed: " + e);
            }
        }
    }
}
