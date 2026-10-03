# Castle Story Plus installer / updater for Windows.
#
# Installs BepInEx 5 (if missing) and the latest Castle Story Plus release from GitHub into the
# Castle Story folder. Run it again to update. The game uses it too ("Update" in the main menu).
#
#   install.bat                   install or update to the latest release
#   install.bat -Tag v0.3.0       install a specific release
#   install.bat -Local DIR        install from an unpacked release folder (contains files\)
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
$Bootstrap = "BepInEx\core\CastleStoryPlus.Bootstrap.dll"
$PluginDir = "BepInEx\plugins\CastleStoryPlus"

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
if (-not $Local -and $ScriptDir -and (Test-Path (Join-Path $ScriptDir "files\BepInEx"))) {
    $Local = $ScriptDir
}

if ($WaitPid -gt 0) {
    Log "waiting for the game to close..."
    Wait-Process -Id $WaitPid -ErrorAction SilentlyContinue
}

if (-not $GameDir) {
    $GameDir = Find-GameDir
    if (-not $GameDir) { Fail "Castle Story not found; pass -GameDir ""C:\path\to\Castle Story""" }
}
$Exe = Join-Path $GameDir "Castle Story.exe"
if (-not (Test-Path $Exe)) { Fail "not a Castle Story folder: $GameDir" }
Log "game folder: $GameDir"
$DoorstopConfig = Join-Path $GameDir "doorstop_config.ini"

function Set-TargetAssembly($path) {
    if (Test-Path $DoorstopConfig) {
        $config = Get-Content $DoorstopConfig
        $config = $config -replace '^target_assembly\s*=.*', "target_assembly = $path"
        Set-Content -Path $DoorstopConfig -Value $config
    }
}

if ($Uninstall) {
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $GameDir $PluginDir), (Join-Path $GameDir $Bootstrap)
    Set-TargetAssembly "BepInEx\core\BepInEx.Preloader.dll"
    Log "Castle Story Plus removed. BepInEx is still installed; delete winhttp.dll from the game folder to start the game without it."
    exit 0
}

$Tmp = Join-Path ([IO.Path]::GetTempPath()) ("CastleStoryPlus-" + [Guid]::NewGuid())
New-Item -ItemType Directory -Path $Tmp | Out-Null
try {
    # 1. Castle Story Plus release (the Windows package bundles BepInEx x64 in bepinex\)
    $versionFile = Join-Path $GameDir "$PluginDir\version.txt"
    $installed = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() } else { "" }
    $hasBepInEx = Test-Path (Join-Path $GameDir "BepInEx\core\BepInEx.Preloader.dll")
    $source = $null
    if ($Local) {
        $source = $Local
        $localVersion = Join-Path $Local "files\$PluginDir\version.txt"
        $version = if (Test-Path $localVersion) { (Get-Content $localVersion -Raw).Trim() } else { "local" }
    }
    else {
        $api = if ($Tag) { "https://api.github.com/repos/$Repo/releases/tags/$Tag" } else { "https://api.github.com/repos/$Repo/releases/latest" }
        $release = Invoke-RestMethod -Uri $api -Headers @{ Accept = "application/vnd.github+json" }
        $version = $release.tag_name
        $asset = $release.assets | Where-Object { $_.name -like "CastleStoryPlus-*-windows.zip" } | Select-Object -First 1
        if (-not $version -or -not $asset) { Fail "release $api has no Castle Story Plus package for Windows" }
        if (-not $Force -and $installed -eq $version -and $hasBepInEx) {
            Log "Castle Story Plus $version is already installed."
        }
        else {
            Log "downloading Castle Story Plus $version..."
            $zip = Join-Path $Tmp "mod.zip"
            Download $asset.browser_download_url $zip
            Expand-Archive -Path $zip -DestinationPath (Join-Path $Tmp "mod") -Force
            $files = Get-ChildItem -Path (Join-Path $Tmp "mod") -Directory -Recurse -Filter "files" | Select-Object -First 1
            if (-not $files) { Fail "unexpected release layout" }
            $source = $files.Parent.FullName
        }
    }

    # 2. BepInEx, from the package when it fits the game (x64), else from the BepInEx releases.
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
        # Replace the plugin folder (settings live in BepInEx\config and are kept).
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $GameDir $PluginDir)
        Copy-Item -Recurse -Force (Join-Path $source "files\BepInEx") $GameDir
        Set-Content -Path $versionFile -Value $version
        $was = if ($installed) { " (was $installed)" } else { "" }
        Log "installed Castle Story Plus $version$was"
    }

    # 3. Start BepInEx through the bootstrap (needed on the game's old Mono runtime).
    Set-TargetAssembly $Bootstrap
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
