# Castle Story Plus installer / updater for Windows.
#
# Castle Story Plus runs on the Unity 6 port: a player of its own, made from your Castle Story install the first time
# it starts. This puts the latest release from GitHub in the Castle Story folder (CastleStoryPlus-Unity6), and points
# the game's BepInEx doorstop at a small launcher, so Steam's Play starts the port. Run it again to update. The game
# uses it too ("Update" in the main menu).
#
#   install.bat                   install or update to the latest release
#   install.bat -Tag v0.8.1       install a specific release
#   install.bat -Local DIR        install from an unpacked release folder (contains CastleStoryPlus-Unity6\)
#   install.bat -Uninstall        remove Castle Story Plus (BepInEx and its config stay)
#
# Options: -GameDir DIR, -Force (reinstall even if up to date), -WaitPid PID (wait for the game to
# exit first), -Restart (start the game through Steam afterwards).
param(
    [string]$GameDir = "",
    [string]$Tag = "",
    [string]$Local = "",
    [switch]$Uninstall,
    [switch]$Force,
    [int]$WaitPid = 0,
    [switch]$Restart
)
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Repo = "Tricky12321/CastleStoryPlus"
$BepInExVersion = "5.4.23.5"
$SteamAppId = "227860"
$PortDir = "CastleStoryPlus-Unity6"
$Launcher = "BepInEx\core\CastleStoryPlus.PortLauncher.dll"
$PortVersion = "$PortDir\BepInEx\plugins\CastleStoryPlus\version.txt"
# The mod for the original game (0.8.0 and before), replaced by the port.
$OldBootstrap = "BepInEx\core\CastleStoryPlus.Bootstrap.dll"
$OldPluginDir = "BepInEx\plugins\CastleStoryPlus"

function Log($message) { Write-Host "[Castle Story Plus] $message" }
function Fail($message) { Write-Host "[Castle Story Plus] ERROR: $message" -ForegroundColor Red; exit 1 }

function Find-GameDir {
    $steam = (Get-ItemProperty -Path "HKCU:\Software\Valve\Steam" -Name SteamPath -ErrorAction SilentlyContinue).SteamPath
    $libraries = @()
    if ($steam) {
        $libraries += $steam
        $vdf = Join-Path $steam "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            foreach ($line in Get-Content $vdf) {
                if ($line -match '^\s*"path"\s*"(.*)"') {
                    $libraries += $Matches[1] -replace '\\\\', '\'
                }
            }
        }
    }
    $libraries += "C:\Program Files (x86)\Steam", "C:\Program Files\Steam"
    foreach ($library in $libraries) {
        $dir = Join-Path $library "steamapps\common\Castle Story"
        if (Test-Path (Join-Path $dir "Castle Story.exe")) {
            return $dir
        }
    }
    return $null
}

# x64 or x86 build of the game, from the exe's PE header.
function Get-GameArch($exe) {
    $bytes = [IO.File]::ReadAllBytes($exe)
    $pe = [BitConverter]::ToInt32($bytes, 0x3C)
    $machine = [BitConverter]::ToUInt16($bytes, $pe + 4)
    if ($machine -eq 0x8664) { return "x64" }
    return "x86"
}

function Download($url, $file) {
    Invoke-WebRequest -Uri $url -OutFile $file -UseBasicParsing
}

# Empty when run straight from the web (irm ... | iex).
$ScriptDir = if ($PSCommandPath) { Split-Path -Parent $PSCommandPath } else { "" }
if (-not $Local -and $ScriptDir -and (Test-Path (Join-Path $ScriptDir $PortDir))) {
    $Local = $ScriptDir
}

if ($WaitPid -gt 0) {
    Log "waiting for the game to close..."
    Wait-Process -Id $WaitPid -ErrorAction SilentlyContinue
    # The original game, which started the port, ends right after it and has the launcher loaded until then.
    Get-Process -Name "Castle Story" -ErrorAction SilentlyContinue | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
}

