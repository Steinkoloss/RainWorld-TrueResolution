using System;
using System.Reflection;
using System.Security;
using System.Security.Permissions;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

// BepInEx 5 on a Unity 2020.3 Mono runtime needs this to allow publicized/private access via MonoMod.
[module: UnverifiableCode]
#pragma warning disable CS0618
[assembly: SecurityPermission(SecurityAction.RequestMinimum, SkipVerification = true)]
#pragma warning restore CS0618

namespace TrueResolution
{
    /// <summary>
    /// Rain World renders the whole game into a RenderTexture that is hardcoded to 768 pixels tall
    /// (FScreen ctor) and 1024-1366 wide depending on the aspect-ratio option
    /// (Options.screenResolutions), and then forces the actual window/backbuffer to that same tiny
    /// size (Options.OnLoadFinished -> Screen.SetResolution). On a high-res display the result is
    /// scaled up twice, which is why the game looks soft.
    ///
    /// This plugin fixes both halves without touching the game's world-space framing:
    ///
    ///  1. Supersampling. FScreen already has a "renderScale" multiplier that sizes the RenderTexture
    ///     (pixelWidth * renderScale, pixelHeight * renderScale) but the constructor pins it to 1.
    ///     Crucially the Futile camera's orthographicSize is derived from pixelHeight, NOT from the
    ///     RenderTexture size (Futile.InitCamera / Futile.UpdateCameraPosition), so raising renderScale
    ///     increases pixel density while showing the exact same slice of the world.
    ///
    ///  2. Native backbuffer. We let the game keep its logical screen (gameplay visibility checks,
    ///     shader globals and room framing all derive from Options.ScreenSize) but present it into a
    ///     backbuffer at the display's real resolution, so the final composite is one clean filtered
    ///     scale instead of a hardware stretch of an already-small image.
    ///
    /// Invariants this plugin must never break: it never writes FScreen.pixelWidth, FScreen.pixelHeight,
    /// Options.screenResolutions or Options.ScreenSize.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "steinkoloss.trueresolution";
        public const string PluginName = "True Resolution";
        public const string PluginVersion = "1.5.0";

        internal static ManualLogSource Log;

        private ConfigEntry<int> cfgSupersample;
        private ConfigEntry<bool> cfgNativeBackbuffer;
        private ConfigEntry<int> cfgTargetWidth;
        private ConfigEntry<int> cfgTargetHeight;
        private ConfigEntry<bool> cfgLegacyScreenOffset;
        private ConfigEntry<AspectMode> cfgAspectMode;
        private ConfigEntry<DownsampleMode> cfgDownsample;

        /// <summary>Cached private setter for FScreen.renderScale (auto-property with a private set).</summary>
        private static PropertyInfo renderScaleProp;
        private static FieldInfo renderScaleField;

        /// <summary>Guards the ReinitRenderTexture hook against re-entering itself while we rebuild.</summary>
        private static bool rebuilding;

        private static int DesiredScale = 2;
        private static bool LegacyScreenOffset;
        private static DownsampleMode Downsampling = DownsampleMode.Point;

        /// <summary>Setter for FScreen.renderTexture, which is an auto-property with a private set.</summary>
        private static PropertyInfo renderTextureProp;

        /// <summary>Remembers the requested scale we already warned about, so the log stays readable.</summary>
        private static int loggedClampOf = -1;
        private static int loggedCostOf = -1;
        private static bool loggedMipDecline;

        private bool hooksApplied;

        // ---- display probe, resolved exactly once, at OnEnable (i.e. before the game has had any
        // chance to shrink the backbuffer) so we can never read our own forced window size back.
        private static int nativeW = -1, nativeH = -1;
        private static string nativeHow = "unresolved";

        // ---- backbuffer request state. Screen.SetResolution is a *request* applied at the next frame
        // boundary and it has no failure signal, so every request is verified and bounded.
        private int pendingW, pendingH, pendingFrames;
        private int attempts;
        private int confirmedW = -1, confirmedH = -1;
        private int frameCounter;
        private const int VerifyFrames = 20;
        private const int MaxAttempts = 4;

        private static Options lastOptions;
        private bool loggedUpdateFailure;
        private bool loggedPresentationFailure;

        /// <summary>Set once so the Remix options page can reach the live values and the apply path.</summary>
        private static Plugin self;
        private static TrueResolutionOptions optionsUI;
        private static bool optionsRegistered;

        internal static int CurrentSupersample => DesiredScale;
        internal static bool CurrentNativeBackbuffer => self != null && self.cfgNativeBackbuffer.Value;
        internal static DownsampleMode CurrentDownsample => Downsampling;
        internal static AspectMode CurrentAspect => Presentation.Mode;

