## Install or update

The installer finds Castle Story in your Steam libraries, installs [BepInEx 5](https://github.com/BepInEx/BepInEx) if it is missing and installs the latest Castle Story Plus. Run it again at any time to update. Once installed, the game also tells you in the main menu when a new version is out and can update itself.

**Windows:** download [`install.bat`](https://github.com/Tricky12321/CastleStoryPlus/releases/latest/download/install.bat) and double-click it. Or paste this into PowerShell:
```powershell
& ([scriptblock]::Create((New-Object Net.WebClient).DownloadString('https://github.com/Tricky12321/CastleStoryPlus/releases/latest/download/install.ps1')))
```

**Linux:** run this in a terminal:
```bash
curl -fsSL https://github.com/Tricky12321/CastleStoryPlus/releases/latest/download/install.sh | bash
```
If Steam is closed, the installer also sets the game's launch option. Otherwise set it once yourself (right-click Castle Story > Properties > Launch Options): `./run_bepinex.sh %command%`

Offline: download the package for your system below (`CastleStoryPlus-*-windows.zip` or `CastleStoryPlus-*-linux.zip`, both include BepInEx), unpack it and run `install.bat` (Windows) or `bash install.sh` (Linux) from that folder.
