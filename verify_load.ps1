# Map the patched empire.retail.dll with the real Windows loader (no code runs: DONT_RESOLVE_DLL_REFERENCES)
# and check the patch in memory after relocation. Must run in 32-bit PowerShell:
#   C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -ExecutionPolicy Bypass -File verify_load.ps1 <dll>
param([Parameter(Mandatory = $true)][string]$Dll)
if ([IntPtr]::Size -ne 4) { throw "run this in 32-bit PowerShell (SysWOW64)" }
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Ld {
	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	public static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
	[DllImport("kernel32.dll")] public static extern bool FreeLibrary(IntPtr h);
	public static byte[] Read(IntPtr p, int n) { var b = new byte[n]; Marshal.Copy(p, b, 0, n); return b; }
}
"@
$path = (Resolve-Path $Dll).Path
$h = [Ld]::LoadLibraryExW($path, [IntPtr]::Zero, 0x1)
if ($h -eq [IntPtr]::Zero) { throw "LoadLibraryEx failed, error $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
$base = [uint32]$h.ToInt32(); $delta = [int64]$base - 0x10000000
"mapped at 0x{0:X8} (delta {1})" -f $base, $delta
function At([uint32]$va, [int]$n) { [Ld]::Read([IntPtr]([int64]$va - 0x10000000 + $base), $n) }
function Hex($b) { ($b | ForEach-Object { $_.ToString('X2') }) -join ' ' }
$file = [IO.File]::ReadAllBytes($path)
$pe = [BitConverter]::ToInt32($file, 0x3C); $nSec = [BitConverter]::ToUInt16($file, $pe + 6)
$sec = $pe + 24 + [BitConverter]::ToUInt16($file, $pe + 20) + ($nSec - 1) * 40
$secName = [Text.Encoding]::ASCII.GetString($file, $sec, 4)
$secVa = [uint32](0x10000000 + [BitConverter]::ToUInt32($file, $sec + 12)); $secLen = [BitConverter]::ToInt32($file, $sec + 8)
$fileSec = New-Object byte[] $secLen; [Array]::Copy($file, [BitConverter]::ToInt32($file, $sec + 20), $fileSec, 0, $secLen)
$ok = $true
$same = [Linq.Enumerable]::SequenceEqual([byte[]](At $secVa $secLen), [byte[]]$fileSec)
"section {0} at {1:X8}, {2} bytes, identical to file after relocation: {3}" -f $secName, $secVa, $secLen, $same
if ($secName -ne '.r8p' -or -not $same) { $ok = $false }
foreach ($hook in @(@(0x11225E4A, 'panel binding'), @(0x111FA976, 'listeners'), @(0x111FB385, 'join kick'), @(0x111FAF72, 'kick click'))) {
	$ins = At $hook[0] 5
	$dest = [int64]$hook[0] + 5 + [BitConverter]::ToInt32($ins, 1)
	$in = $ins[0] -eq 0xE9 -and $dest -ge $secVa -and $dest -lt ($secVa + $secLen)
	"hook {0,-14} {1:X8}: {2} -> {3:X8} (in section: {4})" -f $hook[1], $hook[0], (Hex $ins), $dest, $in
	if (-not $in) { $ok = $false }
}
$cap = Hex (At 0x111EFED3 6); "capacity pushes : $cap (expected 6A 02 6A 00 6A 08)"; if ($cap -ne '6A 02 6A 00 6A 08') { $ok = $false }
$j = Hex (At 0x111FB3B5 5); "join slot test  : $j (expected 83 FD 01 72 04)"; if ($j -ne '83 FD 01 72 04') { $ok = $false }
$l = Hex (At 0x111FB8EA 3); "leave kick root : $l (expected 8B F3 90)"; if ($l -ne '8B F3 90') { $ok = $false }
# a relocated original instruction next to a hook: push "panel_player2" must have been fixed up by the loader
$push = At 0x11225E18 5; $imm = [BitConverter]::ToUInt32($push, 1)
"relocated push  : imm {0:X8} expected {1:X8}" -f $imm, [uint32](0x118E2FF0 + $delta)
if ($imm -ne [uint32](0x118E2FF0 + $delta)) { $ok = $false }
[void][Ld]::FreeLibrary($h)
if ($ok) { "RESULT: patched DLL loads and the patch is intact in memory" } else { "RESULT: PROBLEM - see above"; exit 1 }
