# Changelog

All notable changes to this mod. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions are the ones
that appear in `mod/modinfo.json`, in the Remix mod list, and on the Steam
Workshop page — those three must always agree, and CI fails the build if they
do not.

`mod/modinfo.json` is the single source of truth for the version number. The
csproj reads `$(Version)` out of it, and `tools/check-metadata.py` asserts that
`src/Plugin.cs` and this file match.

## 1.7.0

Zero-config release: everything important now happens automatically, and the
development-only knobs from the 1.6.x native-mode investigation are gone.

### Changed
- **`RenderQuality` replaces `Supersample`, default `0` = automatic**: the
  smallest integer scale whose render texture covers the *displayed picture* —
  2× on 1080p and 1440p, 3× on 4K. On ultrawides the fit is computed against the
  letterboxed picture, not the raw screen, so 3440×1440 correctly picks 2× rather
  than wasting 2.4× the fill on discarded pixels. The scale re-fits itself if the
  backbuffer changes mid-session.
- **The settings page is one slider** (Render quality, default Auto) plus, on
  non-16:9 displays only, the Stretch tick box. Smooth scaling is gone — Point
  sampling is correct for this art, and the game's own filter selection already
  handles the one case (sub-768p displays) where bilinear is right.
- **Everything is integer-scaled by design.** The display-sized "true native"
  render texture is deleted rather than optional: pixel-level captures proved the
  sprites carry a baked one-texel black outline that a non-integer world-to-pixel
  ratio must render unevenly (Point) or smear into a halo (Bilinear). With
  integer targets, the game's own filter choice, half-texel offset and camera
  aspect are exactly correct — so the camera-aspect pin, the generalised screen
  offset, the render-target swap machinery and the atlas-filter repair are all
  deleted too. The plugin is roughly half its former size.

### Removed
- Debug tooling: the Ctrl+Alt alignment-nudge hotkeys, the Ctrl+Alt+P
  render-texture capture, `AlignX`/`AlignY`, `LegacyScreenOffset`.
- Config: `Supersample` (renamed), `Downsample`, `SmoothDownsample`,
  `TrueNativeRT`. Old keys in existing config files are ignored harmlessly.
- The mip-chain downsampler: it only ever mattered for smooth minification at
  3×+, which no longer exists as a shipped mode.

## 1.6.2

### Fixed
- **The one-pixel dark seam in Native mode, properly this time.** It flips side
  across the middle of the screen — left of a sprite in the left half, right of it
  in the right half — which rules out a constant offset and means a *scale* error.
  Futile never assigns `camera.aspect`, so Unity derives it from the render
  target. In Native mode that is the display, so on 2560x1440 with a 1366x768
  logical screen the camera renders `768 * (2560/1440)` = 1365.33 world units of
  width while every screen-space shader is told `_screenSize.x = 1366`. The 0.049%
  difference is nothing as framing, but as a scale error it drifts sprite geometry
  against the screen-space textures those sprites sample by up to ±0.62 render
  pixels — zero at centre, opposite directions on the two halves.
  `camera.aspect` is now pinned to the logical aspect whenever the target is not an
  exact integer multiple, so the camera renders exactly `pixelWidth x pixelHeight`
  world units and registration is exact everywhere. Render pixels become 0.049%
  non-square, which is uniform, invisible, and cannot produce an edge.
  The 1.6.0 changelog called that 0.05% "negligible" — negligible as framing, not
  as registration.

## 1.6.1