        /// <summary>
        /// Applies a change made in the Remix options page. The BepInEx config file remains the storage,
        /// so everything is written back to it and persisted by BepInEx as usual.
        /// </summary>
        internal static void ApplyFromOptions(int supersample, bool nativeBackbuffer,
                                              DownsampleMode ds, AspectMode am)
        {
            if (self == null) return;

            int newScale = Mathf.Clamp(supersample, 1, 8);
            bool rebuildNeeded = newScale != DesiredScale || ds != Downsampling;
            bool aspectChanged = am != Presentation.Mode;

            self.cfgSupersample.Value = newScale;
            self.cfgNativeBackbuffer.Value = nativeBackbuffer;
            self.cfgDownsample.Value = ds;
            self.cfgAspectMode.Value = am;

            DesiredScale = newScale;
            Downsampling = ds;

            if (aspectChanged)
            {
                // Leaving Letterbox has to hand the RawImage back to the game, or it stays fitted to the
                // old aspect. Entering it needs the cursor patch, without which every menu button would
                // be offset by the width of the bars.
                Presentation.Mode = am;
                if (am == AspectMode.Letterbox) Presentation.InstallMousePatch();
                else Presentation.Restore();
                Log.LogInfo($"options: aspect mode -> {Presentation.Mode}");
            }

            if (rebuildNeeded && Futile.instance != null && Futile.screen != null)
            {
                // Go through the game's own UpdateScreenWidth rather than ReinitRenderTexture directly:
                // it is the only path that also rebinds camera.targetTexture and the presenting RawImage,
                // so shrinking the scale cannot leave them pointing at a released texture.
                Log.LogInfo($"options: rebuilding render texture for supersample={newScale} downsample={ds}");
                Futile.instance.UpdateScreenWidth(Futile.screen.pixelWidth);
            }

            // Re-assert the backbuffer; harmless when nothing changed, and picks up NativeBackbuffer
            // being switched back on.
            self.attempts = 0;
            self.pendingW = 0;
            self.frameCounter = 30;
        }