if (-not $GameDir) {
    $GameDir = Find-GameDir
    if (-not $GameDir) { Fail "Castle Story not found; pass -GameDir ""C:\path\to\Castle Story""" }
}
$Exe = Join-Path $GameDir "Castle Story.exe"
if (-not (Test-Path $Exe)) { Fail "not a Castle Story folder: $GameDir" }
Log "game folder: $GameDir"

# Windows does not let a file be replaced while a program has it open: nothing is changed until no program runs from
# the game's folder (the port, the original game holding the launcher, Unity's crash handler).
function Wait-GameClosed {
    $deadline = (Get-Date).AddSeconds(60)
    while ($true) {
        $running = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($GameDir, [StringComparison]::OrdinalIgnoreCase) })
        if ($running.Count -eq 0) { return }
        if ((Get-Date) -gt $deadline) {
            Fail ("the game is still running (" + (($running | ForEach-Object { $_.ProcessName }) -join ", ") + "); close it and run the installer again")
        }
        Start-Sleep -Seconds 1
    }
}

# A step that can fail while Windows still holds a file a moment after its program closed (an antivirus scan, the
# Steam overlay): tried again for about 20 seconds before giving up.
function Retry($what, [scriptblock]$step) {
    for ($i = 1; $i -le 10; $i++) {
        try {
            & $step
            return
        }
        catch {
            if ($i -eq 10) {
                Fail "$what failed: $($_.Exception.Message). Close the game and any program using its folder, then run the installer again."
            }
            Start-Sleep -Seconds 2
        }
    }
}

Wait-GameClosed
$DoorstopConfig = Join-Path $GameDir "doorstop_config.ini"

function Set-TargetAssembly($path) {
    if (Test-Path $DoorstopConfig) {
        $config = Get-Content $DoorstopConfig
        $config = $config -replace '^target_assembly\s*=.*', "target_assembly = $path"
        Set-Content -Path $DoorstopConfig -Value $config
    }
}

if ($Uninstall) {
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $GameDir $PortDir), (Join-Path $GameDir $Launcher), (Join-Path $GameDir $OldPluginDir), (Join-Path $GameDir $OldBootstrap)
    Set-TargetAssembly "BepInEx\core\BepInEx.Preloader.dll"
    Log "Castle Story Plus removed. BepInEx is still installed; delete winhttp.dll from the game folder to start the game without it."
    exit 0
}

