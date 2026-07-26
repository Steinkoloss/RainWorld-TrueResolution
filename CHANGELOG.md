# Changelog

All notable changes to this mod. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions are the ones
that appear in `mod/modinfo.json`, in the Remix mod list, and on the Steam
Workshop page — those three must always agree, and CI fails the build if they
do not.

`mod/modinfo.json` is the single source of truth for the version number. The
csproj reads `$(Version)` out of it, and `tools/check-metadata.py` asserts that
`src/Plugin.cs` and this file match.

## 1.3.1

### Fixed
- `Downsample = Auto` enabled the mip chain on *any* minification, including
  ratios barely above 1:1, where it is a net loss. Trilinear blends `log2(ratio)`
  of a half-resolution level into the result, so at 1.07x — exactly the default
  `Supersample = 2` on a 1440p screen — it paid ~10% of a half-res blur to
  suppress aliasing that a 4-tap bilinear was already handling. Auto now requires
  at least 1.5x minification, on the reasoning that a bilinear tap covers a 2x2
  texel neighbourhood and so stops covering the footprint as the ratio nears 2.
  `MipmapBox` still forces it on.
- Consequence: at the default `Supersample = 2` the mip chain is now inert on
  every common display, which is correct — it only earns its keep at 3 and above.

## 1.3.0

### Added
- **In-game settings page**, in the Remix config menu (main menu -> Remix ->
  True Resolution -> cog). Deliberately plain: one tab, one control per setting,
  and a live status line reading `logical -> render -> screen` so you can see
  what you actually got rather than what you asked for. Exposes Supersample,
  Downsample filter, Aspect fit and Native backbuffer; the rarely-needed
  `TargetWidth`/`TargetHeight` and `LegacyScreenOffset` stay in the config file.
- Settings apply immediately. A Supersample or Downsample change rebuilds through
  the game's own `Futile.UpdateScreenWidth`, which is the only path that also
  rebinds `camera.targetTexture` and the presenting `RawImage` — going through
  `ReinitRenderTexture` directly would leave both pointing at a released texture
  when the scale shrinks.
- `Presentation.Restore()`, so switching *away* from Letterbox at runtime hands
  the `RawImage` back to the game (full-stretch anchors, backdrop destroyed,
  cursor patch removed) instead of leaving it fitted to the previous aspect.

### Changed
- The BepInEx config file remains the single source of truth. The page seeds its
  controls from it whenever the menu opens and writes back on Apply, so hand-editing
  the `.cfg` and using the page cannot disagree. Remix's own persisted copy is ignored.
- Registration happens in `On.RainWorld.OnModsInit` and is non-fatal: if
  `SetRegisteredOI` fails the mod logs it and keeps working from the config file.

## 1.2.0

### Added
- `Downsample` option, default `Auto`. Gives the render texture a mip chain and
  samples it trilinearly whenever it is larger than the backbuffer, so the GPU
  builds a box-filtered pyramid and every source pixel contributes instead of
  only the nearest four. This is the best downsample obtainable without shipping
  a custom shader — Unity cannot compile ShaderLab at runtime, so a
  Lanczos/Mitchell kernel would require an AssetBundle built in the editor, and
  for pure minification a box pyramid is close to optimal anyway. `useMipMap`
  can only be set before a texture is created, so the render target is
  reallocated and the three references to it (both Futile cameras'
  `targetTexture` and the presenting `RawImage`) are rebound.
  Modes: `Auto`, `MipmapBox`, `Bilinear`, `Point`.
- `Supersample` now accepts up to 8 (was 4). Clamped automatically so the render
  texture stays inside `SystemInfo.maxTextureSize`, because an oversized
  `Create()` fails silently and yields a black screen. Warns above 4 with the
  actual megapixel and memory cost.

### Changed
- Point filtering is still chosen for a 1:1 or exact-integer composite, where it
  is genuinely sharpest and a mip chain would only blur. Trilinear is only
  requested when mips actually exist, since Unity otherwise silently treats it
  as bilinear.

### Not done: FSR 4 / DLSS 4
Investigated and rejected on four independent grounds, recorded here so it is
not re-litigated:
- The game renders on **D3D11**; FSR 4 is a D3D12 implementation reached through
  AMD's `amdxcffx64.dll`. There is no D3D11 entry point.
- FSR 4 and DLSS 2+ are **temporal**. They require per-pixel motion vectors;
  `motionVector` appears nowhere in the assembly, the presented render texture is
  allocated with **zero depth bits**, and the camera is orthographic 2D.
  Synthesising motion vectors would mean replicating the vertex animation of
  ~300 shaders in a second pass.
- Rain World's camera **hard-cuts** between fixed per-room positions, the
  worst-case input for a temporal accumulator.
- The payoff would be negative: temporal upscalers recover sub-pixel detail by
  accumulating jittered samples, and a pixel-locked 2D game sampling fixed
  1400×800 art has none to recover. Expect ghosting, gain nothing.

DLSS additionally requires an NVIDIA GPU. FSR 1 remains theoretically viable —
it is purely spatial — but needs a shader shipped in an AssetBundle.

## 1.1.0

First public release. 1.0.0 and 1.0.1 below were built and runtime-verified
locally but never published; they are kept as development history.

