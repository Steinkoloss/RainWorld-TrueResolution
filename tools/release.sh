#!/usr/bin/env bash
# Cut a release from a machine that has Rain World installed. This is the path
# that needs no CI, no secrets and no reference assemblies - it is the
# recommended way to release, and .github/workflows/release.yml is the optional
# alternative for when you have the private-refs setup.
#
#   tools/release.sh            build, validate, package, print next steps
#   tools/release.sh --publish   ...and create the GitHub release with gh
#
# The version comes from mod/modinfo.json. Bump it there, add a CHANGELOG
# section, then run this.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$here"

publish=0
[[ "${1:-}" == "--publish" ]] && publish=1

version="$(python3 -c "import json;print(json.load(open('mod/modinfo.json'))['version'])")"
tag="v$version"

echo "==> version $version (tag $tag)"

# ================================================================== validate
echo "==> validating metadata"
python3 tools/check-metadata.py

if git rev-parse --git-dir >/dev/null 2>&1; then
  if [[ -n "$(git status --porcelain)" ]]; then
    echo "!! working tree is dirty; the release would not match any commit:" >&2
    git status --short >&2
    [[ $publish -eq 1 ]] && { echo "refusing to publish from a dirty tree." >&2; exit 1; }
  fi
  if git rev-parse -q --verify "refs/tags/$tag" >/dev/null; then
    echo "!! tag $tag already exists. Bump the version in mod/modinfo.json." >&2
    [[ $publish -eq 1 ]] && exit 1
  fi
fi

# ===================================================================== build
echo "==> building and packaging"
rm -rf bin
dotnet build TrueResolution.csproj -c Release -warnaserror -t:PackageMod

zip="$(ls -1 bin/dist/*.zip)"
( cd bin/dist && sha256sum ./*.zip > SHA256SUMS.txt )

echo
echo "==> $zip"
python3 - "$zip" <<'PY'
import sys, zipfile
z = zipfile.ZipFile(sys.argv[1])
for i in z.infolist():
    print(f"    {i.file_size:>10}  {i.filename}")
tops = {n.split('/')[0] for n in z.namelist()}
assert tops == {"trueresolution"}, f"unexpected top-level entries: {tops}"
print("    layout OK: one top-level 'trueresolution/' folder")
PY
cat bin/dist/SHA256SUMS.txt | sed 's/^/    /'

# =================================================================== publish
if [[ $publish -eq 1 ]]; then
  command -v gh >/dev/null || { echo "gh CLI not found: https://cli.github.com" >&2; exit 1; }
  notes="$(mktemp)"
  python3 - "$version" > "$notes" <<'PY'
import re, sys, pathlib
v = sys.argv[1]
p = pathlib.Path("CHANGELOG.md")
text = p.read_text(encoding="utf-8") if p.exists() else ""
m = re.search(rf"^##\s*\[?{re.escape(v)}\]?.*?$(.*?)(?=^##\s|\Z)", text, re.M | re.S)
print((m.group(1).strip() if m else "See CHANGELOG.md.") + "\n")
PY
  cat >> "$notes" <<'EOF'

## Install

1. Download the zip below.
2. Extract it into `Rain World/RainWorld_Data/StreamingAssets/mods/` so you end
   up with `mods/trueresolution/modinfo.json`.
3. Launch, then main menu -> **Remix** -> enable **True Resolution** -> Apply ->
   restart.

Linux/Proton: set the launch option `WINEDLLOVERRIDES="winhttp=n,b" %command%`
first, or BepInEx never injects and no mod loads.
EOF
  git tag -a "$tag" -m "True Resolution $version"
  git push origin "$tag"
  gh release create "$tag" --title "True Resolution $tag" --notes-file "$notes" \
    bin/dist/*.zip bin/dist/SHA256SUMS.txt
  rm -f "$notes"
  echo "==> published $tag"
else
  cat <<EOF

Not published. To publish:
    tools/release.sh --publish

Then, for the two distribution channels (see docs/RELEASING.md):
  Steam Workshop  in-game: Remix -> select the mod -> UPLOAD. Do it from the
                  game, not steamcmd: only the game writes the 'id' and
                  'version' key-value tags that identify the item on re-upload
                  and that RainDB reads.
  RainDB          nothing to do. It mirrors the Workshop; publishing there is
                  the submission.
EOF
fi
