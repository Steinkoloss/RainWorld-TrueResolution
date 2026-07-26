<!--
CI on a fork PR runs the metadata and installer-script checks but CANNOT
compile: three of the referenced assemblies ship with Rain World and are not
redistributable, and GitHub does not expose secrets to forked runs. So please
say below whether you actually ran the thing.
-->

## What this changes

## Did you run it?

- [ ] `dotnet build -c Release -warnaserror` succeeds locally
- [ ] `python3 tools/check-metadata.py` passes
- [ ] Launched the game with the mod enabled and it still loads

**Display it was tested on** (resolution, aspect, fullscreen/windowed, GPU and
graphics API from the `gfx:` log line):

**Relevant BepInEx log lines** (`display probe`, `render texture is now`,
`backbuffer:`):

```
```

## Checklist

- [ ] No game or engine assembly is added to the repo (`Assembly-CSharp.dll`,
      `HOOKS-Assembly-CSharp.dll`, `UnityEngine*.dll`, `BepInEx.dll`, …). CI
      fails on these, and they are not ours to redistribute.
- [ ] `FScreen.pixelWidth`, `FScreen.pixelHeight`, `Options.screenResolutions`
      and `Options.ScreenSize` are still never written. They are world units as
      well as pixels, so enlarging them shows void past the room art and breaks
      the culling and camera-switch predicates. This invariant is the whole
      reason the mod is safe.
- [ ] If the version changed: `mod/modinfo.json`, `src/Plugin.cs`
      `PluginVersion` and a `CHANGELOG.md` section all agree
      (`tools/check-metadata.py` enforces this).
- [ ] Any claim about game behaviour cites `file:line` of the decompiled source.
