# Releasing and distributing

## Why CI cannot just build this

The plugin compiles against three assemblies that ship inside the Rain World
install and exist on no package feed anywhere:

| Assembly | Where it lives | Size |
|---|---|---|
| `Assembly-CSharp.dll` | `RainWorld_Data/Managed/` | 9.1 MB |
| `HOOKS-Assembly-CSharp.dll` | `BepInEx/plugins/` | 14.5 MB |
| `UnityEngine.UI.dll` | `RainWorld_Data/Managed/` | 264 KB |

`HOOKS-Assembly-CSharp.dll` is worth calling out: it is MonoMod HookGen output
(the `On.*` / `IL.*` hook surface), but it is **not** generated on your machine —
it has the same mtime as `RainWorld.exe`, so it arrives in the Steam depot.
BepInEx has been bundled with Rain World since v1.9, which makes the whole
`BepInEx/` folder part of the game's distribution and therefore Videocult's to
license, not ours.

Everything else now comes from NuGet, pinned to exactly what the game ships:

| Package | Version | Provides | Game ships |
|---|---|---|---|
| `BepInEx.Core` | 5.4.17 | `BepInEx.dll` (via `BepInEx.BaseLib`) | 5.4.17.0 |
| `HarmonyX` | 2.5.5 | `0Harmony.dll` | 2.5.5.0 |
| `UnityEngine.Modules` | 2020.3.45 | `UnityEngine`, `.CoreModule`, `.UIModule`, `.InputLegacyModule` | Unity 2020.3.45f1 |

`BepInEx.Core` and `UnityEngine.Modules` at these versions are only on
<https://nuget.bepinex.dev/v3/index.json> — nuget.org has no `BepInEx.Core` at
all and only `UnityEngine.Modules` 2021.3.33. `HarmonyX` must be pinned
explicitly because `BepInEx.Core` pulls 2.7.0 in transitively, and the game loads
2.5.5.0; compiling against the newer surface would defer a
`MissingMethodException` to a user's machine.

There is **no NuGet package for Rain World's game assemblies**. Searching
nuget.org for `RainWorld` / `Rain World` returns one unrelated weather-API
package.

## What other Rain World repos do

Almost none have CI, and **every one that does works by committing the game's
DLLs into the repo.** Surveyed via the GitHub code-search API: 365 public repos
name `HOOKS-Assembly-CSharp` in a `.csproj`, and of the largest and most active
of those, only five have a `.github/workflows` directory at all.

