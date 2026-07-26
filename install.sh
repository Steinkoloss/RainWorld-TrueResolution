#!/usr/bin/env bash
# Build True Resolution and install it into Rain World's mods folder.
# Linux, macOS, and Windows under Git Bash / WSL. Windows users: install.ps1.
#
#   ./install.sh                              auto-detect the game
#   RW_DIR="/path/to/Rain World" ./install.sh  explicit
#   ./install.sh --no-build                   install an already-built bin/mod
set -euo pipefail

MOD_ID="trueresolution"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
do_build=1
[[ "${1:-}" == "--no-build" ]] && do_build=0

# ================================================================ find the game
# Mirrors build/RainWorld.props: every library folder Steam records in
# libraryfolders.vdf, then a list of common non-Steam locations. Probing for
# Assembly-CSharp.dll rather than the folder, because Steam leaves empty game
# folders behind after an uninstall.
find_game() {
  if [[ -n "${RW_DIR:-}" ]]; then printf '%s\n' "$RW_DIR"; return 0; fi

  local roots=(
    "$HOME/.local/share/Steam"
    "$HOME/.steam/steam"
    "$HOME/.steam/root"
    "$HOME/.steam/debian-installation"
    "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"
    "$HOME/snap/steam/common/.local/share/Steam"
    "$HOME/Library/Application Support/Steam"
    "/home/deck/.local/share/Steam"
    "/run/media/mmcblk0p1"
    "/c/Program Files (x86)/Steam"
    "/mnt/c/Program Files (x86)/Steam"
  )

  local libs=() r vdf line
  for r in "${roots[@]}"; do
    for vdf in "$r/steamapps/libraryfolders.vdf" "$r/config/libraryfolders.vdf"; do
      [[ -f "$vdf" ]] || continue
      while IFS= read -r line; do
        [[ -n "$line" ]] && libs+=("$line")
      done < <(sed -n 's/.*"path"[[:space:]]*"\(.*\)".*/\1/p' "$vdf" | sed 's/\\\\/\//g')
    done
  done
  libs+=("${roots[@]}")

  local cand
  for r in "${libs[@]}"; do
    for cand in "$r/steamapps/common/Rain World" "$r"; do
      [[ -f "$cand/RainWorld_Data/Managed/Assembly-CSharp.dll" ]] && { printf '%s\n' "$cand"; return 0; }
    done
  done

  for cand in "$HOME/GOG Games/Rain World" "$HOME/Games/Rain World" "/opt/Rain World"; do
    [[ -f "$cand/RainWorld_Data/Managed/Assembly-CSharp.dll" ]] && { printf '%s\n' "$cand"; return 0; }
  done
  return 1
}

if ! RW_DIR="$(find_game)"; then
  cat >&2 <<'EOF'
error: could not find Rain World.

Set RW_DIR to the folder that contains RainWorld.exe and re-run, e.g.

  RW_DIR="$HOME/.local/share/Steam/steamapps/common/Rain World" ./install.sh

To find it: Steam -> right-click Rain World -> Manage -> Browse local files.
EOF
  exit 1
fi
echo "==> game: $RW_DIR"

SA="$RW_DIR/RainWorld_Data/StreamingAssets"
DEST="$SA/mods/$MOD_ID"

if [[ ! -d "$SA" ]]; then
  echo "error: $SA does not exist - RW_DIR is not a Rain World install root." >&2
  exit 1
fi

# ======================================================================== build
if (( do_build )); then
  command -v dotnet >/dev/null 2>&1 || {
    echo "error: dotnet SDK not found. Install .NET SDK 6 or newer:" >&2
    echo "       https://dotnet.microsoft.com/download" >&2
    exit 1
  }
  echo "==> building"
  dotnet build "$here/TrueResolution.csproj" -c Release -p:RainWorldDir="$RW_DIR"
fi

STAGE="$here/bin/mod/$MOD_ID"
if [[ ! -f "$STAGE/modinfo.json" ]]; then
  echo "error: $STAGE was not staged. Run without --no-build." >&2
  exit 1
fi

# ====================================================================== install
echo "==> installing to $DEST"
# Remove rather than merge: a stale DLL from a previous version left beside the
# new one would give BepInEx two plugins with the same GUID.
rm -rf "$DEST"
mkdir -p "$(dirname "$DEST")"
cp -R "$STAGE" "$DEST"
find "$DEST" -type f -printf '    %P\n' 2>/dev/null || find "$DEST" -type f

# ============================================== preflight: is BepInEx injecting?
# Rain World ships a Windows build. Under Proton, Wine prefers its own builtin
# winhttp.dll over the Doorstop shim in the game folder, so BepInEx never loads
# and no mod of any kind can work - with no error message anywhere.
if [[ ! -f "$RW_DIR/BepInEx/LogOutput.log" && ! -d "$RW_DIR/BepInEx/cache" ]]; then
  cat <<EOF

!! BepInEx has never run on this install: no BepInEx/LogOutput.log, no BepInEx/cache.
!! On Linux/Proton this is expected until you add the winhttp override, and until
!! you do, NO mod can load and this one will appear to do nothing.
!!
!!   Steam -> Rain World -> Properties -> Launch Options:
!!       WINEDLLOVERRIDES="winhttp=n,b" %command%
!!
!! Launch once, then confirm the log appeared:
!!   ls "$RW_DIR/BepInEx/LogOutput.log"
EOF
fi

# ============================================================= enabledMods.txt
# MultiFolderLoader.cs:228 skips a mod folder when the enabled list exists and
# does not contain it. When the file is absent the list is null and the gate
# short-circuits, so every folder loads.
# The game writes this file CRLF-separated, hence the tr.
if [[ -f "$SA/enabledMods.txt" ]]; then
  if ! tr -d '\r' < "$SA/enabledMods.txt" | grep -qix "$MOD_ID"; then
    printf '\r\n%s' "$MOD_ID" >> "$SA/enabledMods.txt"
    echo "==> appended '$MOD_ID' to enabledMods.txt (stopgap)"
  fi
  echo "!! That file is rewritten from the ENABLED list the next time you press"
  echo "!! Apply in Remix. Enable 'True Resolution' there to make it stick."
else
  echo "==> enabledMods.txt is absent, so MultiFolderLoader loads every folder"
  echo "==> under mods/ - the plugin is already active even though Remix shows"
  echo "==> it as disabled. The first Remix 'Apply' creates the file from the"
  echo "==> ENABLED mods only, so enable it there or it stops loading."
fi

ver="$(python3 -c "import json;print(json.load(open('$here/mod/modinfo.json'))['version'])" 2>/dev/null || echo "")"
cat <<EOF

Next: launch, main menu -> Remix -> enable 'True Resolution' -> Apply -> restart.

Verify:
  grep -F 'True Resolution${ver:+ $ver} loaded' "$RW_DIR/BepInEx/LogOutput.log"
  grep -F 'display probe'                      "$RW_DIR/BepInEx/LogOutput.log"
  grep -F 'backbuffer: CONFIRMED'              "$RW_DIR/BepInEx/LogOutput.log"

Config (written on the first successful run):
  $RW_DIR/BepInEx/config/steinkoloss.trueresolution.cfg

After a Rain World update, re-enable the mod in Remix once: MultiFolderLoader
blanks enabledMods.txt whenever the game version string changes
(MultiFolderLoader.cs:301-305).
EOF