$Tmp = Join-Path ([IO.Path]::GetTempPath()) ("CastleStoryPlus-" + [Guid]::NewGuid())
New-Item -ItemType Directory -Path $Tmp | Out-Null
try {
    # 1. Castle Story Plus release (the Windows package bundles BepInEx x64 in bepinex\)
    $versionFile = Join-Path $GameDir $PortVersion
    $installed = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() } else { "" }
    $hasBepInEx = Test-Path (Join-Path $GameDir "BepInEx\core\BepInEx.Preloader.dll")
    $source = $null
    if ($Local) {
        $source = $Local
        $localVersion = Join-Path $Local $PortVersion
        $version = if (Test-Path $localVersion) { (Get-Content $localVersion -Raw).Trim() } else { "local" }
    }
    else {
        $api = if ($Tag) { "https://api.github.com/repos/$Repo/releases/tags/$Tag" } else { "https://api.github.com/repos/$Repo/releases/latest" }
        $release = Invoke-RestMethod -Uri $api -Headers @{ Accept = "application/vnd.github+json" }
        $version = $release.tag_name
        $asset = $release.assets | Where-Object { $_.name -like "CastleStoryPlus-*-windows.zip" } | Select-Object -First 1
        if (-not $version -or -not $asset) { Fail "release $api has no Castle Story Plus package for Windows" }
        if (-not $Force -and $installed -eq $version -and (Test-Path (Join-Path $GameDir $Launcher))) {
            Log "Castle Story Plus $version is already installed."
        }
        else {
            Log "downloading Castle Story Plus $version..."
            $zip = Join-Path $Tmp "mod.zip"
            Download $asset.browser_download_url $zip
            Expand-Archive -Path $zip -DestinationPath (Join-Path $Tmp "mod") -Force
            $port = Get-ChildItem -Path (Join-Path $Tmp "mod") -Directory -Recurse -Depth 1 -Filter $PortDir | Select-Object -First 1
            if (-not $port -or -not (Test-Path (Join-Path $port.Parent.FullName "launcher"))) { Fail "unexpected release layout" }
            $source = $port.Parent.FullName
        }
    }

    # 2. BepInEx in the game, from the package when it fits the game (x64), else from the BepInEx releases: only its
    # doorstop is used, to start the launcher.
    if (-not $hasBepInEx) {
        $arch = Get-GameArch $Exe
        Log "installing BepInEx $BepInExVersion ($arch)..."
        $bundled = if ($source) { Join-Path $source "bepinex" } else { "" }
        if ($arch -eq "x64" -and $bundled -and (Test-Path (Join-Path $bundled "BepInEx"))) {
            Copy-Item -Recurse -Force (Join-Path $bundled "*") $GameDir
        }
        else {
            $zip = Join-Path $Tmp "bepinex.zip"
            Download "https://github.com/BepInEx/BepInEx/releases/download/v$BepInExVersion/BepInEx_win_${arch}_$BepInExVersion.zip" $zip
            Expand-Archive -Path $zip -DestinationPath $GameDir -Force
        }
    }

    if ($source) {
        # Replace the port's folder. Its mod settings (BepInEx\config) are kept; what its first start made from the
        # game (Content) is made again by the new player.
        $target = Join-Path $GameDir $PortDir
        $config = Join-Path $target "BepInEx\config"
        $keptConfig = Join-Path $Tmp "config"
        if (Test-Path $config) {
            Copy-Item -Recurse -Force $config $keptConfig
        }
        # The new version is put together beside the old one first, then the folders are swapped by renaming: a
        # file still held open makes the swap fail before anything is lost, and the old version is put back.
        $staged = "$target.new"
        $old = "$target.old"
        Retry "removing what an earlier update left" {
            foreach ($leftover in @($staged, $old)) {
                if (Test-Path $leftover) { Remove-Item -Recurse -Force -ErrorAction Stop $leftover }
            }
        }
        Copy-Item -Recurse -Force -ErrorAction Stop (Join-Path $source $PortDir) $staged
        if (Test-Path $keptConfig) {
            Copy-Item -Recurse -Force -ErrorAction Stop $keptConfig (Join-Path $staged "BepInEx")
        }
        Retry "moving the installed version aside" {
            if (Test-Path $target) { Move-Item -ErrorAction Stop $target $old }
        }
        try {
            Move-Item -ErrorAction Stop $staged $target
        }
        catch {
            if (Test-Path $old) { Move-Item -ErrorAction SilentlyContinue $old $target }
            Fail "could not put the new version in place: $($_.Exception.Message). The installed version was kept."
        }
        Set-Content -Path $versionFile -Value $version
        Retry "replacing the launcher" {
            Copy-Item -Force -ErrorAction Stop (Join-Path $source "launcher\CastleStoryPlus.PortLauncher.dll") (Join-Path $GameDir "BepInEx\core")
        }
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $old
        # The mod for the original game gives way to the port.
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $GameDir $OldPluginDir), (Join-Path $GameDir $OldBootstrap)
        $was = if ($installed) { " (was $installed)" } else { "" }
        Log "installed Castle Story Plus $version$was"
    }
    if (-not (Test-Path (Join-Path $GameDir $Launcher))) { Fail "the launcher is missing; run the installer again with -Force" }

    # 3. The game's doorstop starts the launcher, which starts the port.
    Set-TargetAssembly $Launcher
}
finally {
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $Tmp
}

if ($Restart) {
    Log "starting Castle Story..."
    Start-Sleep -Seconds 5
    Start-Process "steam://rungameid/$SteamAppId"
}
Log "done."
