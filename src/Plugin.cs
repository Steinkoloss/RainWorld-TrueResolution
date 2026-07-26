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
    ///     increases pixel density while showing the exact same slice of the world. The default is
    ///     automatic: the smallest integer scale whose render texture covers the displayed picture.
    ///
    ///  2. Native backbuffer. We let the game keep its logical screen (gameplay visibility checks,
    ///     shader globals and room framing all derive from Options.ScreenSize) but present it into a
    ///     backbuffer at the display's real resolution, so the final composite is one clean scale
    ///     instead of a hardware stretch of an already-small image.
    ///
    /// Everything is integer-scaled BY DESIGN. A display-sized ("true native") render texture was
    /// implemented and pixel-diagnosed in development: Rain World's sprites carry a baked one-texel
    /// black outline, and a non-integer world-to-pixel ratio must render that outline unevenly (Point)
    /// or smear it into a halo (Bilinear). There is no third sampling mode, so non-integer targets are
    /// gone rather than configurable. With targets integer-sized, the game's own filter selection,
    /// half-texel offset and camera aspect are all exactly correct, so this plugin no longer overrides
    /// any of them.
    ///
    /// Invariants this plugin must never break: it never writes FScreen.pixelWidth, FScreen.pixelHeight,
    /// Options.screenResolutions or Options.ScreenSize.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "steinkoloss.trueresolution";
        public const string PluginName = "True Resolution";
        public const string PluginVersion = "1.7.0";

        internal static ManualLogSource Log;

        private ConfigEntry<int> cfgQuality;
        private ConfigEntry<bool> cfgNativeBackbuffer;
        private ConfigEntry<int> cfgTargetWidth;
        private ConfigEntry<int> cfgTargetHeight;
        private ConfigEntry<AspectMode> cfgAspectMode;

        /// <summary>Cached private setter for FScreen.renderScale (auto-property with a private set).</summary>
        private static PropertyInfo renderScaleProp;
        private static FieldInfo renderScaleField;

        /// <summary>Guards the ReinitRenderTexture hook against re-entering itself while we rebuild.</summary>
        private static bool rebuilding;

        /// <summary>0 = automatic (smallest integer scale covering the picture), 1-8 = fixed.</summary>
        private static int Quality;

        private static int loggedClampOf = -1;
        private static int loggedCostOf = -1;

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

        internal static int CurrentQuality => Quality;
        internal static AspectMode CurrentAspect => Presentation.Mode;

        /// <summary>
        /// Applies a change made in the Remix options page. The BepInEx config file remains the storage,
        /// so everything is written back to it and persisted by BepInEx as usual.
        /// </summary>
        internal static void ApplyFromOptions(int quality, AspectMode am)
        {
            if (self == null) return;

            int newQuality = Mathf.Clamp(quality, 0, 8);
            bool rebuildNeeded = newQuality != Quality;
            bool aspectChanged = am != Presentation.Mode;

            self.cfgQuality.Value = newQuality;
            self.cfgAspectMode.Value = am;

            Quality = newQuality;

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
                Log.LogInfo($"options: rebuilding render texture for quality={newQuality}");
                Futile.instance.UpdateScreenWidth(Futile.screen.pixelWidth);
            }

            // Re-assert the backbuffer; harmless when nothing changed.
            self.attempts = 0;
            self.pendingW = 0;
            self.frameCounter = 30;
        }

        public void OnEnable()
        {
            Log = Logger;

            cfgQuality = Config.Bind(
                "Rendering", "RenderQuality", 0,
                new ConfigDescription(
                    "How much detail to render, as a multiple of the game's internal 768-tall buffer.\n"
                    + "0 (default) = automatic: the smallest scale whose render texture covers the "
                    + "displayed picture - 2x on 1080p and 1440p, 3x on 4K - which is the cheapest "
                    + "clean setting for any display.\n"
                    + "1-8 force a fixed scale. Higher values keep paying off with hard pixels: the room "
                    + "artwork is magnified inside the engine instead of being stretched by the display, "
                    + "and a denser render places every pixel edge more precisely, so the image is "
                    + "crisper and steadier as the camera pans. Cost grows with the square of the value "
                    + "and is very room-dependent; the scale is clamped so the render texture stays "
                    + "within the GPU's maximum texture size.",
                    new AcceptableValueRange<int>(0, 8)));

            cfgNativeBackbuffer = Config.Bind(
                "Rendering", "NativeBackbuffer", true,
                "In fullscreen only, present at the display's native resolution instead of letting the "
                + "game force the window down to its internal buffer size. This is usually the single "
                + "biggest visual win. Windowed mode is left alone. Troubleshooting switch - there is "
                + "no good reason to turn this off.");

            cfgTargetWidth = Config.Bind(
                "Rendering", "TargetWidth", 0,
                "Fullscreen backbuffer width. 0 = auto-detect the display's native width. "
                + "Set this together with TargetHeight if auto-detect logs the wrong size.");

            cfgTargetHeight = Config.Bind(
                "Rendering", "TargetHeight", 0,
                "Fullscreen backbuffer height. 0 = auto-detect the display's native height.");

            cfgAspectMode = Config.Bind(
                "Rendering", "AspectMode", AspectMode.Letterbox,
                "How the game's ~16:9 logical picture is fitted into your display.\n"
                + "Letterbox (default): keep the backbuffer at your panel's native size and draw black "
                + "bars inside the game. Correct and identical on every platform, driver and graphics "
                + "API. Required on 21:9 / 32:9 / 4:3 / 16:10 panels, a no-op on 16:9.\n"
                + "AspectBackbuffer: ask Unity for a backbuffer that already has the logical aspect "
                + "ratio and let FullScreenMode.FullScreenWindow letterbox it. Documented behaviour, "
                + "but there are known Unity bugs where it silently stretches instead. Only use this "
                + "if Letterbox misbehaves.\n"
                + "Stretch: vanilla behaviour. The picture is distorted on any non-16:9 display.");

            Quality = Mathf.Clamp(cfgQuality.Value, 0, 8);
            Presentation.Mode = cfgAspectMode.Value;

            renderScaleProp = typeof(FScreen).GetProperty(
                "renderScale", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            renderScaleField = typeof(FScreen).GetField(
                "<renderScale>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);

            self = this;

            On.FScreen.ctor += FScreen_ctor;
            On.FScreen.ReinitRenderTexture += FScreen_ReinitRenderTexture;
            On.Futile.Init += Futile_Init;
            On.Futile.UpdateCameraPosition += Futile_UpdateCameraPosition;
            On.Options.OnLoadFinished += Options_OnLoadFinished;
            On.RainWorld.OnModsInit += RainWorld_OnModsInit;

            if (Presentation.Active) Presentation.InstallMousePatch();

            hooksApplied = true;

            Log.LogInfo($"{PluginName} {PluginVersion} loaded. cfg: quality={(Quality == 0 ? "auto" : Quality.ToString())} "
                        + $"nativeBackbuffer={cfgNativeBackbuffer.Value} "
                        + $"target={(cfgTargetWidth.Value > 0 ? cfgTargetWidth.Value + "x" + cfgTargetHeight.Value : "auto")} "
                        + $"aspect={Presentation.Mode}");
            Log.LogInfo($"gfx: '{SystemInfo.graphicsDeviceVersion}' device='{SystemInfo.graphicsDeviceName}' "
                        + $"-> Futile.isOpenGL will be {SystemInfo.graphicsDeviceVersion.Contains("OpenGL")}");
            ProbeNative();
        }

        public void OnDisable()
        {
            if (!hooksApplied) return;
            On.FScreen.ctor -= FScreen_ctor;
            On.FScreen.ReinitRenderTexture -= FScreen_ReinitRenderTexture;
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

        /// <summary>
        /// The automatic scale: the smallest integer multiple of the logical screen that covers the
        /// PICTURE - the letterboxed area the render actually lands in, not the raw backbuffer. The
        /// difference matters on ultrawides: on 3440x1440 the picture is ~2561x1440, so the right answer
        /// is ceil(1440/768) = 2, while raw screen width would say ceil(3440/1366) = 3 and waste ~2.4x
        /// the fill on pixels that are then thrown away by the downscale.
        /// </summary>
        private static int AutoFitScale(FScreen s)
        {
            int pw = Mathf.Max(1, s.pixelWidth), ph = Mathf.Max(1, s.pixelHeight);
            float sw = Mathf.Max(1, Screen.width), sh = Mathf.Max(1, Screen.height);

            float picW = sw, picH = sh;
            if (Presentation.Mode == AspectMode.Letterbox)
            {
                float logicalAspect = (float)pw / ph;
                picW = Mathf.Min(sw, sh * logicalAspect);
                picH = picW / logicalAspect;
            }

            int sx = Mathf.CeilToInt(picW / pw);
            int sy = Mathf.CeilToInt(picH / ph);
            return Mathf.Clamp(Mathf.Max(sx, sy), 1, 8);
        }

        /// <summary>
        /// The scale actually applied: the configured one (or the automatic fit for 0), clamped so the
        /// render texture stays within <see cref="SystemInfo.maxTextureSize"/> - an oversized Create()
        /// fails silently and yields a black screen.
        /// </summary>
        private static int EffectiveScale(FScreen s)
        {
            int want = Quality == 0 ? AutoFitScale(s) : Mathf.Clamp(Quality, 1, 8);
            int pw = Mathf.Max(1, s.pixelWidth), ph = Mathf.Max(1, s.pixelHeight);

            int maxDim = SystemInfo.maxTextureSize;
            if (maxDim <= 0) maxDim = 8192;                 // unknown driver: assume the D3D10 floor
            int cap = Mathf.Max(1, Mathf.Min(maxDim / pw, maxDim / ph));

            int eff = Mathf.Clamp(want, 1, cap);
            if (eff != want && loggedClampOf != want)
            {
                loggedClampOf = want;
                Log.LogWarning($"quality {want} would need a {pw * want}x{ph * want} render texture, "
                               + $"past this GPU's {maxDim}px limit. Clamped to {eff} "
                               + $"({pw * eff}x{ph * eff}).");
            }

            if (eff >= 3 && Quality != 0 && loggedCostOf != eff)
            {
                loggedCostOf = eff;
                long px = (long)pw * eff * ph * eff;
                Log.LogInfo($"quality {eff}: {pw * eff}x{ph * eff}, {px / 1000000f:F1} MP/frame "
                            + $"(~{px * 4L / 1048576L} MB for the target). Cost is room-dependent "
                            + "(unnamed GrabPasses copy the whole target per drawing object).");
            }
            return eff;
        }

        private static void SetRenderScale(FScreen s, int scale)
        {
            // Prefer the real (private) setter so we do not depend on the compiler's backing-field name.
            MethodInfo setter = renderScaleProp?.GetSetMethod(true);
            if (setter != null)
            {
                setter.Invoke(s, new object[] { scale });
                return;
            }
            if (renderScaleField != null)
            {
                renderScaleField.SetValue(s, scale);
                return;
            }
            Log.LogError("Could not set FScreen.renderScale - supersampling disabled.");
        }

        /// <summary>
        /// Makes renderScale (and thereby the render texture) match the configured quality. Safe to call
        /// repeatedly. The rebuild goes through the game's own ReinitRenderTexture, so the filter choice
        /// and the shader half-texel offset are recomputed exactly the way the game expects - with an
        /// integer-multiple target both are correct as shipped, which is why this plugin overrides
        /// neither.
        /// </summary>
        private static void EnsureRenderScale(FScreen s)
        {
            if (s == null || rebuilding) return;

            int target = EffectiveScale(s);
            if (s.renderScale == target) return;

            SetRenderScale(s, target);
            if (s.renderScale != target)
            {
                Log.LogError($"renderScale is still {s.renderScale} after trying to set "
                             + $"{target} - reflection failed, supersampling is OFF.");
                return;
            }

            rebuilding = true;
            try
            {
                // Re-run with the current width: this releases the old texture and allocates a new
                // one at pixelWidth * renderScale x pixelHeight * renderScale.
                s.ReinitRenderTexture(s.pixelWidth);
            }
            finally
            {
                rebuilding = false;
            }
            Log.LogInfo($"render texture is now {s.pixelWidth * s.renderScale}x"
                        + $"{s.pixelHeight * s.renderScale} "
                        + $"(logical {s.pixelWidth}x{s.pixelHeight}, {s.renderScale}x"
                        + (Quality == 0 ? ", auto" : "") + ")");
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

        private static void FScreen_ctor(On.FScreen.orig_ctor orig, FScreen s, FutileParams futileParams)
        {
            orig(s, futileParams);
            // The constructor just pinned renderScale to 1 and allocated a 1x texture; upgrade it.
            try { EnsureRenderScale(s); }
            catch (Exception e) { Log.LogError("FScreen ctor postfix failed, supersampling is OFF: " + e); }
        }

        private static void FScreen_ReinitRenderTexture(
            On.FScreen.orig_ReinitRenderTexture orig, FScreen s, int displayWidth)
        {
            orig(s, displayWidth);
            // orig() reallocated using the *current* renderScale; re-check in case the wanted scale
            // changed (an options-menu resolution change goes through here, and in auto mode the right
            // scale can shift when the backbuffer does).
            try
            {
                if (!rebuilding) EnsureRenderScale(s);
            }
            catch (Exception e) { Log.LogError("ReinitRenderTexture postfix failed: " + e); }
        }

        private static void Futile_Init(On.Futile.orig_Init orig, Futile f, FutileParams futileParams)
        {
            orig(f, futileParams);
            try
            {
                FScreen s = Futile.screen;
                if (s != null && s.renderTexture != null)
                    Log.LogInfo($"FScreen: logical {s.pixelWidth}x{s.pixelHeight} renderScale={s.renderScale} "
                                + $"RT={s.renderTexture.width}x{s.renderTexture.height} "
                                + $"filter={s.renderTexture.filterMode} | backbuffer {Screen.width}x{Screen.height} "
                                + $"| isOpenGL={Futile.isOpenGL}");
            }
            catch (Exception e) { Log.LogError("Futile.Init postfix failed: " + e); }
        }

        /// <summary>
        /// Futile.UpdateCameraPosition is the only writer of _cameraImage.uvRect and
        /// Futile.subjectToAspectRatioIrregularity in the whole game, and it does not touch the
        /// RectTransform - so the letterbox geometry is never undone and only needs re-asserting here.
        /// This covers every path that can change it: Futile.Init, Futile.UpdateScreenWidth and the
        /// FScreen.originX/originY setters.
        /// </summary>
        private static void Futile_UpdateCameraPosition(On.Futile.orig_UpdateCameraPosition orig, Futile f)
        {
            orig(f);
            try { Presentation.Apply(); }
            catch (Exception e) { Log.LogError("UpdateCameraPosition postfix failed: " + e); }
        }

        private void Options_OnLoadFinished(On.Options.orig_OnLoadFinished orig, Options o)
        {
            // Never swallow orig(): if it throws the game is half-initialised and continuing is worse.
            orig(o);
            try
            {
                lastOptions = o;
                if (!cfgNativeBackbuffer.Value || !o.fullScreen) return;

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
                    RefitAutoScale();
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

            RefitAutoScale();

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

        /// <summary>
        /// In automatic mode the correct scale depends on the backbuffer, which is not final until our
        /// request lands (the FScreen constructor usually runs against the desktop resolution, which
        /// gives the same answer - but ordering is not guaranteed on every machine). Rebuild through the
        /// game's own UpdateScreenWidth, the only path that also rebinds the cameras and the RawImage.
        /// </summary>
        private void RefitAutoScale()
        {
            if (Quality != 0) return;
            FScreen s = Futile.screen;
            if (s == null || Futile.instance == null || rebuilding) return;
            if (s.renderScale == EffectiveScale(s)) return;

            Log.LogInfo("auto quality: backbuffer changed, refitting the render scale");
            Futile.instance.UpdateScreenWidth(s.pixelWidth);
        }
    }
}