        public void OnEnable()
        {
            Log = Logger;

            cfgSupersample = Config.Bind(
                "Rendering", "Supersample", 2,
                new ConfigDescription(
                    "Internal render scale. The game renders the same view at this multiple of its "
                    + "internal 768-pixel-tall buffer (1024-1366 wide depending on the aspect-ratio "
                    + "option), then it is scaled down to your screen.\n"
                    + "With Downsample=Point (the default) higher values keep paying off. Room artwork is "
                    + "a Point-filtered texture, so supersampling magnifies it INSIDE the engine with hard "
                    + "pixel edges, instead of letting the display stretch a 768-tall image by a "
                    + "non-integer factor and blur across every texel boundary - which is what vanilla "
                    + "does and why vanilla looks soft. A denser render also quantises sprite positions "
                    + "more finely (the level graphic is placed at fractional camera coordinates), so "
                    + "edges land accurately and stop crawling when the camera pans.\n"
                    + "With a smoothing filter the returns fade much sooner, because filtering averages "
                    + "that precision back out.\n"
                    + "Cost scales with the SQUARE of this value, and how much that hurts is very "
                    + "room-dependent: the shader library declares 112 GrabPasses, 81 of them unnamed, "
                    + "and an unnamed grab copies the whole render target once per drawing object - but "
                    + "only shaders for effects present in the current room ever run. Raise it until the "
                    + "framerate stops being comfortable.\n"
                    + "The scale is clamped automatically so the render texture stays within the GPU's "
                    + "maximum texture size. 1 disables supersampling.",
                    new AcceptableValueRange<int>(1, 8)));

            cfgNativeBackbuffer = Config.Bind(
                "Rendering", "NativeBackbuffer", true,
                "In fullscreen only, present at the display's native resolution instead of letting the "
                + "game force the window down to its internal buffer size. This is usually the single "
                + "biggest visual win. Windowed mode is left alone.");

            cfgTargetWidth = Config.Bind(
                "Rendering", "TargetWidth", 0,
                "Fullscreen backbuffer width. 0 = auto-detect the display's native width. "
                + "Set this together with TargetHeight if auto-detect logs the wrong size.");

            cfgTargetHeight = Config.Bind(
                "Rendering", "TargetHeight", 0,
                "Fullscreen backbuffer height. 0 = auto-detect the display's native height.");

            cfgDownsample = Config.Bind(
                "Rendering", "Downsample", DownsampleMode.Point,
                "Filter used to get the supersampled render texture down to your screen.\n"
                + "Point (default): nearest neighbour, keeping hard pixel edges. Rain World is pixel art "
                + "and most people prefer this. It pairs best with a Supersample that lands near your "
                + "screen's resolution - at 2 on a 1440p screen the render texture is within 7% of the "
                + "backbuffer, so this is very nearly a 1:1 blit. Much higher values throw most of the "
                + "extra samples away and will shimmer in motion.\n"
                + "Auto: MipmapBox once the render texture is at least 1.5x the backbuffer, "
                + "plain bilinear below that. The threshold exists because mipmapping buys freedom from "
                + "aliasing and pays in sharpness - near 1:1 that is a net loss, since trilinear blends "
                + "part of a half-resolution level in to suppress aliasing bilinear was already "
                + "handling.\n"
                + "MipmapBox: give the render texture a mip chain and sample it trilinearly. The GPU "
                + "builds a box-filtered pyramid, so every source pixel contributes instead of just the "
                + "nearest four. This is the meaningful win at Supersample 3+, where a single bilinear "
                + "tap undersamples badly and shimmers; at Supersample 2 on a 1440p screen the ratio is "
                + "only 1.07 so trilinear stays on mip 0 and it changes almost nothing.\n"
                + "Bilinear: one 4-tap sample, the previous behaviour.\n"
                + "Point: nearest neighbour. Aliases hard unless the ratio is exactly 1:1 or integer.");

            cfgLegacyScreenOffset = Config.Bind(
                "Compatibility", "LegacyScreenOffset", false,
                "Diagnostic A/B switch. Raising renderScale above 1 moves FScreen.UpdateScreenOffset onto "
                + "a branch that is dead code in stock Rain World and shifts the camera by half a logical "
                + "unit. Enable this to force the stock renderScale==1 offset instead. Only touch this if "
                + "you are chasing a half-pixel shift; it is not obviously more correct either way.");

            cfgAspectMode = Config.Bind(
                "Rendering", "AspectMode", AspectMode.Letterbox,
                "How the game's ~16:9 logical picture is fitted into your display.\n"
                + "Letterbox (recommended): keep the backbuffer at your panel's native size and draw black "
                + "bars inside the game. Correct and identical on every platform, driver and graphics API. "
                + "Required on 21:9 / 32:9 / 4:3 / 16:10 panels, a no-op on 16:9.\n"
                + "AspectBackbuffer: ask Unity for a backbuffer that already has the logical aspect ratio "
                + "and let FullScreenMode.FullScreenWindow letterbox it. Documented behaviour, but there "
                + "are known Unity bugs where it silently stretches instead, and it costs an extra "
                + "rescale. Only use this if Letterbox misbehaves.\n"
                + "Stretch: vanilla behaviour. The picture is distorted on any non-16:9 display.");

            DesiredScale = Mathf.Clamp(cfgSupersample.Value, 1, 8);
            LegacyScreenOffset = cfgLegacyScreenOffset.Value;
            Downsampling = cfgDownsample.Value;
            Presentation.Mode = cfgAspectMode.Value;

            renderScaleProp = typeof(FScreen).GetProperty(
                "renderScale", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            renderScaleField = typeof(FScreen).GetField(
                "<renderScale>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            renderTextureProp = typeof(FScreen).GetProperty(
                "renderTexture", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            self = this;

            On.FScreen.ctor += FScreen_ctor;
            On.FScreen.ReinitRenderTexture += FScreen_ReinitRenderTexture;
            On.FScreen.UpdateScreenOffset += FScreen_UpdateScreenOffset;
            On.Futile.Init += Futile_Init;
            On.Futile.UpdateCameraPosition += Futile_UpdateCameraPosition;
            On.Options.OnLoadFinished += Options_OnLoadFinished;
            On.RainWorld.OnModsInit += RainWorld_OnModsInit;

            if (Presentation.Active) Presentation.InstallMousePatch();

            hooksApplied = true;

            Log.LogInfo($"{PluginName} {PluginVersion} loaded. cfg: supersample={DesiredScale} "
                        + $"nativeBackbuffer={cfgNativeBackbuffer.Value} "
                        + $"target={(cfgTargetWidth.Value > 0 ? cfgTargetWidth.Value + "x" + cfgTargetHeight.Value : "auto")} "
                        + $"downsample={Downsampling} legacyScreenOffset={LegacyScreenOffset}");
            Log.LogInfo($"gfx: '{SystemInfo.graphicsDeviceVersion}' device='{SystemInfo.graphicsDeviceName}' "
                        + $"-> Futile.isOpenGL will be {SystemInfo.graphicsDeviceVersion.Contains("OpenGL")}");
            ProbeNative();
        }

        public void OnDisable()
        {
            if (!hooksApplied) return;
            On.FScreen.ctor -= FScreen_ctor;
            On.FScreen.ReinitRenderTexture -= FScreen_ReinitRenderTexture;
            On.FScreen.UpdateScreenOffset -= FScreen_UpdateScreenOffset;
            On.Futile.Init -= Futile_Init;
            On.Futile.UpdateCameraPosition -= Futile_UpdateCameraPosition;
            On.Options.OnLoadFinished -= Options_OnLoadFinished;
            Presentation.RemoveMousePatch();
            Presentation.Reset();
            hooksApplied = false;
        }

        // ---------------------------------------------------------------- display probe

        /// <summary>
        /// Resolve the display's native size once, at plugin load, i.e. BEFORE Options.OnLoadFinished
        /// has forced the backbuffer down. Reading it later risks reading back the size we ourselves
        /// forced, which would silently turn the whole feature into a no-op.
        /// </summary>
        private static void ProbeNative()
        {
            if (nativeW > 0) return;

            int dw = 0, dh = 0, cw = 0, ch = 0, mw = 0, mh = 0, modeCount = 0;
            try { dw = Display.main.systemWidth; dh = Display.main.systemHeight; }
            catch (Exception e) { Log.LogWarning("Display.main.system* threw: " + e.Message); }
            try { Resolution c = Screen.currentResolution; cw = c.width; ch = c.height; }
            catch (Exception e) { Log.LogWarning("Screen.currentResolution threw: " + e.Message); }
            try
            {
                Resolution[] all = Screen.resolutions;
                if (all != null)
                {
                    modeCount = all.Length;
                    for (int i = 0; i < all.Length; i++)
                        if ((long)all[i].width * all[i].height > (long)mw * mh) { mw = all[i].width; mh = all[i].height; }
                }
            }
            catch (Exception e) { Log.LogWarning("Screen.resolutions threw: " + e.Message); }

            Log.LogInfo($"display probe: Screen={Screen.width}x{Screen.height} fs={Screen.fullScreen} "
                        + $"mode={Screen.fullScreenMode}");
            Log.LogInfo($"display probe: currentResolution={cw}x{ch}  Display.main.system={dw}x{dh}  "
                        + $"largestMode={mw}x{mh} ({modeCount} modes)");

            // currentResolution and Display.main.system* both describe the *current display*, so taking
            // the larger of the two is safe. The enumerated-mode maximum is only a last resort: a panel
            // running at 2560x1440 still enumerates 3840x2160, and using that would allocate an
            // oversized backbuffer that the compositor then has to shrink again.
            if (cw > 0 || dw > 0)
            {
                if ((long)cw * ch >= (long)dw * dh) { nativeW = cw; nativeH = ch; nativeHow = "Screen.currentResolution"; }
                else { nativeW = dw; nativeH = dh; nativeHow = "Display.main.system*"; }
            }
            else if (mw > 0)
            {
                nativeW = mw; nativeH = mh; nativeHow = "Screen.resolutions max (fallback)";
            }
            else
            {
                nativeHow = "FAILED";
            }

            if (nativeW > 0)
            {
                Log.LogInfo($"display probe: chose {nativeW}x{nativeH} via '{nativeHow}'");
                if (nativeW <= 1366 && nativeH <= 768)
                    Log.LogWarning($"display probe: {nativeW}x{nativeH} is not larger than the game's own "
                                   + "buffer, so NativeBackbuffer will have nothing to do. If your display "
                                   + "really is bigger, set TargetWidth/TargetHeight in the config.");
            }
            else
            {
                Log.LogError("display probe FAILED - NativeBackbuffer disabled unless you set "
                             + "TargetWidth/TargetHeight in the config.");
            }
        }

        // ---------------------------------------------------------------- render scale

        private static void SetRenderScale(FScreen self, int scale)
        {
            // Prefer the real (private) setter so we do not depend on the compiler's backing-field name.
            MethodInfo setter = renderScaleProp?.GetSetMethod(true);
            if (setter != null)
            {
                setter.Invoke(self, new object[] { scale });
                return;
            }
            if (renderScaleField != null)
            {
                renderScaleField.SetValue(self, scale);
                return;
            }
            Log.LogError("Could not set FScreen.renderScale - supersampling disabled.");
        }

        /// <summary>
        /// Makes the render texture match <see cref="DesiredScale"/>. Safe to call repeatedly.
        /// Rebuilding goes through the game's own ReinitRenderTexture so the shader half-texel offset
        /// (FScreen.UpdateScreenOffset) is recomputed exactly the way the game expects, and so other
        /// mods' ReinitRenderTexture postfixes still run.
        /// </summary>
        /// <summary>
        /// The scale we can actually afford to allocate. A 1366x768 logical screen at 8x is 10928x6144,
        /// which is inside D3D11's 16384 limit but not inside every GPU's, and asking for a render texture
        /// past <see cref="SystemInfo.maxTextureSize"/> gets you a silently-failed Create() and a black
        /// screen. Clamp instead, and say so once.
        /// </summary>
        private static int EffectiveScale(FScreen self)
        {
            int want = Mathf.Clamp(DesiredScale, 1, 8);
            int pw = Mathf.Max(1, self.pixelWidth), ph = Mathf.Max(1, self.pixelHeight);

            int maxDim = SystemInfo.maxTextureSize;
            if (maxDim <= 0) maxDim = 8192;                 // unknown driver: assume the D3D10 floor
            int cap = Mathf.Max(1, Mathf.Min(maxDim / pw, maxDim / ph));

            int eff = Mathf.Clamp(want, 1, cap);
            if (eff != want && loggedClampOf != want)
            {
                loggedClampOf = want;
                Log.LogWarning($"Supersample {want} would need a {pw * want}x{ph * want} render texture, "
                               + $"past this GPU's {maxDim}px limit. Clamped to {eff} "
                               + $"({pw * eff}x{ph * eff}).");
            }

            if (eff >= 3 && loggedCostOf != eff)
            {
                loggedCostOf = eff;
                long px = (long)pw * eff * ph * eff;
                // Informational, not a warning of doom: the actual hit depends entirely on how many
                // grab-pass effects the current room uses, and a fast GPU may not notice at all.
                Log.LogInfo($"Supersample {eff}: {pw * eff}x{ph * eff}, {px / 1000000f:F1} MP/frame "
                            + $"(~{px * 4L / 1048576L} MB for the target), {eff * eff / 4f:F1}x the fill cost "
                            + "of the default 2. Cost is room-dependent (unnamed GrabPasses copy the whole "
                            + "target per drawing object). Note this is already well above your backbuffer, "
                            + "so the gain is anti-aliasing only - terrain is fixed 1400x800 art.");
            }
            return eff;
        }

        private static void EnsureRenderScale(FScreen self)
        {
            if (self == null || rebuilding) return;

            int target = EffectiveScale(self);

            if (self.renderScale != target)
            {
                SetRenderScale(self, target);
                if (self.renderScale != target)
                {
                    Log.LogError($"renderScale is still {self.renderScale} after trying to set "
                                 + $"{target} - reflection failed, supersampling is OFF.");
                    return;
                }

                rebuilding = true;
                try
                {
                    // Re-run with the current width: this releases the old texture and allocates a new
                    // one at pixelWidth * renderScale x pixelHeight * renderScale.
                    self.ReinitRenderTexture(self.pixelWidth);
                }
                finally
                {
                    rebuilding = false;
                }
                Log.LogInfo($"render texture is now {self.pixelWidth * self.renderScale}x"
                            + $"{self.pixelHeight * self.renderScale} "
                            + $"(logical {self.pixelWidth}x{self.pixelHeight}, {self.renderScale}x supersampled)");
            }

            ConformRenderTexture(self);
            ApplyFilterMode(self);
        }

        /// <summary>
        /// Gives the render texture a mip chain, which is the best downsample available without shipping a
        /// custom shader (Unity cannot compile ShaderLab at runtime, so a Lanczos/Mitchell kernel would
        /// need an AssetBundle built in the editor).
        ///
        /// The GPU builds a box-filtered pyramid and trilinear sampling blends the two bracketing levels,
        /// so at large ratios every source pixel contributes instead of only the nearest four. For pure
        /// minification a box pyramid is close to optimal - Lanczos mainly wins when magnifying.
        ///
        /// useMipMap can only be set before the texture is created and the game news up a plain
        /// RenderTexture, so the only way in is to allocate a replacement and rebind the three things that
        /// reference it: both Futile cameras' targetTexture and the presenting RawImage.
        /// </summary>
        private static void ConformRenderTexture(FScreen self)
        {
            RenderTexture rt = self?.renderTexture;
            if (rt == null || renderTextureProp == null) return;

            int wantW, wantH;
            DesiredRTSize(self, out wantW, out wantH);

            float ratio = Mathf.Max((float)wantW / Mathf.Max(1, Screen.width),
                                    (float)wantH / Mathf.Max(1, Screen.height));

            // Mipmapping buys freedom from aliasing and pays for it in sharpness, and close to 1:1 that
            // trade is a net loss: trilinear blends log2(ratio) of a HALF-resolution level into the
            // result. A bilinear tap covers a 2x2 texel neighbourhood, so it stops covering the footprint
            // as the ratio approaches 2; 1.5 is a conservative switch-over. MipmapBox forces it on.
            const float MipThreshold = 1.5f;
            bool wantMips = Downsampling == DownsampleMode.MipmapBox
                            || (Downsampling == DownsampleMode.Auto && ratio >= MipThreshold);

            if (rt.width == wantW && rt.height == wantH && rt.useMipMap == wantMips)
            {
                if (Downsampling == DownsampleMode.Auto && !wantMips && ratio > 1f && !loggedMipDecline)
                {
                    loggedMipDecline = true;
                    Log.LogInfo($"downsample: ratio {ratio:F2}x is below the {MipThreshold:F1}x threshold, "
                                + "so plain bilinear is sharper than a mip chain here.");
                }
                return;
            }

            MethodInfo setter = renderTextureProp.GetSetMethod(true);
            if (setter == null) return;

            RenderTexture next = new RenderTexture(wantW, wantH, 0, rt.format,
                                                   RenderTextureReadWrite.Default)
            {
                name = "TrueResolution_RT",
                useMipMap = wantMips,
                autoGenerateMips = wantMips,      // regenerated after the camera renders into it
                antiAliasing = 1,
                wrapMode = rt.wrapMode,
                filterMode = wantMips ? FilterMode.Trilinear : rt.filterMode
            };

            if (!next.Create())
            {
                UnityEngine.Object.Destroy(next);
                Log.LogWarning($"could not create a {wantW}x{wantH} render texture; keeping the "
                               + $"existing {rt.width}x{rt.height} one.");
                return;
            }

            setter.Invoke(self, new object[] { next });

            Futile f = Futile.instance;
            if (f != null)
            {
                if (f.camera != null) f.camera.targetTexture = next;
                if (f.camera2 != null) f.camera2.targetTexture = next;   // JollyCoop split-screen
                Presentation.RebindTexture(next);
            }

            rt.Release();
            UnityEngine.Object.Destroy(rt);

            Log.LogInfo($"render target: {next.width}x{next.height}"
                        + (NativeRT ? " (native, 1:1 with the backbuffer)" : "")
                        + $"  ratio {ratio:F2}x"
                        + (wantMips ? "  mip chain on" : ""));
        }

        /// <summary>
        /// How big the render texture should be.
        ///
        /// Normally pixelWidth/Height times the integer renderScale. In Native mode it is the backbuffer
        /// size exactly, which is legal for the same reason supersampling is: the Futile camera's
        /// orthographicSize comes from pixelHeight and never from the render texture, so the world
        /// framing does not move. That gives a 1:1 composite with no resampling at all, at a fraction of
        /// the fill cost. (camera.aspect is derived from the target's dimensions, so the visible world
        /// width shifts by the difference between the logical and display aspect ratios - 1366/768 vs
        /// 16:9 is 0.05%, i.e. under a single world unit.)
        /// </summary>
        private static void DesiredRTSize(FScreen self, out int w, out int h)
        {
            if (NativeRT && Screen.width > 0 && Screen.height > 0)
            {
                w = Screen.width;
                h = Screen.height;
            }
            else
            {
                w = self.pixelWidth * Mathf.Max(1, self.renderScale);
                h = self.pixelHeight * Mathf.Max(1, self.renderScale);
            }

            int max = SystemInfo.maxTextureSize > 0 ? SystemInfo.maxTextureSize : 8192;
            w = Mathf.Clamp(w, 1, max);
            h = Mathf.Clamp(h, 1, max);
        }

        /// <summary>
        /// Point sampling is only correct when the render texture lands on the backbuffer pixel-exactly.
        /// The stock code (FScreen.ReinitRenderTexture / Futile.Init) picks Point whenever the display is
        /// at least 1366x768, which is right for vanilla's 1:1 blit but wrong once EITHER side of the
        /// composite changes size. In particular 1360 -> 2560 (1.88x, non-integer) point-magnified in
        /// engine is visibly worse than vanilla, so this must key on the RT-vs-backbuffer ratio, not on
        /// renderScale.
        /// </summary>
        private static void ApplyFilterMode(FScreen self)
        {
            RenderTexture rt = self?.renderTexture;
            if (rt == null) return;

            int bbW = Screen.width, bbH = Screen.height;
            int rtW = rt.width, rtH = rt.height;
            if (bbW <= 0 || bbH <= 0 || rtW <= 0 || rtH <= 0) return;

            FilterMode want;
            if (Downsampling == DownsampleMode.Point)
            {
                want = FilterMode.Point;
            }
            else
            {
                bool oneToOne = rtW == bbW && rtH == bbH;
                bool intUpscale = !oneToOne
                                  && rtW <= bbW && rtH <= bbH
                                  && bbW % rtW == 0 && bbH % rtH == 0
                                  && (bbW / rtW) == (bbH / rtH);

                if (oneToOne || intUpscale)
                {
                    // Pixel-exact or an exact integer magnification: nearest neighbour is the correct,
                    // sharpest choice and a mip chain would only ever blur it.
                    want = FilterMode.Point;
                }
                else
                {
                    // Trilinear is only meaningful with mips present; without them Unity treats it as
                    // bilinear anyway, so asking for it unconditionally would be misleading in the log.
                    want = rt.useMipMap ? FilterMode.Trilinear : FilterMode.Bilinear;
                }
            }

            if (rt.filterMode != want) rt.filterMode = want;
        }

        // ---------------------------------------------------------------- hooks

        /// <summary>
        /// Registers the Remix options page. This is the only correct place: the mod id must be registered
        /// after the mod list exists, and OnModsInit is where every Rain World mod does it. Failure here is
        /// non-fatal - the BepInEx config file still works, you just lose the in-game page.
        /// </summary>
        private static void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld rw)
        {
            orig(rw);
            if (optionsRegistered) return;
            try
            {
                optionsUI = new TrueResolutionOptions();
                // Must match "id" in modinfo.json, or Remix cannot pair the page with the mod.
                optionsRegistered = MachineConnector.SetRegisteredOI("trueresolution", optionsUI);
                Log.LogInfo(optionsRegistered
                    ? "options: registered the in-game Remix config page"
                    : "options: SetRegisteredOI returned false; use the BepInEx config file instead");
            }
            catch (Exception e)
            {
                optionsUI = null;
                Log.LogError("options: could not register the Remix config page, falling back to the "
                             + "BepInEx config file: " + e);
            }
        }

        private static void FScreen_ctor(On.FScreen.orig_ctor orig, FScreen self, FutileParams futileParams)
        {
            orig(self, futileParams);
            // The constructor just pinned renderScale to 1 and allocated a 1x texture; upgrade it.
            try { EnsureRenderScale(self); }
            catch (Exception e) { Log.LogError("FScreen ctor postfix failed, supersampling is OFF: " + e); }
        }

        private static void FScreen_ReinitRenderTexture(
            On.FScreen.orig_ReinitRenderTexture orig, FScreen self, int displayWidth)
        {
            orig(self, displayWidth);
            // orig() reallocated using the *current* renderScale. If that is already what we want the
            // texture is correctly sized and we only need to restore the filter mode.
            try
            {
                if (rebuilding) ApplyFilterMode(self);
                else EnsureRenderScale(self);
            }
            catch (Exception e) { Log.LogError("ReinitRenderTexture postfix failed: " + e); }
        }

        private static void FScreen_UpdateScreenOffset(On.FScreen.orig_UpdateScreenOffset orig, FScreen self)
        {
            orig(self);
            if (!LegacyScreenOffset || Futile.isOpenGL) return;
            try
            {
                Futile.screenPixelOffset = new Vector2(0.5f * Futile.displayScaleInverse,
                                                       0.5f * Futile.displayScaleInverse);
                Shader.SetGlobalVector(RainWorld.ShadPropScreenOffset, Vector2.zero);
            }
            catch (Exception e) { Log.LogError("UpdateScreenOffset postfix failed: " + e); }
        }

        private static void Futile_Init(On.Futile.orig_Init orig, Futile self, FutileParams futileParams)
        {
            orig(self, futileParams);
            // Futile.Init overwrites filterMode after constructing FScreen, so re-assert it here.
            try
            {
                ApplyFilterMode(Futile.screen);
                FScreen s = Futile.screen;
                if (s != null && s.renderTexture != null)
                    Log.LogInfo($"FScreen: logical {s.pixelWidth}x{s.pixelHeight} renderScale={s.renderScale} "
                                + $"RT={s.renderTexture.width}x{s.renderTexture.height} "
                                + $"filter={s.renderTexture.filterMode} | backbuffer {Screen.width}x{Screen.height} "
                                + $"| isOpenGL={Futile.isOpenGL} screenPixelOffset={Futile.screenPixelOffset}");
            }
            catch (Exception e) { Log.LogError("Futile.Init postfix failed: " + e); }
        }

        /// <summary>
        /// Futile.UpdateCameraPosition (Futile.cs:494-512) is the ONLY writer of _cameraImage.uvRect and
        /// Futile.subjectToAspectRatioIrregularity in the whole game (verified by grep: Futile.cs:510/511
        /// are the only assignments; the only reader of the flag is RoomCamera.cs:1289). It does NOT touch
        /// the RectTransform, so our letterbox geometry is never undone and only the uvRect needs
        /// re-asserting. Hooking here covers every path that can change it: Futile.Init (:224),
        /// Futile.UpdateScreenWidth (:284) and the FScreen.originX/originY setters (FScreen.cs:34/:50).
        /// </summary>
        private static void Futile_UpdateCameraPosition(On.Futile.orig_UpdateCameraPosition orig, Futile self)
        {
            orig(self);
            try { Presentation.Apply(); }
            catch (Exception e) { Log.LogError("UpdateCameraPosition postfix failed: " + e); }
        }

        private void Options_OnLoadFinished(On.Options.orig_OnLoadFinished orig, Options self)
        {
            // Never swallow orig(): if it throws the game is half-initialised and continuing is worse.
            orig(self);
            try
            {
                lastOptions = self;
                if (!cfgNativeBackbuffer.Value || !self.fullScreen) return;

                int w, h;
                if (!ResolveTarget(out w, out h)) return;

                // orig() has just QUEUED Screen.SetResolution(ScreenSize, false) (Options.cs:1119).
                // Unity applies that at the end of the frame, so Screen.width still reports whatever we
                // set last time. Comparing against the live Screen here is exactly wrong - it would
                // early-return and let the game's shrink win. Always re-request; Unity coalesces
                // requests within a frame and the last one wins.
                attempts = 0;   // fresh external cause, not a failed request of ours
                pendingW = 0;   // supersede anything in flight
                RequestBackbuffer(w, h, "Options.OnLoadFinished", true);
            }
            catch (Exception e) { Log.LogError("Options.OnLoadFinished postfix failed: " + e); }
        }

        // ---------------------------------------------------------------- backbuffer

        private static Options CurrentOptions()
        {
            try
            {
                RainWorld rw = RWCustom.Custom.rainWorld;
                if (rw != null && rw.options != null) return rw.options;
            }
            catch { }
            return lastOptions;
        }

        /// <summary>The game's *intent*, not Unity's deferred state. Menu code does
        /// SetResolution(..., false) and then Screen.fullScreen = x as two separate statements, so live
        /// Screen.fullScreen reads false for a few frames during a transition.</summary>
        private static bool WantsFullscreen()
        {
            Options o = CurrentOptions();
            return o != null ? o.fullScreen : Screen.fullScreen;
        }

        private static Vector2 LogicalSize()
        {
            FScreen s = Futile.screen;
            if (s != null && s.pixelWidth > 0 && s.pixelHeight > 0)
                return new Vector2(s.pixelWidth, s.pixelHeight);
            Options o = CurrentOptions();
            return o != null ? o.ScreenSize : Vector2.zero;
        }

        private bool ResolveTarget(out int w, out int h)
        {
            w = 0; h = 0;

            ProbeNative();

            if (cfgTargetWidth.Value > 0 && cfgTargetHeight.Value > 0)
            {
                w = cfgTargetWidth.Value;
                h = cfgTargetHeight.Value;
            }
            else
            {
                if (nativeW <= 0 || nativeH <= 0) return false;
                w = nativeW; h = nativeH;
            }

            // Never ask for a fullscreen backbuffer larger than the panel. Unity documents that "if no
            // matching resolution is supported, the closest one is used", so an oversized request does not
            // fail loudly - it silently lands somewhere else and then our verify loop burns its whole
            // attempt budget. This also covers a hand-edited TargetWidth/TargetHeight and is the guard
            // that makes a sub-768p display (1280x720, 1024x600) degrade instead of thrash.
            if (nativeW > 0 && nativeH > 0)
            {
                if (w > nativeW || h > nativeH)
                {
                    Log.LogInfo($"backbuffer: clamping requested {w}x{h} to panel {nativeW}x{nativeH}");
                    w = Mathf.Min(w, nativeW);
                    h = Mathf.Min(h, nativeH);
                }
            }

            // AspectBackbuffer mode only: shrink the request so the backbuffer already carries the logical
            // aspect ratio and Unity's own FullScreenWindow letterboxing has nothing to correct. In
            // Letterbox mode we deliberately keep the FULL native size and put the bars inside the game
            // instead - that is the whole point, and it is what keeps the picture area a single clean
            // filtered scale rather than two.
            if (Presentation.Mode == AspectMode.AspectBackbuffer)
            {
                Vector2 s = LogicalSize();
                if (s.x > 0f && s.y > 0f)
                {
                    float wantAspect = s.x / s.y;
                    float haveAspect = (float)w / h;
                    // 1360x768 (1.7708) vs 16:9 (1.7778) is 0.4% out and must pass through untouched;
                    // 1024x768 (1.333), 1229x768 (1.600) and 1280x768 (1.667) are all >5% out and are
                    // corrected.
                    if (Mathf.Abs(wantAspect - haveAspect) > 0.02f)
                    {
                        if (wantAspect < haveAspect) w = Mathf.RoundToInt(h * wantAspect);
                        else h = Mathf.RoundToInt(w / wantAspect);
                        w -= w & 1;
                        h -= h & 1;
                    }
                }
            }

            return w > 0 && h > 0;
        }

        private void RequestBackbuffer(int w, int h, string why, bool force)
        {
            if (pendingW != 0) return;
            if (!force && Screen.width == w && Screen.height == h)
            {
                confirmedW = w; confirmedH = h;
                return;
            }
            if (attempts >= MaxAttempts) return;

            attempts++;
            pendingW = w; pendingH = h; pendingFrames = VerifyFrames;
            Log.LogInfo($"backbuffer: requesting {w}x{h} ({why}, attempt {attempts}/{MaxAttempts}); "
                        + $"now {Screen.width}x{Screen.height} fs={Screen.fullScreen} mode={Screen.fullScreenMode}");
            // Explicit FullScreenWindow: borderless at the panel's own size means no display mode change
            // and no external scaler, which is the entire point. The bool overload maps to this anyway.
            Screen.SetResolution(w, h, FullScreenMode.FullScreenWindow);
        }

        public void Update()
        {
            try { UpdateInner(); }
            catch (Exception e)
            {
                if (!loggedUpdateFailure)
                {
                    loggedUpdateFailure = true;
                    Log.LogError("Update failed (logged once): " + e);
                }
            }
        }

        private void UpdateInner()
        {
            // 0. Re-fit the picture. Idempotent and allocation-free, and it must run every frame because
            //    the backbuffer can change under us at any time (alt-tab, a display hot-plug, the user
            //    dragging the window to a differently-shaped monitor, or one of the four vanilla
            //    Screen.SetResolution sites we do not hook). It also refreshes the cached picture
            //    rectangle that the Futile.mousePosition patch divides by.
            //    Isolated: a presentation failure must not take the backbuffer state machine with it.
            try { Presentation.Apply(); }
            catch (Exception e)
            {
                if (!loggedPresentationFailure)
                {
                    loggedPresentationFailure = true;
                    Log.LogError("Presentation.Apply failed (logged once): " + e);
                }
            }

            // 1. Verify an in-flight request every frame. Screen.SetResolution is a request with no
            //    failure signal; without this the plugin would re-issue it forever if it never lands,
            //    recreating the swapchain twice a second.
            if (pendingW != 0)
            {
                if (Screen.width == pendingW && Screen.height == pendingH)
                {
                    confirmedW = pendingW; confirmedH = pendingH;
                    Log.LogInfo($"backbuffer: CONFIRMED {Screen.width}x{Screen.height} "
                                + $"mode={Screen.fullScreenMode} after {VerifyFrames - pendingFrames} frames");
                    pendingW = 0; attempts = 0;
                    // The correct filter mode depends on the RT-vs-backbuffer ratio, and the backbuffer
                    // has only just settled - the lifecycle hooks ran with a stale Screen.width.
                    ApplyFilterMode(Futile.screen);
                }
                else if (--pendingFrames <= 0)
                {
                    Log.LogWarning($"backbuffer: SetResolution({pendingW}x{pendingH}) did not take effect "
                                   + $"within {VerifyFrames} frames - still {Screen.width}x{Screen.height}. "
                                   + (attempts >= MaxAttempts
                                        ? "Giving up. Set TargetWidth/TargetHeight in the config, or the "
                                          + "display refused the mode."
                                        : "Will retry."));
                    pendingW = 0;
                }
                return;
            }

            if (++frameCounter < 30) return;
            frameCounter = 0;

            // 2. Throttled re-assert. Four of the five vanilla Screen.SetResolution sites are not hooked
            //    (Menu/OptionsMenu.cs:1088, 1101, 1105 and Options.cs:1115), so this is their recovery.
            if (!cfgNativeBackbuffer.Value) return;

            // Gate on the game's intent AND on the live state agreeing with it. Firing while the game is
            // deliberately dropping to windowed (OptionsMenu.cs:1088-1089) would yank the user back into
            // fullscreen and start a fight with the wrongFullscreenSetting watchdog.
            if (!WantsFullscreen() || !Screen.fullScreen) return;

            int w, h;
            if (!ResolveTarget(out w, out h)) return;

            if (Screen.width == w && Screen.height == h)
            {
                attempts = 0;
                confirmedW = w; confirmedH = h;
                return;
            }

            // Distinguish "the game clobbered a size we had already achieved" (a fresh cause: reset the
            // attempt budget) from "our request never landed" (keep counting down towards giving up).
            if (confirmedW == w && confirmedH == h)
            {
                attempts = 0;
                confirmedW = -1; confirmedH = -1;
            }

            // Note the comparison is != , not < : an explicitly configured target smaller than the
            // current backbuffer must also be honoured.
            RequestBackbuffer(w, h, "poll", false);
        }
    }
}
