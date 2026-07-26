using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace TrueResolution
{
    /// <summary>Filter used to bring the supersampled render texture down to the backbuffer.</summary>
    internal enum DownsampleMode
    {
        /// <summary>MipmapBox while minifying, Bilinear otherwise. Recommended.</summary>
        Auto = 0,

        /// <summary>Mip chain + trilinear: a box-filtered pyramid. Best available without a shader.</summary>
        MipmapBox = 1,

        /// <summary>A single 4-tap bilinear sample.</summary>
        Bilinear = 2,

        /// <summary>Nearest neighbour.</summary>
        Point = 3
    }

    /// <summary>How the (at most 1366x768, ~16:9) logical picture is fitted into the backbuffer.</summary>
    internal enum AspectMode
    {
        /// <summary>Do nothing. The RawImage keeps stretching to fill the backbuffer (vanilla).</summary>
        Stretch = 0,

        /// <summary>
        /// RECOMMENDED. Backbuffer stays at the panel's native size; the picture is fitted inside it
        /// with real black bars drawn by us. Deterministic on every platform and graphics API.
        /// </summary>
        Letterbox = 1,

        /// <summary>
        /// Legacy/fallback. Ask Unity for a backbuffer that already has the logical aspect ratio and
        /// let FullScreenMode.FullScreenWindow letterbox it. Documented to work, observed to fail on
        /// several API/platform combinations - see README. No mouse correction needed in this mode.
        /// </summary>
        AspectBackbuffer = 2
    }

    /// <summary>
    /// Owns everything about how <c>Futile.screen.renderTexture</c> reaches the display.
    ///
    /// Rain World presents the render texture as a UGUI <see cref="RawImage"/> (<c>Futile._cameraImage</c>,
    /// Futile.cs:61) inside a ScreenSpaceOverlay Canvas. That RawImage stretches to fill the whole
    /// backbuffer, so once the backbuffer stops having the logical aspect ratio the picture is
    /// anisotropically distorted. This class fits the RawImage's RectTransform to the logical aspect
    /// ratio instead, and repairs the two things that then go wrong:
    ///
    ///  1. <c>Futile.UpdateCameraPosition</c> (Futile.cs:507-511) derives <c>_cameraImage.uvRect</c> from
    ///     <c>canvasScaler.referenceResolution</c> vs the hardcoded constant 1.7786459f. That constant is
    ///     bit-identical to 1366f/768f (both 0x3FE3AAAB), so the uvRect is only the identity on a
    ///     1366x768 logical screen. On 1360x768 - the default in localoptions.txt - it is
    ///     (0,0,0.9956076,0.9956076), i.e. vanilla silently crops the top/right of the render texture and
    ///     zooms in. Once we own the destination rectangle the source rectangle must be the whole
    ///     texture, so we force it back to the identity and clear
    ///     <c>Futile.subjectToAspectRatioIrregularity</c> to match.
    ///
    ///  2. <c>Futile.mousePosition</c> (Futile.cs:95-108) maps the cursor with
    ///     <c>Input.mousePosition * pixelWidth / Screen.width</c>, which assumes the picture covers the
    ///     entire backbuffer. Every menu in the game reads it through <c>Menu.mousePosition</c>
    ///     (Menu/Menu.cs:319) and the Remix config UI reads it through
    ///     <c>UIelement.MousePos</c> (Menu.Remix.MixedUI/UIelement.cs:217), so a letterbox without this
    ///     correction makes every button in the game unclickable at the wrong offset.
    /// </summary>
    internal static class Presentation
    {
        internal static AspectMode Mode = AspectMode.Letterbox;

        // ---- resolved once, from Futile.instance
        private static FieldInfo cameraImageField;
        private static RawImage image;
        private static RectTransform imageRT;
        private static Canvas canvas;
        private static Image backdrop;
        private static bool resolveFailed;

        // ---- the picture's rectangle in real backbuffer pixels, origin bottom-left (Input.mousePosition
        //      space). Recomputed every frame; the mouse mapping is driven from this and nothing else.
        private static Rect picture;
        private static bool pictureValid;

        private static readonly Vector3[] worldCorners = new Vector3[4];
        private static readonly Rect IdentityUV = new Rect(0f, 0f, 1f, 1f);

        // ---- change detection, so a steady state costs no UGUI invalidation at all
        private static int lastBackbufferW, lastBackbufferH;
        private static float lastFx = -1f, lastFy = -1f;
        private static bool loggedOnce;

        private static Harmony harmony;

        internal static bool Active => Mode == AspectMode.Letterbox;

        // ------------------------------------------------------------------ lifecycle

        internal static void InstallMousePatch()
        {
            if (harmony != null) return;
            try
            {
                // Futile.mousePosition is a static property with no setter, and HookGen does NOT emit
                // On.Futile.get_mousePosition (verified: HOOKS-Assembly-CSharp.dll contains
                // orig_UpdateCameraPosition but no orig_get_mousePosition), so a Harmony prefix on the
                // getter is the only hook point. 0Harmony.dll 2.5.5.0 exists exactly once in the install
                // (BepInEx/core) so there is no assembly-identity ambiguity.
                MethodInfo getter = typeof(Futile)
                    .GetProperty("mousePosition", BindingFlags.Public | BindingFlags.Static)
                    ?.GetGetMethod(true);
                if (getter == null)
                    throw new MissingMemberException("Futile.mousePosition getter not found");

                harmony = new Harmony(Plugin.PluginGuid);
                harmony.PatchAll(typeof(FutileMousePositionPatch));

                // Never trust PatchAll silently: an unpatched getter means every menu button in the game
                // is offset by the size of the black bars, which is far worse than not letterboxing.
                bool patched = false;
                foreach (MethodBase m in Harmony.GetAllPatchedMethods())
                {
                    if (m == getter) { patched = true; break; }
                }
                if (!patched)
                    throw new InvalidOperationException(
                        "Harmony reported no patch on Futile.get_mousePosition");

                Plugin.Log.LogInfo("mouse: patched Futile.get_mousePosition for letterbox-aware mapping");
            }
            catch (Exception e)
            {
                harmony = null;
                Plugin.Log.LogError("mouse: FAILED to patch Futile.get_mousePosition. Letterboxing would "
                                    + "misplace the cursor, so aspect correction is disabled: " + e);
                Mode = AspectMode.Stretch;
            }
        }

        internal static void RemoveMousePatch()
        {
            if (harmony == null) return;
            try { harmony.UnpatchSelf(); }
            catch (Exception e) { Plugin.Log.LogWarning("mouse: UnpatchSelf failed: " + e.Message); }
            harmony = null;
        }

        internal static void Reset()
        {
            image = null; imageRT = null; canvas = null; backdrop = null;
            resolveFailed = false; pictureValid = false;
            lastBackbufferW = lastBackbufferH = 0;
            lastFx = lastFy = -1f;
        }

        /// <summary>
        /// Hand the RawImage back to the game: full-stretch anchors, no backdrop, and stop correcting the
        /// cursor. Needed when the user switches away from Letterbox at runtime, otherwise the picture
        /// stays fitted to the aspect ratio we last computed. Leaves uvRect alone - Futile rewrites it in
        /// UpdateCameraPosition, which is exactly the vanilla behaviour we are restoring.
        /// </summary>
        internal static void Restore()
        {
            RemoveMousePatch();
            pictureValid = false;

            if (imageRT != null)
            {
                imageRT.anchorMin = Vector2.zero;
                imageRT.anchorMax = Vector2.one;
                imageRT.sizeDelta = Vector2.zero;
                imageRT.anchoredPosition = Vector2.zero;
            }

            if (backdrop != null)
            {
                UnityEngine.Object.Destroy(backdrop.gameObject);
                backdrop = null;
            }

            // Force Apply() to recompute from scratch if Letterbox is switched back on.
            lastFx = lastFy = -1f;
            lastBackbufferW = lastBackbufferH = 0;

            Plugin.Log.LogInfo("presentation: restored vanilla full-stretch presentation");
        }

        /// <summary>
        /// Repoint the presenting RawImage at a different render texture, used when the render target is
        /// swapped for a mipmapped one. Presentation owns the RawImage reference, so it owns the rebind.
        /// A no-op before Futile.Init has produced the RawImage, which is harmless: Futile.Init
        /// (Futile.cs:222) and UpdateScreenWidth (Futile.cs:283) assign the texture themselves.
        /// </summary>
        internal static void RebindTexture(RenderTexture rt)
        {
            if (!Resolve()) return;
            if (image != null) image.texture = rt;
        }

        // ------------------------------------------------------------------ resolve

        private static bool Resolve()
        {
            if (image != null) return true;
            if (resolveFailed) return false;

            Futile f = Futile.instance;
            if (f == null) return false;

            if (cameraImageField == null)
            {
                cameraImageField = typeof(Futile).GetField(
                    "_cameraImage", BindingFlags.Instance | BindingFlags.NonPublic);
                if (cameraImageField == null)
                {
                    resolveFailed = true;
                    Plugin.Log.LogError("Futile._cameraImage not found - aspect correction disabled.");
                    return false;
                }
            }

            image = cameraImageField.GetValue(f) as RawImage;
            if (image == null) return false;          // Futile.Init has not run far enough yet

            imageRT = image.rectTransform;
            canvas = image.canvas;
            if (imageRT == null) { image = null; return false; }

            if (!loggedOnce)
            {
                loggedOnce = true;
                Plugin.Log.LogInfo($"presentation: RawImage='{image.name}' parent='"
                    + $"{(imageRT.parent != null ? imageRT.parent.name : "<none>")}' canvas='"
                    + $"{(canvas != null ? canvas.name : "<none>")}' mode="
                    + $"{(canvas != null ? canvas.renderMode.ToString() : "?")} "
                    + $"anchorMin={imageRT.anchorMin} anchorMax={imageRT.anchorMax} "
                    + $"uvRect={image.uvRect}");
            }
            return true;
        }

        /// <summary>
        /// A full-canvas black quad behind the RawImage. This is NOT optional: the Futile camera targets
        /// the render texture (Futile.cs:247) and the only other cameras in the game target their own
        /// render textures too (Menu.Remix.MixedUI/OpScrollBox.cs:249), so nothing in Rain World ever
        /// clears the backbuffer. The bars would otherwise be whatever the driver last left there -
        /// exactly the "black bands are not refreshing" artefact people hit with Unity's own letterboxing.
        /// </summary>
        private static void EnsureBackdrop()
        {
            if (backdrop != null) return;
            Transform parent = canvas != null ? canvas.transform : (imageRT != null ? imageRT.parent : null);
            if (parent == null) return;

            GameObject go = new GameObject("TrueResolutionLetterboxBackdrop", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            RectTransform rt = (RectTransform)go.transform;
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = Vector2.zero;
            rt.anchoredPosition = Vector2.zero;

            backdrop = go.AddComponent<Image>();
            backdrop.color = Color.black;
            backdrop.raycastTarget = false;
            // UGUI draws siblings in hierarchy order, so index 0 is behind everything else on the canvas.
            go.transform.SetAsFirstSibling();

            Plugin.Log.LogInfo("presentation: created letterbox backdrop under '" + parent.name + "'");
        }

        // ------------------------------------------------------------------ fit

        private static Vector2 LogicalSize()
        {
            FScreen s = Futile.screen;
            if (s != null && s.pixelWidth > 0 && s.pixelHeight > 0)
                return new Vector2(s.pixelWidth, s.pixelHeight);
            return Vector2.zero;
        }

        /// <summary>
        /// Fractions of the parent rect the picture must occupy so that its aspect ratio equals the
        /// logical aspect ratio, maximised and centred. Working in normalised parent space (rather than
        /// pixels, as Sharpener does) makes this immune to the CanvasScaler's scaleFactor, to
        /// referenceResolution, and to ScreenSafeArea's insets (ScreenSafeArea.cs:50-51) - so unlike
        /// Sharpener we never have to disable the CanvasScaler, and Futile.UpdateCameraPosition's read of
        /// canvasScaler.referenceResolution (Futile.cs:507) cannot start returning something unexpected.
        /// </summary>
        private static bool ComputeFit(out float fx, out float fy)
        {
            fx = 1f; fy = 1f;

            Vector2 logical = LogicalSize();
            if (logical.x <= 0f || logical.y <= 0f) return false;

            // Prefer the parent's own rect: its aspect is the aspect of the area we are allowed to fill,
            // insets included. Fall back to the backbuffer.
            float hostW, hostH;
            RectTransform parent = imageRT != null ? imageRT.parent as RectTransform : null;
            if (parent != null && parent.rect.width > 0f && parent.rect.height > 0f)
            {
                hostW = parent.rect.width; hostH = parent.rect.height;
            }
            else
            {
                hostW = Screen.width; hostH = Screen.height;
            }
            if (hostW <= 0f || hostH <= 0f) return false;

            float want = logical.x / logical.y;
            float host = hostW / hostH;

            // want == host must leave fx==fy==1 exactly, so that a matched display does not pick up a
            // sub-pixel bar. 1360x768 vs a 16:9 panel is only 0.4% out and would otherwise produce a
            // 3-pixel bar nobody asked for; the epsilon absorbs it.
            const float eps = 0.005f;
            float ratio = want / host;
            if (ratio > 1f + eps) fy = 1f / ratio;      // host too tall  -> letterbox (bars top/bottom)
            else if (ratio < 1f - eps) fx = ratio;      // host too wide  -> pillarbox (bars left/right)

            return true;
        }

        // ------------------------------------------------------------------ apply

        /// <summary>
        /// Idempotent. Safe and cheap to call every frame: every write is guarded, and UGUI's own setters
        /// early-out on an unchanged value, so a steady state dirties nothing.
        /// </summary>
        internal static void Apply()
        {
            if (Mode != AspectMode.Letterbox) return;
            if (!Resolve()) return;

            float fx, fy;
            if (!ComputeFit(out fx, out fy)) return;

            EnsureBackdrop();

            if (fx != lastFx || fy != lastFy
                || Screen.width != lastBackbufferW || Screen.height != lastBackbufferH)
            {
                Vector2 min = new Vector2(0.5f - fx * 0.5f, 0.5f - fy * 0.5f);
                Vector2 max = new Vector2(0.5f + fx * 0.5f, 0.5f + fy * 0.5f);

                imageRT.anchorMin = min;
                imageRT.anchorMax = max;
                // With anchorMin != anchorMax, sizeDelta==0 makes the rect exactly the anchor rect and
                // anchoredPosition==0 centres it on that rect, for ANY pivot. This is the pivot-safe way
                // to say "fill the anchors" and is equivalent to offsetMin==offsetMax==zero.
                imageRT.sizeDelta = Vector2.zero;
                imageRT.anchoredPosition = Vector2.zero;
                imageRT.localScale = Vector3.one;
                imageRT.localRotation = Quaternion.identity;

                Vector2 logical = LogicalSize();
                Plugin.Log.LogInfo(
                    $"presentation: logical {logical.x}x{logical.y} (aspect {logical.x / Mathf.Max(1f, logical.y):F4}) "
                    + $"into backbuffer {Screen.width}x{Screen.height} "
                    + $"(aspect {(float)Screen.width / Mathf.Max(1, Screen.height):F4}) -> "
                    + $"fill {fx:F4}x{fy:F4} "
                    + (fx < 1f ? "PILLARBOX" : (fy < 1f ? "LETTERBOX" : "exact fit, no bars")));

                lastFx = fx; lastFy = fy;
                lastBackbufferW = Screen.width; lastBackbufferH = Screen.height;
            }

            // The source rectangle must be the whole texture now that we own the destination rectangle.
            // Both setters early-out when unchanged, so this is free.
            image.uvRect = IdentityUV;
            Futile.subjectToAspectRatioIrregularity = false;

            CachePictureRect();
        }

        /// <summary>
        /// Convert the RawImage's rect into Input.mousePosition space. Reading it back from the transform
        /// (instead of recomputing it) means the mouse mapping is correct by construction even if the
        /// CanvasScaler, ScreenSafeArea, or another mod moves the picture.
        /// </summary>
        private static void CachePictureRect()
        {
            pictureValid = false;
            if (imageRT == null) return;

            // For a ScreenSpaceOverlay canvas world space IS backbuffer-pixel space, which is what
            // WorldToScreenPoint(null, p) assumes. Passing the canvas camera keeps the other render
            // modes correct too, in case some future Rain World build changes it.
            Camera cam = (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                ? canvas.worldCamera
                : null;

            imageRT.GetWorldCorners(worldCorners);
            Vector2 bl = RectTransformUtility.WorldToScreenPoint(cam, worldCorners[0]); // bottom-left
            Vector2 tr = RectTransformUtility.WorldToScreenPoint(cam, worldCorners[2]); // top-right

            float w = tr.x - bl.x, h = tr.y - bl.y;
            if (w < 1f || h < 1f) return;   // degenerate - do not hand this to a divide

            picture = new Rect(bl.x, bl.y, w, h);
            pictureValid = true;
        }

        /// <summary>
        /// The replacement for Futile.cs:103-104. Vanilla divides by the whole backbuffer; we divide by
        /// the rectangle the picture actually occupies and subtract its origin first.
        /// Returns false to let the original getter run (startup, or anything not yet resolved).
        /// </summary>
        internal static bool TryMapMouse(out Vector3 result)
        {
            result = Vector3.zero;
            if (Mode != AspectMode.Letterbox || !pictureValid) return false;

            FScreen s = Futile.screen;
            if (s == null || s.pixelWidth <= 0 || s.pixelHeight <= 0) return false;

            Vector3 m = Input.mousePosition;
            result = new Vector3(
                (m.x - picture.x) * s.pixelWidth / picture.width,
                (m.y - picture.y) * s.pixelHeight / picture.height,
                0f);   // vanilla never writes z either
            return true;
        }

        /// <summary>Diagnostic: dump the whole presentation chain. Wired to a keybind by the plugin.</summary>
        internal static void DumpState()
        {
            Plugin.Log.LogInfo("---- presentation dump ----");
            Plugin.Log.LogInfo($"mode={Mode} backbuffer={Screen.width}x{Screen.height} "
                               + $"fs={Screen.fullScreen} fsMode={Screen.fullScreenMode}");
            FScreen s = Futile.screen;
            if (s != null)
                Plugin.Log.LogInfo($"logical={s.pixelWidth}x{s.pixelHeight} renderScale={s.renderScale} "
                                   + $"RT={(s.renderTexture != null ? s.renderTexture.width + "x" + s.renderTexture.height : "null")} "
                                   + $"filter={(s.renderTexture != null ? s.renderTexture.filterMode.ToString() : "?")}");
            if (imageRT != null)
            {
                Plugin.Log.LogInfo($"anchorMin={imageRT.anchorMin} anchorMax={imageRT.anchorMax} "
                                   + $"sizeDelta={imageRT.sizeDelta} anchoredPos={imageRT.anchoredPosition} "
                                   + $"rect={imageRT.rect}");
                Plugin.Log.LogInfo($"uvRect={image.uvRect} irregular={Futile.subjectToAspectRatioIrregularity}");
                Plugin.Log.LogInfo($"pictureRect(px)={picture} valid={pictureValid} backdrop={(backdrop != null)}");
                RectTransform parent = imageRT.parent as RectTransform;
                if (parent != null) Plugin.Log.LogInfo($"parent '{parent.name}' rect={parent.rect}");
            }
            else Plugin.Log.LogInfo("camera image not resolved");
        }
    }

    /// <summary>
    /// Harmony prefix on the <c>Futile.mousePosition</c> getter (Futile.cs:95-108). Skipping the original
    /// is safe: <c>_mousePosition</c>/<c>_mousePositionValid</c> are private and read nowhere else
    /// (verified by grep over the decompiled tree), and <c>Futile.LateUpdate</c>'s
    /// <c>_mousePositionValid = false</c> (Futile.cs:473) stays harmless.
    /// </summary>
    [HarmonyPatch(typeof(Futile), "mousePosition", MethodType.Getter)]
    internal static class FutileMousePositionPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(ref Vector3 __result)
        {
            Vector3 mapped;
            if (!Presentation.TryMapMouse(out mapped)) return true;   // fall through to vanilla
            __result = mapped;
            return false;                                            // skip vanilla
        }
    }
}
