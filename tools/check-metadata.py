#!/usr/bin/env python3
"""
Validate everything about this mod that can be checked without a Rain World
install. This is what CI runs on pull requests from forks, where the game
assemblies are unavailable by design.

Every rule below encodes a real failure mode of the game's own mod loader, with
the deciding line of decompiled game code cited. Run from anywhere:

    python3 tools/check-metadata.py
"""
import json
import re
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
FAILURES: list[str] = []
NOTES: list[str] = []

# The game hardcodes its own version in a dozen places and compares with
# StartsWith, e.g. MenuModList.cs:1893.
GAME_VERSION = "v1.11.8"


def fail(msg: str) -> None:
    FAILURES.append(msg)


def note(msg: str) -> None:
    NOTES.append(msg)


# ===================================================================== modinfo
modinfo_path = ROOT / "mod" / "modinfo.json"
if not modinfo_path.is_file():
    fail(f"{modinfo_path.relative_to(ROOT)} is missing. The game reads it from "
         "the mod folder root (ModManager.cs:4001-4005); without it the folder "
         "name is used as the id and the mod shows up unnamed.")
    print("\n".join(FAILURES))
    sys.exit(1)

raw = modinfo_path.read_text(encoding="utf-8")
try:
    info = json.loads(raw)
except json.JSONDecodeError as e:
    fail(f"mod/modinfo.json is not valid JSON: {e}. ModManager.cs:4009-4012 "
         "returns null for an unparsable modinfo and the mod silently vanishes "
         "from the Remix list.")
    print("\n".join(FAILURES))
    sys.exit(1)

for key in ("id", "name", "version", "authors", "description"):
    if not info.get(key):
        fail(f"mod/modinfo.json is missing '{key}'.")

mod_id = info.get("id", "")
version = info.get("version", "")

# The id is the folder name users type, the BepInEx config file stem, the
# enabledMods.txt entry and the Workshop key-value tag
# (RainWorldSteamManager.cs:263). Anything exotic breaks at least one of those.
if mod_id and not re.fullmatch(r"[A-Za-z0-9_.-]+", mod_id):
    fail(f"mod id {mod_id!r} should be limited to letters, digits, '.', '_' and "
         "'-'. It is used as a folder name and as an enabledMods.txt entry.")

# ModManager.cs:4109 - with checksum_override_version the declared version is
# used in place of a content hash, so a stale version silently suppresses the
# "mod changed" prompt on every future update.
if info.get("checksum_override_version") is True and not version:
    fail("checksum_override_version is true but 'version' is empty. "
         "ModManager.cs:4109 substitutes the version string for the content "
         "checksum, so an empty version disables change detection entirely.")

# ===================================================== target_game_version
# MenuModList.cs:1893:  if (!"v1.11.8".StartsWith(installedMod.targetGameVersion))
#                            modButton.outdated = true;
# It is a prefix test against the game's own version string, and it only drives
# a colour (MenuModList.cs:1402) - loading is never blocked.
tgv = info.get("target_game_version")
if tgv is None:
    note("target_game_version is absent, so it defaults to the exact current "
         f"game version {GAME_VERSION} (ModManager.cs:44). The mod will be "
         "flagged 'outdated' in Remix the day v1.11.9 ships. Setting 'v1.11' "
         "keeps it clean across the whole 1.11 line.")
elif not GAME_VERSION.startswith(tgv):
    fail(f"target_game_version {tgv!r} is not a prefix of {GAME_VERSION!r}, so "
         "MenuModList.cs:1893 marks this mod 'outdated' in the Remix list "
         "(orange). Loading still works, but users read that as broken.")

# ============================================================= tags
# RainWorldSteamManager.cs:269 passes these straight to SteamUGC.SetItemTags.
# Steam accepts arbitrary strings, but RainDB only offers these twelve in its
# filter UI (index.html <option> list), so anything else is unfilterable there.
RAINDB_TAGS = {
    "Arenas", "Regions", "Campaigns", "Creatures", "Game Mechanics", "Items",
    "Cosmetics", "Game Modes", "Dependency", "Accessibility", "Translations",
    "Tools",
}
# Widely used DLC-compatibility markers, not part of RainDB's filter.
DLC_TAGS = {"Base", "Downpour", "Watcher"}
tags = info.get("tags") or []
if not isinstance(tags, list):
    fail("'tags' must be a JSON array (ModManager.cs:4053-4056 casts it to "
         "List<object>; a string there throws and the whole modinfo is dropped).")
