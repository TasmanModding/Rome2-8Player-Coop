# 8-player MP campaign mod for Total War: Rome II (experimental) - installer / uninstaller.
#
# Works entirely from THIS player's own installed game: it extracts the lobby layout from data\data_rome2.pack,
# builds the 8-player version, writes data\eightplayer_coop.pack (a movie pack: the game loads it by itself), and
# patches empire.retail.dll after backing it up. Every player must install it (identical output on identical game
# versions), or they won't be able to play together.
#
#   powershell -ExecutionPolicy Bypass -File install.ps1              # install
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall   # restore everything
param(
	[string]$GamePath = "",
	[switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$lib = Join-Path $PSScriptRoot 'lib'
foreach ($t in @(@('Layout', 'Layout86'), @('Pack', 'PackTools'), @('EightPlayerPatcher', 'Patcher'))) {
	if (-not ($t[0] -as [type])) { Add-Type -Path (Join-Path $lib "$($t[1]).cs") }
}

# find the game in any Steam library if no path was given
if (-not $GamePath) {
	$roots = @()
	foreach ($k in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
		try { $p = (Get-ItemProperty $k -ErrorAction Stop); if ($p.SteamPath) { $roots += $p.SteamPath }; if ($p.InstallPath) { $roots += $p.InstallPath } } catch { }
	}
	$roots += "C:\Program Files (x86)\Steam"
	$libs = @()
	foreach ($r in ($roots | Select-Object -Unique)) {
		$libs += $r
		$vdf = Join-Path $r 'steamapps\libraryfolders.vdf'
		if (Test-Path $vdf) { foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $libs += $m.Groups[1].Value.Replace('\\', '\') } }
	}
	foreach ($l in ($libs | Select-Object -Unique)) {
		$c = Join-Path $l 'steamapps\common\Total War Rome II'
		if (Test-Path (Join-Path $c 'empire.retail.dll')) { $GamePath = $c; break }
	}
	if (-not $GamePath) { throw "Couldn't find Total War: Rome II. Run with -GamePath ""<your game folder>""." }
}
"Game folder: $GamePath"

$dll = Join-Path $GamePath 'empire.retail.dll'
$backup = "$dll.8p-original"
$packName = 'eightplayer_coop.pack'
$dataDir = Join-Path $GamePath 'data'
$packPath = Join-Path $dataDir $packName
$layoutName = 'ui\frontend ui\mp_grand_campaign'
$orig = [EightPlayerPatcher]::OriginalSha256

function Sha($p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash }

if (Get-Process -Name 'Rome2*' -ErrorAction SilentlyContinue) { throw "Close Rome II before running this." }
if (-not (Test-Path $dll)) { throw "empire.retail.dll not found in '$GamePath' (pass -GamePath)." }

if ($Uninstall) {
	if (Test-Path $backup) {
		if ((Sha $backup) -ne $orig) { throw "Backup doesn't match the original game DLL; not restoring. Use Steam 'Verify integrity' instead." }
		Copy-Item $backup $dll -Force
		Remove-Item $backup
		"Restored the original empire.retail.dll."
	}
	else { "No backup found; empire.retail.dll left as it is." }
	if (Test-Path $packPath) { Remove-Item $packPath; "Removed data\$packName." }
	"Uninstalled."
	return
}

# ---- 1. engine DLL --------------------------------------------------------------------------------
$tmp = "$dll.8p-tmp"
$current = Sha $dll
if ($current -eq $orig) {
	if (-not (Test-Path $backup)) { Copy-Item $dll $backup }
	[EightPlayerPatcher]::Build($backup, $tmp) | Out-Host
	Move-Item $tmp $dll -Force
	"Patched empire.retail.dll (original saved as $(Split-Path $backup -Leaf))."
}
elseif ((Test-Path $backup) -and (Sha $backup) -eq $orig) {
	[EightPlayerPatcher]::Build($backup, $tmp) | Out-Null
	if ($current -eq (Sha $tmp)) { Remove-Item $tmp; "empire.retail.dll is already patched." }
	else { Move-Item $tmp $dll -Force; "Updated empire.retail.dll to this version of the mod." }
}
else { throw "empire.retail.dll is not the version this mod was made for (sha256 $current). Not touching it. If the game was updated, this mod needs updating too." }
"empire.retail.dll sha256 $(Sha $dll). All players should see the same hash."

# ---- 2. lobby layout -> movie pack -------------------------------------------------------------------
$layout = [Pack]::ReadLatest($dataDir, $layoutName, [string[]]@('data.pack', 'data_rome2.pack'))
if (-not $layout) { throw "'$layoutName' not found in data.pack / data_rome2.pack" }
$work = Join-Path ([IO.Path]::GetTempPath()) ("r2_8p_" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
try {
	$inFile = Join-Path $work 'original_layout'; $outFile = Join-Path $work 'layout_8p'
	[IO.File]::WriteAllBytes($inFile, $layout)
	& (Join-Path $PSScriptRoot 'build_lobby_layout.ps1') -Original $inFile -Out $outFile | Out-Host
	$list = New-Object 'System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string,byte[]]]'
	$list.Add((New-Object 'System.Collections.Generic.KeyValuePair[string,byte[]]' $layoutName, ([IO.File]::ReadAllBytes($outFile))))
	[Pack]::WriteMovie($packPath, $list)
	"Wrote data\$packName (sha256 $(Sha $packPath)). All players should see the same hash."
}
finally {
	$resolved = [IO.Path]::GetFullPath($work)
	if ((Split-Path $resolved -Leaf) -match '^r2_8p_[0-9a-f]{32}$') { Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue }
}

# ---- 3. match code: same code on every PC = same mod files -------------------------------------------
$code = (Get-FileHash -InputStream ([IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes((Sha $dll) + '|' + (Sha $packPath)))) -Algorithm SHA256).Hash.Substring(0, 8)
""
"MATCH CODE: $code   <- compare this with your friends. Same code = same mod files."
""
"Installed. Reminders:"
"  - Every player needs this mod installed, or they can't join each other."
"  - Host: Multiplayer > Campaign > host as usual; the lobby now has 8 slots. Use the invite buttons on the empty slots."
"  - EXPERIMENTAL. This modifies the game's engine DLL; Steam 'Verify integrity of game files' also undoes it."
"  - Undo at any time:  powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall"
