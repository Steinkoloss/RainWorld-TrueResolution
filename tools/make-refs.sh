#!/usr/bin/env bash
# Generate ./refs/ - a "reference root" that lets this project build with no
# Rain World installed. Run once per game version, from a machine that has the
# game.
#
# What it produces:
#
#   refs/
#     RainWorld_Data/Managed/Assembly-CSharp.dll     9.1 MB -> 1.7 MB
#     RainWorld_Data/Managed/UnityEngine.UI.dll      264 KB -> 100 KB
#     BepInEx/plugins/HOOKS-Assembly-CSharp.dll     14.5 MB -> 12.0 MB
#     GAME_VERSION.txt
#
# The layout deliberately mirrors the game's own, so build/RainWorld.props can
# point $(RainWorldDir) at refs/ and every HintPath keeps working unchanged.
#
# These are *reference assemblies*: public type and member signatures with all
# method bodies removed (JetBrains Refasmer). Nothing executable survives; they
# cannot be run and are useless to a would-be pirate.
#
#   ---------------------------------------------------------------------------
#   THEY ARE STILL A DERIVATIVE WORK OF COPYRIGHTED GAME CODE.
#   Do not commit refs/ to a public repository and do not attach it to a
#   release. .gitignore excludes it for exactly this reason. If you want CI to
#   compile, push refs/ to a PRIVATE repository and let the workflow pull it
#   with a token - see docs/RELEASING.md.
#   ---------------------------------------------------------------------------
#
# Usage:
#   tools/make-refs.sh                  # auto-detect the game
#   RW_DIR="/path/to/Rain World" tools/make-refs.sh
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$here/refs"

# ---------------------------------------------------------------- find the game
find_game() {
  if [[ -n "${RW_DIR:-}" ]]; then printf '%s\n' "$RW_DIR"; return; fi

  local roots=(
    "$HOME/.local/share/Steam"
    "$HOME/.steam/steam"
    "$HOME/.steam/root"
    "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"
    "$HOME/snap/steam/common/.local/share/Steam"
    "$HOME/Library/Application Support/Steam"
  )
  # Every library folder Steam knows about, including other drives.
  local r libs=()
  for r in "${roots[@]}"; do
    for vdf in "$r/steamapps/libraryfolders.vdf" "$r/config/libraryfolders.vdf"; do
      [[ -f "$vdf" ]] || continue
      while IFS= read -r line; do libs+=("$line"); done < <(
        sed -n 's/.*"path"[[:space:]]*"\(.*\)".*/\1/p' "$vdf" | sed 's/\\\\/\\/g'
      )
    done
  done
  libs+=("${roots[@]}")

  local cand
  for r in "${libs[@]}"; do
    cand="$r/steamapps/common/Rain World"
    if [[ -f "$cand/RainWorld_Data/Managed/Assembly-CSharp.dll" ]]; then
      printf '%s\n' "$cand"; return
    fi
  done
  return 1
}

if ! rw="$(find_game)"; then
  echo "error: could not find Rain World." >&2
  echo "       Set RW_DIR=/path/to/Rain World and re-run." >&2
  exit 1
fi
echo "==> game: $rw"

managed="$rw/RainWorld_Data/Managed"
plugins="$rw/BepInEx/plugins"
for f in "$managed/Assembly-CSharp.dll" "$managed/UnityEngine.UI.dll" "$plugins/HOOKS-Assembly-CSharp.dll"; do
  [[ -f "$f" ]] || { echo "error: missing $f - verify the game files in Steam." >&2; exit 1; }
done

# ---------------------------------------------------------------------- refasmer
if ! command -v refasmer >/dev/null 2>&1; then
  echo "==> installing JetBrains.Refasmer.CliTool into $here/.tools"
  dotnet tool install --tool-path "$here/.tools" JetBrains.Refasmer.CliTool >/dev/null
  export PATH="$here/.tools:$PATH"
fi

rm -rf "$out"
mkdir -p "$out/RainWorld_Data/Managed" "$out/BepInEx/plugins"

# --all keeps internal and private types. Refasmer would otherwise infer
# --public for these assemblies and drop them, and HookGen's On.* delegates
# reference non-public game types in their signatures, so a --public HOOKS
# assembly fails to load in the compiler.
echo "==> refasming (public API surface only, no method bodies)"
refasmer --all -O "$out/RainWorld_Data/Managed" \
  "$managed/Assembly-CSharp.dll" "$managed/UnityEngine.UI.dll"
refasmer --all -O "$out/BepInEx/plugins" "$plugins/HOOKS-Assembly-CSharp.dll"

# Record what this was generated from, so a stale refs/ is obvious.
ver="$rw/RainWorld_Data/StreamingAssets/GameVersion.txt"
{
  echo "generated:   $(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "from:        $rw"
  echo -n "GameVersion: "; if [[ -f "$ver" ]]; then tr -d '\r\n' < "$ver"; echo; else echo UNKNOWN; fi
} > "$out/GAME_VERSION.txt"

echo
echo "==> wrote $out"
du -sh "$out"
find "$out" -name '*.dll' -printf '    %-46p %10s bytes\n' 2>/dev/null || find "$out" -name '*.dll'
cat "$out/GAME_VERSION.txt" | sed 's/^/    /'
echo
echo "Builds will now use refs/ automatically when no game is found."
echo "Force it with:  dotnet build -p:RainWorldDir=refs"
echo
echo "!! refs/ is a derivative of copyrighted game code. It is in .gitignore."
echo "!! Never commit it to a public repo. See docs/RELEASING.md for the"
echo "!! private-repo route if you want CI to compile."