else:
    unknown = [t for t in tags if t not in RAINDB_TAGS | DLC_TAGS]
    if unknown:
        note(f"tags not in RainDB's filter list: {unknown}. Steam accepts them, "
             "but RainDB users cannot filter on them. Known-good: "
             f"{sorted(RAINDB_TAGS)}")
    if not tags:
        note("no tags set. The mod will be unfilterable on RainDB and on the "
             "Workshop.")

# =========================================================== version agreement
# Four independent copies of the version string, one of which is what users
# quote in bug reports. They drift.
plugin_cs = ROOT / "src" / "Plugin.cs"
if plugin_cs.is_file():
    text = plugin_cs.read_text(encoding="utf-8")
    m = re.search(r'PluginVersion\s*=\s*"([^"]+)"', text)
    if not m:
        fail("src/Plugin.cs has no PluginVersion constant.")
    elif m.group(1) != version:
        fail(f"version mismatch: mod/modinfo.json says {version!r} but "
             f"src/Plugin.cs PluginVersion is {m.group(1)!r}. BepInEx logs the "
             "latter and Remix shows the former, so bug reports become "
             "impossible to correlate.")
    gm = re.search(r'PluginGuid\s*=\s*"([^"]+)"', text)
    if gm:
        guid = gm.group(1)
        # BepInEx writes BepInEx/config/<guid>.cfg; the README tells users where
        # to find it by name.
        readme = ROOT / "README.md"
        if readme.is_file() and f"{guid}.cfg" not in readme.read_text(encoding="utf-8"):
            note(f"README.md does not mention {guid}.cfg, the config file "
                 "BepInEx will create from PluginGuid.")

# ================================================================ name drift
# The display name and the id appear in prose in a dozen files, and a rename
# leaves survivors behind - especially where hard wrapping splits the name
# across two lines, which is invisible to a plain `grep "Old Name"`. Collapsing
# whitespace before searching is the entire trick.
#
# Add every name this mod has ever shipped under. Each is a hard failure: Remix
# lists mod/modinfo.json's "name" verbatim, so a user told to enable "Old Name"
# will not find it.
FORMER_IDENTIFIERS = [
    "Resolution Fix",
    "resolutionfix",
    "RainWorldResolutionFix",
    "jaimep.resolutionfix",
]

display_name = info.get("name", "")

# Where the mod is named *to a user*. Each of these must use the current name.
USER_FACING = [
    "README.md",
    "install.sh",
    "install.ps1",
    ".github/ISSUE_TEMPLATE/bug_report.yml",
]

TEXT_SUFFIXES = {".md", ".yml", ".yaml", ".json", ".sh", ".ps1", ".py", ".cs",
                 ".csproj", ".props", ".targets", ".config"}
SKIP_DIRS = {".git", "bin", "obj", "refs", ".tools", ".vs", ".idea"}

for path in sorted(ROOT.rglob("*")):
    if not path.is_file() or path.suffix not in TEXT_SUFFIXES:
        continue
    rel = path.relative_to(ROOT)
    if any(part in SKIP_DIRS for part in rel.parts):
        continue
    # Past CHANGELOG entries may legitimately record the old name, and this
    # file has to spell the old names out to look for them.
    if path.name in {"CHANGELOG.md", Path(__file__).name}:
        continue
    try:
        flat = re.sub(r"\s+", " ", path.read_text(encoding="utf-8"))
    except (UnicodeDecodeError, OSError):
        continue
    for stale in FORMER_IDENTIFIERS:
        if stale.lower() in flat.lower():
            fail(f"{rel} still refers to {stale!r}; the mod is now "
                 f"{display_name!r} / id {mod_id!r}. Remix lists "
                 "modinfo.json's 'name' verbatim, so an instruction naming the "
                 "old one sends users hunting for a mod that is not there.")

for rel in USER_FACING:
    path = ROOT / rel
    if not path.is_file():
        note(f"{rel} is missing.")
        continue
    flat = re.sub(r"\s+", " ", path.read_text(encoding="utf-8"))
    if display_name and display_name not in flat:
        fail(f"{rel} never names the mod {display_name!r}. That is the string "
             "Remix shows, so it is the only name a user can search for.")

changelog = ROOT / "CHANGELOG.md"
if changelog.is_file():
    if not re.search(rf"^##\s*\[?{re.escape(version)}\]?", changelog.read_text(encoding="utf-8"), re.M):
        fail(f"CHANGELOG.md has no '## {version}' section. Releases are cut "
             "from the tag and the release notes are read out of this file.")
else:
    note("no CHANGELOG.md.")