### Added
- **Correct presentation on any display aspect ratio** (`AspectMode`, default
  `Letterbox`). The render texture is presented as a UGUI `RawImage` that
  stretches to fill the backbuffer, so on a 21:9, 32:9, 4:3 or 16:10 panel the
  ~16:9 picture came out anisotropically distorted. The backbuffer now stays at
  the panel's native size and the picture is fitted inside it, centred, with real
  black bars drawn behind it — a mod-owned quad, because nothing in Rain World
  ever clears the backbuffer. The fit is computed in normalised parent space, so
  it is immune to the `CanvasScaler`, to `referenceResolution` and to
  `ScreenSafeArea` insets. A 16:9 display is unaffected.
- Letterbox-aware cursor mapping. `Futile.mousePosition` maps the cursor with
  `Input.mousePosition * pixelWidth / Screen.width`, which assumes the picture
  covers the whole backbuffer, and every menu plus the entire Remix config UI
  reads it. A Harmony prefix on the getter divides by the picture's real
  rectangle instead. If that patch cannot be applied, aspect correction disables
  itself rather than leave every button offset by the width of the bars.
- While letterboxing, `uvRect` is forced to the identity and
  `Futile.subjectToAspectRatioIrregularity` cleared to match. Vanilla derives
  `uvRect` from the hardcoded constant `1.7786459f` (bit-identical to 1366÷768),
  so on the default 1360×768 it is `(0,0,0.9956,0.9956)` — vanilla silently crops
  and zooms slightly. The flag's only reader is a cosmetic `PixelShift` nudge.
- A diagnostic dump of the whole presentation chain.
- Cross-platform build: the csproj finds Rain World on Windows, Linux and macOS
  by reading every Steam library out of `libraryfolders.vdf`, and falls back to
  GOG/Epic/manual locations. `-p:RainWorldDir=…` and `RW_DIR` still override.
  Failure is a single actionable error (`RWFIX001`) instead of nine `CS0246`s.
- `install.ps1` for Windows, plus a foolproof manual install path in the README
  for users without the .NET SDK.
- `tools/make-refs.sh`, which generates stripped reference assemblies from a
  local install so the project can build with no game present.
- `tools/check-metadata.py`, which validates `modinfo.json`, the version
  agreement across all four files, and whether `thumbnail.png` satisfies the
  in-game Workshop uploader's size and aspect checks.
- `tools/release.sh` — build, validate, package and publish from a machine that
  has the game. No CI or secrets required.
- GitHub Actions: metadata and layout checks that run on every PR including from
  forks, a Windows job that parses and exercises `install.ps1`, an optional
  compile job, and a tag-triggered release workflow.
- `target_game_version: "v1.11"` and Workshop/RainDB `tags` in `modinfo.json`.
- MIT licence, `.gitignore` that actively refuses game assemblies, issue
  templates, and `docs/RELEASING.md` covering Workshop and RainDB submission.

### Changed
- `BepInEx.dll`, `0Harmony.dll`, `UnityEngine.dll`, `UnityEngine.CoreModule.dll`,
  `UnityEngine.UIModule.dll` and `UnityEngine.InputLegacyModule.dll` now come
  from NuGet at versions pinned to exactly what the game ships (BepInEx.Core
  5.4.17, HarmonyX 2.5.5, UnityEngine.Modules 2020.3.45) instead of being read
  out of the install. Only three assemblies still require a game copy:
  `Assembly-CSharp`, `HOOKS-Assembly-CSharp` and `UnityEngine.UI`.
- The build stages `bin/mod/trueresolution/` in the exact layout the game
  requires, so the installers and the release zip copy an already-correct tree.

## 1.0.1

### Fixed
- Filter mode is chosen from the render-texture-to-backbuffer ratio rather than
  from `renderScale`. Point sampling is only correct at 1:1 or an exact integer
  multiple; the previous rule made a 1360 → 2560 (1.88×) composite look worse
  than vanilla.
- The backbuffer request is always re-issued from `Options.OnLoadFinished`
  rather than compared against the live `Screen` size. `Screen.SetResolution` is
  applied at the next frame boundary, so comparing against `Screen.width` there
  early-returns and lets the game's own shrink win.

## 1.0.0

### Added
- Supersampling: raises `FScreen.renderScale` from 1 to N (default 2) and
  rebuilds the render texture through the game's own `ReinitRenderTexture`, so
  the shader half-texel offset is recomputed the way the game expects. The
  Futile camera's `orthographicSize` derives from `pixelHeight`, not from the
  render texture, so framing is invariant by construction.
- Native backbuffer: in fullscreen, `Screen.SetResolution(w, h,
  FullScreenWindow)` at the display's real resolution, verified with a bounded
  retry loop because `SetResolution` is a request with no failure signal.
- Display probe at plugin load, before `Options.OnLoadFinished` can shrink the
  backbuffer, so the detected native size can never be a read-back of a size
  this mod itself forced.
- Config: `Supersample`, `NativeBackbuffer`, `TargetWidth`/`TargetHeight`,
  `SmoothDownsample`, `LegacyScreenOffset`.

### Deliberately not done
- `FScreen.pixelWidth`, `FScreen.pixelHeight`, `Options.screenResolutions` and
  `Options.ScreenSize` are never written. They are world units as well as
  pixels: `RoomCamera.GetVisibleRect` returns `new Rect(pos, sSize)` and room
  art is a fixed 1400×800 PNG per screen, so enlarging them shows void past the
  art and breaks roughly forty culling and camera-switch predicates.