### Fixed
- **One-pixel dark seam along one edge of sprites in Native mode.**
  `FScreen.UpdateScreenOffset` assumes the render texture is exactly
  `pixelWidth/Height * renderScale`, and its `renderScale != 1` branch shifts the
  camera a whole world unit while handing shaders a half-texel of
  `0.5/pixelWidth`. A native 2560x1440 target against a 1366x768 logical screen
  has 1.875 render pixels per world unit, so that correction was ~1.875x too
  large: screen-space sampling landed about a pixel off and left a seam on
  whichever side the offset pushed toward.
  Alignment is now derived from the real target size whenever it is not an exact
  integer multiple of the logical screen, using the shipped `renderScale == 1`
  semantics generalised to any size (shift by half a *render texture* pixel, no
  shader-side correction). It reduces exactly to vanilla when the two match.
  The integer path is untouched — it is visually verified and worth no risk.
  Note the branch this replaces is dead code in stock Rain World, so it had never
  been exercised by anyone.

## 1.6.0

### Added
- **Native render mode**, `Render quality = 0`. Sizes the render texture to your
  display exactly instead of to an integer multiple of the internal buffer, giving
  a **1:1 composite with no resampling at all** — the sharpest possible result for
  everything drawn procedurally, at a fraction of the fill cost (3.7 MP at 1440p
  versus 67 MP at 8x). Legal for the same reason supersampling is: the Futile
  camera's `orthographicSize` comes from `pixelHeight` and never from the render
  texture, so world framing does not move. `camera.aspect` is derived from the
  target's dimensions, so the visible world width shifts by the difference between
  the logical and display aspect ratios — 0.05%, under one world unit.
- Which of Native, 2 or 4 looks best on the *room artwork* is genuinely a matter
  of taste: Native magnifies it by a non-integer factor with hard pixels, while
  supersampling quantises the edges more finely first. Worth an A/B.

### Changed
- The render-target logic is now one function that conforms size *and* mip state
  together, rather than a mip-only path that could not resize.

## 1.5.0

### Changed
- **Hard pixels are now the default.** Rain World is pixel art and nearest-neighbour
  scaling suits it, so `Downsample` defaults to `Point` and the settings page
  offers **Smooth scaling** as an opt-in instead of the reverse. Existing configs
  keep whatever they already say.
- Removed the `SmoothDownsample` switch. Its only job was forcing point sampling,
  which `Downsample = Point` now does, and shipping two overlapping controls for
  one behaviour was worse than shipping one.

### Fixed
- Corrected the supersampling guidance, which previously claimed values above 2
  bought "anti-aliasing only" because room terrain is fixed 1400×800 artwork.
  That was wrong for the pixelated path. The room texture is `FilterMode.Point`
  (`PersistentData.cs:18`); vanilla draws it 1:1 into a 768-tall buffer and lets
  the *display* stretch that by a non-integer factor, blurring across every hard
  texel boundary outside the engine. Supersampling moves the magnification inside
  the engine, where it stays hard-edged, and a denser render quantises the level
  graphic's fractional camera position more finely — at 1x edges snap to whole
  pixels, at 8x to an eighth of one — so they land accurately and stop crawling
  when the camera pans. Higher values therefore keep paying off with smoothing
  off; the returns only fade quickly with smoothing on.

## 1.4.0

### Changed
- **The settings page is now two controls.** There are only two decisions a
  player actually has to make — how much to render, and smooth or pixelated —
  so it is a **Render quality** slider and a **Sharp pixels** tick box. The
  slider shows the resolution it buys, so the number means something.
- Everything else was either automatic already or has one correct answer, and is
  decided for you: which downsample filter to use is worked out from the ratio,
  and presenting at the display's real resolution is always right. Both remain in
  the config file as troubleshooting overrides.
- **Stretch to fill screen** appears only on a non-16:9 display, where it is a
  real choice between black bars and a distorted picture. On 16:9 both settings
  look identical, and a control that visibly does nothing is worse than no control.
- Unticking a box no longer stamps on a deliberate `MipmapBox`, `Bilinear` or
  `AspectBackbuffer` choice made in the config file; it only overrides when the
  tick box actually disagrees.

### Fixed
- The performance guidance overstated memory pressure. Even at `Supersample = 8`
  the render target is ~360 MB, which is not a problem on a 4 GB card. The real
  constraint is fill rate.

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