# =============================================================== thumbnail.png
# ModManager.cs:174-182 reads <mod>/thumbnail.png. The in-game Workshop
# uploader refuses to proceed unless it satisfies both of these:
#   RainWorldSteamManager.cs:173-176  size must be < 1_000_000 bytes
#   RainWorldSteamManager.cs:179-183  0.5616 <= height/width <= 0.5634
thumb = ROOT / "mod" / "thumbnail.png"
if not thumb.is_file():
    note("mod/thumbnail.png is absent. Remix shows a blank tile and the "
         "Workshop item will have no preview image. Required aspect is 16:9 "
         "(the uploader accepts height/width in [0.5616, 0.5634]) and under "
         "1 MB. 1920x1080 or 1280x720 both qualify.")
else:
    size = thumb.stat().st_size
    if size >= 1_000_000:
        fail(f"mod/thumbnail.png is {size} bytes. "
             "RainWorldSteamManager.cs:173-176 rejects >= 1000000 bytes with "
             "'Mod's thumbnail image must be less than 1 MB in size.' and the "
             "upload never starts.")
    data = thumb.read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        fail("mod/thumbnail.png is not a PNG.")
    else:
        # IHDR is always the first chunk: length(4) type(4) width(4) height(4)
        w, h = struct.unpack(">II", data[16:24])
        ratio = h / w
        if not (0.5616 <= ratio <= 0.5634):
            fail(f"mod/thumbnail.png is {w}x{h}, height/width = {ratio:.4f}. "
                 "RainWorldSteamManager.cs:179-183 requires [0.5616, 0.5634] "
                 "(16:9) and otherwise refuses the upload with \"Mod's "
                 "thumbnail image should have a 16:9 aspect ratio.\" "
                 "Use 1920x1080 or 1280x720.")
        else:
            note(f"thumbnail {w}x{h} ({size} bytes) passes the uploader's checks.")

# ================================================== description encoding
# RainWorldSteamManager.cs:252 turns <LINE> into a newline for the Workshop
# description; Remix renders it as a line break too. A literal '\n' does not
# work in either place.
desc = info.get("description", "")
if "\n" in raw.split('"description"')[-1].split('",')[0]:
    fail("the description contains a raw newline. Use <LINE> instead: "
         "RainWorldSteamManager.cs:252 replaces <LINE> with Environment.NewLine "
         "when publishing, and Remix does the same when displaying.")
if len(desc) > 8000:
    note(f"description is {len(desc)} characters; Steam truncates long "
         "descriptions in the Workshop list view.")

# ================================================= requirements array shape
for key in ("requirements", "requirements_names"):
    val = info.get(key)
    if val is not None and not isinstance(val, list):
        fail(f"'{key}' must be a JSON array; ModManager.cs:4045-4052 casts it "
             "to List<object> and a non-array throws, dropping the modinfo.")
reqs = info.get("requirements") or []
names = info.get("requirements_names") or []
if isinstance(reqs, list) and isinstance(names, list) and reqs and len(reqs) != len(names):
    fail(f"'requirements' has {len(reqs)} entries but 'requirements_names' has "
         f"{len(names)}. RainWorldSteamManager.cs:267-268 publishes them as two "
         "parallel comma-joined tags, so a length mismatch mislabels "
         "dependencies on the Workshop page.")

# ================================================================== mod layout
# MultiFolderLoader.cs:536 scans <mod>/plugins for assemblies;
# ModManager.cs:3706-3721 uses the same set to decide mod.hasDLL.
if (ROOT / "mod" / "plugins").exists():
    fail("mod/plugins/ is checked in. The plugin DLL is a build output; "
         "build/RainWorld.targets stages it into bin/mod/<id>/plugins/. A "
         "committed copy will shadow the freshly built one.")

for stray in ("mod/newest", f"mod/{GAME_VERSION}"):
    if (ROOT / stray).exists():
        note(f"{stray}/ exists. MultiFolderLoader.cs:506-536 gives it "
             "precedence over plain plugins/, and a version-named folder stops "
             "matching the moment the game version string changes.")

# ====================================================================== report
print(f"mod id       {mod_id}")
print(f"version      {version}")
print(f"target game  {tgv or f'(default) {GAME_VERSION}'}")
print(f"tags         {', '.join(tags) if tags else '(none)'}")
print()
for n in NOTES:
    print(f"note:  {n}\n")
for f in FAILURES:
    print(f"FAIL:  {f}\n")

if FAILURES:
    print(f"{len(FAILURES)} problem(s).")
    sys.exit(1)
print("metadata OK.")