| Repo | Workflow | How it gets the references |
|---|---|---|
| [`Dual-Iron/fisobs`](https://github.com/Dual-Iron/fisobs/blob/main/.github/workflows/build.yml) | `dotnet publish Fisobs.sln` on `ubuntu-latest` | [`lib/`](https://github.com/Dual-Iron/fisobs/tree/main/lib) committed: `HOOKS-Assembly-CSharp.dll` (14.5 MB), `PUBLIC-Assembly-CSharp.dll` (9.0 MB), `Assembly-CSharp-firstpass.dll`, `UnityEngine*.dll`, `BepInEx.dll`, MonoMod, Mono.Cecil |
| [`Dual-Iron/improved-input-config`](https://github.com/Dual-Iron/improved-input-config/blob/main/.github/workflows/build.yml) | same | `lib/` committed, 16 assemblies incl. `PUBLIC-Assembly-CSharp.dll` (6.7 MB) and `Rewired_Core.dll` |
| [`SlimeCubed/SlugBaseRemix`](https://github.com/SlimeCubed/SlugBaseRemix/blob/master/.github/workflows/build.yml) | same | `lib/` committed |
| [`Dragon-Seeker/myriad`](https://github.com/Dragon-Seeker/myriad/blob/master/.github/workflows/build.yml) | same | `lib/` committed |
| [`rwmoddingch/RandomBuff`](https://github.com/rwmoddingch/RandomBuff/blob/master/.github/workflows/Upload.yml) | **uploads to the Steam Workshop**, does not compile | n/a — ships a prebuilt `mod/` folder |

The csproj in those repos is as simple as this, which is the whole trick:

```xml
<ItemGroup>
  <Reference Include="../lib/*.dll"><Private>false</Private></Reference>
</ItemGroup>
```

Note that these are not stripped reference assemblies — they are the shipped
binaries with full IL, and `PUBLIC-Assembly-CSharp.dll` is a publicised copy of
essentially the entire game's C# code (it ships in `BepInEx/utils/`, produced by
the bundled `Dragons.PublicDragon.dll` patcher). Committing it is the community
norm and it is also plainly a redistribution of Videocult's code. This project
deliberately does not copy that pattern; see the options table below.

The rest of the ecosystem points `HintPath` straight at a local install — 22
public `.csproj` files hardcode a literal `steamapps/common/Rain World` path —
and builds and uploads by hand.

## Options considered

| Option | Verdict |
|---|---|
| **(a)** No CI; build locally, attach the DLL to a release | **Adopted as the default.** `tools/release.sh` does it in one command. |
| **(b)** Commit Refasmer-generated reference assemblies publicly | Technically verified to work — 9.1 MB → 1.7 MB, and the plugin builds clean against it. **Rejected for public repos:** still a mechanical derivative of copyrighted code, ~14 MB of it, with no licence permitting redistribution. |
| **(c)** NuGet reference packages for Rain World | **Do not exist.** Adopted for everything else, which is what shrinks the problem from nine assemblies to three. |
| **(d)** csproj that auto-detects the install | **Adopted.** This is the contributor path and it needs no configuration. |
| **(e)** SteamCMD-install the game on the runner | **Rejected.** Needs your Steam credentials in repo secrets, needs a Steam Guard shared secret for headless login, and credential sharing is questionable under the Steam Subscriber Agreement. Not worth it to compile a 30 KB DLL. |
| **(f)** Refasmer output in a **private** repo, pulled by CI with a token | **Adopted as optional.** Not public redistribution, and it gives real compile-checking plus a fully automated release. Fork PRs get no secrets, so the job skips rather than fails. |

## Recommended flow

Release from your own machine. It needs no secrets and produces the same zip CI
would:

```bash
# 1. bump the version (single source of truth)
$EDITOR mod/modinfo.json          # "version": "1.0.2"
$EDITOR src/Plugin.cs             # PluginVersion = "1.0.2"
$EDITOR CHANGELOG.md              # add a "## 1.0.2" section

# 2. validate, build, package, and see the zip layout
tools/release.sh

# 3. tag, push and create the GitHub release
tools/release.sh --publish
```

`tools/check-metadata.py` runs first and fails on a version mismatch, a
malformed `modinfo.json`, or a `thumbnail.png` the in-game Workshop uploader
would reject.

## Optional: make CI compile

One-time setup.

1. Create a **private** GitHub repo, e.g. `you/rw-refs-private`.
2. Generate and push the reference assemblies:
   ```bash
   tools/make-refs.sh                 # writes ./refs/
   cd refs && git init && git add -A
   git commit -m "Rain World v1.11.8 reference assemblies"
   git remote add origin git@github.com:you/rw-refs-private.git
   git push -u origin main
   ```
3. Create a fine-grained PAT with **Contents: read** on that repo only.
4. In this repo: Settings → Secrets and variables → Actions → add
   `RW_REFS_REPO` = `you/rw-refs-private` and `RW_REFS_TOKEN` = the PAT.

Re-run step 2 after each Rain World update. `refs/GAME_VERSION.txt` records what
a given refs tree was generated from, so a stale one is visible in the CI log.

Without these secrets: `ci.yml`'s `compile` job skips with a notice (metadata and
Windows-installer checks still run), and `release.yml` stops immediately and
tells you to use `tools/release.sh`.

---

# Distribution channels

## Steam Workshop

**The game is the uploader, and for the first publish it has to be.** Rain World
ships its own Workshop publisher: `SteamWorkshopUploader.cs` drives a state
machine (`INIT → CHECK_EXISTS → CREATE → ACCEPT_LEGAL → UPLOAD → PREVIEW`) over
Steamworks' `SteamUGC`, and `RainWorldSteamManager.cs:237-292` does the actual
`StartItemUpdate` / `SubmitItemUpdate`.

SteamCMD *can* push content to app 312520 — see the automation subsection at the
end — but it cannot do the one thing that matters here, which is write the
Steam **key-value tags** the game and RainDB identify a mod by. Use the in-game
button.

### Steps

1. Install the mod locally so it appears in Remix — `./install.sh` or
   `.\install.ps1`, or extract the release zip into
   `RainWorld_Data/StreamingAssets/mods/`.
2. Add `mod/thumbnail.png` first if you have not. The uploader validates it and
   refuses to start otherwise:
   - **under 1 000 000 bytes** — `RainWorldSteamManager.cs:173-176`
   - **16:9**, specifically `height / width` in `[0.5616, 0.5634]` —
     `RainWorldSteamManager.cs:179-183`. Use 1920×1080 or 1280×720.
3. Launch through Steam (the Workshop API needs a live Steam session —
   `SteamManager.Initialized`, `RainWorldSteamManager.cs:198-204`).
4. Main menu → **Remix** → select **True Resolution** → **UPLOAD**, and confirm.
5. Accept the Steam Workshop EULA if prompted; the uploader has a dedicated
   `ACCEPT_LEGAL` step for the first upload.
6. It publishes **Unlisted** the first time
   (`RainWorldSteamManager.cs:253-256`). Open the item in the overlay and set it
   to Public when you are ready.

### What the game pushes from `modinfo.json`

Fill these in before uploading, because they become the Workshop page:

| `modinfo.json` | Workshop field | Source |
|---|---|---|
| `name` | item title | `RainWorldSteamManager.cs:251` |
| `description` | item description, `<LINE>` → newline | `:252` |
| `tags` | item tags | `:269` |
| `id` | key-value tag `id` — the identity used to find your existing item on re-upload | `:263`, `SteamWorkshopUploader.cs:103` |
| `version` | key-value tag `version` | `:264` |
| `target_game_version` | key-value tag `targetGameVersion` | `:265` |
| `authors` | key-value tag `authors` | `:266` |
| `requirements` / `requirements_names` | key-value tags | `:267-268` |
| `youtube_trailer_id` | preview video | `:285-288` |
| `thumbnail.png` | preview image | `:271-284` |

**Updating** is the same UPLOAD button. The uploader searches the Workshop for an
item whose `id` key-value tag matches yours, and reuses it if you own it
(`SteamWorkshopUploader.cs:132-151`). Never change `id` — a different `id` creates
a second Workshop item, and if someone else already published that `id` the
upload is refused with *"This mod already exists on the workshop by another
author."*

### Automatable? Partly, and not worth it

The first publish is **not** automatable: `SteamUGC.CreateItem` runs inside the
game process (`RainWorldSteamManager.cs:216`), the Workshop EULA has to be
accepted interactively, and the item is created Unlisted
(`UploadWorkshopMod(mod, newMod)` — `SteamWorkshopUploader.cs:213` passes
`unlisted = newMod`, so only the *first* upload is forced Unlisted).

Subsequent content updates *can* be automated. There is one real Rain World repo
doing it: [`rwmoddingch/RandomBuff`](https://github.com/rwmoddingch/RandomBuff/blob/master/.github/workflows/Upload.yml)
uses [`pkuyo/steam-workshop-upload`](https://github.com/pkuyo/steam-workshop-upload),
a Docker action that writes a `workshop.vdf` and runs
`steamcmd +login … +workshop_build_item`, with a Steam Guard `shared_secret` for
headless 2FA:

```yaml
- uses: pkuyo/steam-workshop-upload@main
  with:
    appid: 312520          # Rain World
    itemid: ${{ secrets.ITEM_ID }}
    path: 'mod'
    visibility: 3          # 3 = private
  env:
    STEAM_USERNAME: ${{ secrets.DEPLOY_USERNAME }}
    STEAM_PASSWORD: ${{ secrets.DEPLOY_PASSWORD }}
```

**Do not adopt this here.** Three concrete reasons, in order of severity:

1. **It cannot write key-value tags.** The action's generated VDF contains only
   `appid`, `contentfolder`, `changenote`, `publishedfileid`, `previewfile`,
   `visibility`, `title`, `description` — `steamcmd`'s `workshop_build_item` has
   no key-value-tag field at all. So the `version` key-value the game writes
   (`RainWorldSteamManager.cs:264`) would keep whatever value the last in-game
   upload set, and RainDB — which reads exactly those tags — would advertise the
   old version forever while serving new content.
2. **It overwrites the page.** `title` and `description` default to the repo name
   and the head commit message. RandomBuff only runs it on a `Test**` branch with
   `visibility: 3` (private) for that reason; it is a staging pipeline, not a
   publish one.
3. **Credentials.** Your Steam account password plus a 2FA shared secret in
   GitHub secrets, for a 30 KB DLL. The shared secret is the whole second factor,
   permanently, for the entire account.

Everything *before* the upload — build, package, thumbnail validation, version
agreement — is automated here. The upload itself is one button and takes ten
seconds; leave it manual.

## RainDB

**Publishing to the Steam Workshop *is* the RainDB submission.** RainDB mirrors
the Workshop; there is nothing to send anyone.

Evidence: `raindb.js` in `AndrewFM/RainDB` is a 4.24 MB generated file with 4581
entries, and every entry is keyed on a Steam Workshop published file id:

```js
Mods.push({
  "name": "UwU Mod",
  "id": "henpemaz_uwumod",
  "workshop_id": "2920438669",
  "thumb": "previews/2920438669.png",
  "url": "https://andrew.fm/rainworld/raindb/UwU_Mod.zip",
  "version": "0.1.0", "created": 1674148482, "modified": 1674148482,
  "tags": "", "order": 1
});
```

Exactly **1 of 4581** entries has an empty `workshop_id`. The `previews/`
directory is 1000+ PNGs named `<workshop_id>.png`, and the mirrored zip is
rehosted on the maintainer's own server. Older advice to DM AndrewFM on Discord
with a Google Drive link applies to the pre-Workshop era — see `legacy.html` in
that repo.

So: upload to the Workshop, wait for the next scrape, and the entry appears. Only
contact the maintainer (AndrewFM, via the Rain World Modding Discord) if it does
not, or if you need a listing without a Workshop item.

RainDB's filter UI offers exactly twelve tags, so use these to be filterable
there: `Arenas`, `Regions`, `Campaigns`, `Creatures`, `Game Mechanics`, `Items`,
`Cosmetics`, `Game Modes`, `Dependency`, `Accessibility`, `Translations`, `Tools`.
Steam itself accepts arbitrary strings — RainDB's corpus contains 142 distinct
tags including typos — but anything outside the twelve is unfilterable. This mod
uses `Cosmetics` and `Accessibility`, plus the `Base` / `Downpour` / `Watcher`
DLC-compatibility markers that comparable mods use.

**Automatable?** Not needed, and not possible from outside.

## GitHub Releases

Automated. `tools/release.sh --publish`, or push a `v*` tag with the private-refs
setup in place. The zip has one top-level entry, `trueresolution/`, so extracting
it into `mods/` is the entire install.

## Version compatibility

`target_game_version` is `"v1.11"`, deliberately not `"v1.11.8"`.

The game does a **prefix** test, `MenuModList.cs:1893`:

```csharp
if (!"v1.11.8".StartsWith(installedMod.targetGameVersion))
    modButton.outdated = true;
```

- `"v1.11.8"` → clean, but goes orange-"outdated" the day v1.11.9 ships.
- `"v1.11"` → stays current across all of 1.11.x, and correctly flags as
  outdated at v1.12, which is when `FScreen` might actually move.
- `outdated` only drives a colour (`MenuModList.cs:1402`). Loading is **never**
  blocked.

The cost is one grey informational line in the mod's Remix details pane
(`InternalOI_Stats.cs:446-468`), shown whenever `target_game_version` is not
byte-identical to the game version. Worth it.

**Should the plugin hard-fail on an unexpected game version? No — warn.**

1. It cannot usefully detect one. The plugin loads via BepInEx before any game
   version string is available, and the hooks it installs (`On.FScreen.ctor`,
   `On.Futile.Init`, `On.Options.OnLoadFinished`) are resolved by HookGen at load
   time — if a future version renames or removes them, BepInEx throws at hook
   registration and the plugin is already disabled without our help.
2. Every failure mode here is cosmetic. Nothing this mod does can corrupt a save:
   it writes a render-texture size and a backbuffer size and never touches
   `pixelWidth`, `pixelHeight`, `screenResolutions` or `ScreenSize`. A wrong
   guess on a new version means a soft or mis-scaled image, not data loss —
   refusing to load would be strictly worse than looking slightly wrong.
3. The mod already degrades gracefully. Reflection failure on
   `FScreen.renderScale` logs an error and leaves supersampling off; a
   `Screen.SetResolution` that never lands is retried a bounded number of times
   and then abandoned with a warning.

So: state the tested version prominently in the README, keep
`target_game_version` at the minor-version prefix so Remix flags a genuine major
bump, log loudly, and never refuse to load.
