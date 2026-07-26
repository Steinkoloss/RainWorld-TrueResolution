<#
.SYNOPSIS
  Build True Resolution and install it into Rain World's mods folder. Windows.

.DESCRIPTION
  Finds Rain World by asking Steam where its libraries are, builds the plugin,
  and copies the staged mod folder into
  RainWorld_Data\StreamingAssets\mods\trueresolution.

  If you only want to install a downloaded release zip, you do not need this
  script or the .NET SDK - see the "Manual install" section of README.md.

.PARAMETER RainWorldDir
  Path to the folder containing RainWorld.exe. Skips auto-detection.

.PARAMETER NoBuild
  Install the already-built bin\mod folder without invoking dotnet.

.EXAMPLE
  .\install.ps1

.EXAMPLE
  .\install.ps1 -RainWorldDir "D:\SteamLibrary\steamapps\common\Rain World"

.NOTES
  If PowerShell refuses to run this ("running scripts is disabled"), start it
  with:  powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
[CmdletBinding()]
param(
    [string] $RainWorldDir,
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ModId = 'trueresolution'
$Here  = Split-Path -Parent $MyInvocation.MyCommand.Path

function Join-PathLoose([string]$Base, [string]$Leaf) {
    # Join-Path resolves the drive and throws "Cannot find drive" when probing a path on a
    # drive letter that does not exist (a normal situation while guessing install locations).
    # Test-Path handles missing drives fine, so build the string ourselves.
    if ([string]::IsNullOrWhiteSpace($Base)) { return $Leaf }
    return ($Base.TrimEnd('\', '/') + '\' + $Leaf.TrimStart('\', '/'))
}

function Test-RainWorldRoot([string] $Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    # String join, not Join-Path: this probes speculative paths. See Join-PathLoose above.
    Test-Path (Join-PathLoose $Path 'RainWorld_Data\Managed\Assembly-CSharp.dll')
}

# ============================================================== find the game
# Steam records every library folder, including ones on other drives, in
# steamapps\libraryfolders.vdf under its own install root, and records that root
# in the registry. Probe for Assembly-CSharp.dll rather than for the folder:
# Steam leaves empty game folders behind after an uninstall.
function Find-RainWorld {
    if ($env:RW_DIR -and (Test-RainWorldRoot $env:RW_DIR)) { return $env:RW_DIR }

    $steamRoots = New-Object System.Collections.Generic.List[string]

    foreach ($key in @(
        'HKCU:\Software\Valve\Steam',
        'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam',
        'HKLM:\SOFTWARE\Valve\Steam'
    )) {
        try {
            $p = Get-ItemProperty -Path $key -ErrorAction Stop
            foreach ($name in 'SteamPath', 'InstallPath') {
                if ($p.PSObject.Properties.Name -contains $name -and $p.$name) {
                    $steamRoots.Add(($p.$name -replace '/', '\'))
                }
            }
        } catch { }
    }

    foreach ($p in @(
        "${env:ProgramFiles(x86)}\Steam",
        "$env:ProgramFiles\Steam",
        'C:\Steam', 'D:\Steam', 'E:\Steam'
    )) { if ($p) { $steamRoots.Add($p) } }

    $libraries = New-Object System.Collections.Generic.List[string]
    foreach ($root in $steamRoots) {
        foreach ($rel in 'steamapps\libraryfolders.vdf', 'config\libraryfolders.vdf') {
            $vdf = Join-PathLoose $root $rel
            if (-not (Test-Path $vdf)) { continue }
            # Lines look like:   "path"    "D:\\SteamLibrary"
            foreach ($m in [regex]::Matches((Get-Content -Raw $vdf), '"path"\s*"([^"]*)"')) {
                $libraries.Add($m.Groups[1].Value.Replace('\\', '\'))
            }
        }
        $libraries.Add($root)
    }

    foreach ($lib in $libraries) {
        $cand = Join-PathLoose $lib 'steamapps\common\Rain World'
        if (Test-RainWorldRoot $cand) { return $cand }
    }

    foreach ($cand in @(
        'C:\GOG Games\Rain World',
        "$env:ProgramFiles\Epic Games\RainWorld",
        "${env:ProgramFiles(x86)}\Epic Games\RainWorld",
        "$env:USERPROFILE\Games\Rain World"
    )) { if (Test-RainWorldRoot $cand) { return $cand } }

    return $null
}

if (-not $RainWorldDir) { $RainWorldDir = Find-RainWorld }

if (-not (Test-RainWorldRoot $RainWorldDir)) {
    Write-Host ''
    Write-Host 'Could not find Rain World.' -ForegroundColor Red
    Write-Host ''
    Write-Host 'Pass the path explicitly:'
    Write-Host '    .\install.ps1 -RainWorldDir "D:\SteamLibrary\steamapps\common\Rain World"'
    Write-Host ''
    Write-Host 'To find it: Steam -> right-click Rain World -> Manage -> Browse local files.'
    Write-Host 'The folder you want is the one containing RainWorld.exe.'
    exit 1
}

$RainWorldDir = (Resolve-Path $RainWorldDir).Path
Write-Host "==> game: $RainWorldDir"

$StreamingAssets = Join-Path $RainWorldDir 'RainWorld_Data\StreamingAssets'
$Dest            = Join-Path $StreamingAssets "mods\$ModId"

if (-not (Test-Path $StreamingAssets)) {
    Write-Error "$StreamingAssets does not exist - that is not a Rain World install root."
}

# ====================================================================== build
if (-not $NoBuild) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Host ''
        Write-Host 'dotnet SDK not found.' -ForegroundColor Red
        Write-Host 'Install .NET SDK 6 or newer: https://dotnet.microsoft.com/download'
        Write-Host ''
        Write-Host 'You do not need it just to use the mod - download the release zip'
        Write-Host 'and follow "Manual install" in README.md instead.'
        exit 1
    }
    Write-Host '==> building'
    & dotnet build (Join-Path $Here 'TrueResolution.csproj') `
        -c Release "-p:RainWorldDir=$RainWorldDir"
    if ($LASTEXITCODE -ne 0) { Write-Error 'build failed' }
}

$Stage = Join-Path $Here "bin\mod\$ModId"
if (-not (Test-Path (Join-Path $Stage 'modinfo.json'))) {
    Write-Error "$Stage was not staged. Run without -NoBuild."
}

# ==================================================================== install
Write-Host "==> installing to $Dest"
# Remove rather than merge: a stale DLL from an older version left beside the
# new one gives BepInEx two plugins claiming the same GUID.
if (Test-Path $Dest) { Remove-Item -Recurse -Force $Dest }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Dest) | Out-Null
Copy-Item -Recurse $Stage $Dest
Get-ChildItem -Recurse -File $Dest | ForEach-Object {
    Write-Host ('    ' + $_.FullName.Substring($Dest.Length + 1))
}

# =========================================================== enabledMods.txt
# MultiFolderLoader.cs:228 skips a mod folder when the enabled list exists and
# does not contain it. Absent file => null list => the gate short-circuits and
# every folder loads. The game writes this file CRLF-separated.
$EnabledMods = Join-Path $StreamingAssets 'enabledMods.txt'
if (Test-Path $EnabledMods) {
    $entries = (Get-Content $EnabledMods) | ForEach-Object { $_.Trim() }
    if ($entries -notcontains $ModId) {
        Add-Content -Path $EnabledMods -Value $ModId
        Write-Host "==> appended '$ModId' to enabledMods.txt (stopgap)"
    }
    Write-Host '!! That file is rewritten from the ENABLED list the next time you press' -ForegroundColor Yellow
    Write-Host "!! Apply in Remix. Enable 'True Resolution' there to make it stick." -ForegroundColor Yellow
} else {
    Write-Host '==> enabledMods.txt is absent, so MultiFolderLoader loads every folder under'
    Write-Host '==> mods/ - the plugin is already active even though Remix shows it disabled.'
    Write-Host "==> The first Remix 'Apply' creates the file from the ENABLED mods only, so"
    Write-Host '==> enable it there or it stops loading.'
}

$version = (Get-Content -Raw (Join-Path $Here 'mod\modinfo.json') | ConvertFrom-Json).version
$log = Join-Path $RainWorldDir 'BepInEx\LogOutput.log'

Write-Host ''
Write-Host "Next: launch, main menu -> Remix -> enable 'True Resolution' -> Apply -> restart."
Write-Host ''
Write-Host 'Verify:'
Write-Host "    Select-String -Path `"$log`" -SimpleMatch 'True Resolution $version loaded'"
Write-Host "    Select-String -Path `"$log`" -SimpleMatch 'display probe'"
Write-Host "    Select-String -Path `"$log`" -SimpleMatch 'backbuffer: CONFIRMED'"
Write-Host ''
Write-Host 'Config (written on the first successful run):'
Write-Host "    $RainWorldDir\BepInEx\config\steinkoloss.trueresolution.cfg"
Write-Host ''
Write-Host 'After a Rain World update, re-enable the mod in Remix once: MultiFolderLoader'
Write-Host 'blanks enabledMods.txt whenever the game version string changes.'
