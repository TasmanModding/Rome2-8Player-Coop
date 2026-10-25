# Build the 8-player MP campaign lobby layout from the game's own ui\frontend ui\mp_grand_campaign (Version086),
# using lib\Layout86.cs.
#
# Changes:
#   - every player panel compacted from 257x416 to 257x101: portrait left, name / faction dropdown / ready status
#     to its right, flag and effect icons shrunk into the corner (same components, only moved and resized)
#   - eight panels in two columns of four where the original two panels were:
#         P1 | map | P2
#         P3 |     | P4
#         P5 |     | P6
#         P7 |     | P8
#     The engine fills panels by session slot (panels[slot]); slot 0 is the host = panel_player1.
#   - panel_player3..8 = copies of panel_player2 (+ the "bg" frame with the invite button behind it), fresh uids
#   - the copied frames are named invite_frame3..8 (the engine patch wires their invite buttons)
#   - title "Eight Player Campaign"
param(
	[string]$Original = (Join-Path $PSScriptRoot "re\ui_orig\ui\frontend ui\mp_grand_campaign"),
	[string]$Out = (Join-Path $PSScriptRoot "pack_src\ui\frontend ui\mp_grand_campaign")
)
$ErrorActionPreference = 'Stop'
if (-not ('Layout' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot "lib\Layout86.cs") }

$root = [Layout]::Read([IO.File]::ReadAllBytes($Original))
$main = [Layout]::Find($root, 'main_panel')
$kids = $main.List('children')
function Child($node, $name) { $c = $node.Child($name); if (-not $c) { throw "no '$name' under $($node.Str('name'))" }; $c }
function SetXY($node, $x, $y) { $node.SetI('x', $x); $node.SetI('y', $y) }
function Hide($node) { $node.Field('visible').V = [byte]0 }
# resize every state of a component (and stretch/shrink its images to match)
function Resize($node, $w, $h) {
	foreach ($st in $node.List('states')) {
		$ow = $st.I('w'); $oh = $st.I('h'); $st.SetI('w', $w); $st.SetI('h', $h)
		foreach ($si in $st.List('state_images')) {
			if ($ow -gt 0) { $si.SetI('x', [int]($si.I('x') * $w / $ow)); $si.SetI('w', [int]($si.I('w') * $w / $ow)) }
			if ($oh -gt 0) { $si.SetI('y', [int]($si.I('y') * $h / $oh)); $si.SetI('h', [int]($si.I('h') * $h / $oh)) }
		}
	}
}
# shorten a 9-slice background: the bottom row moves up, the middle pieces shrink
function Shorten($node, $newH) {
	foreach ($st in $node.List('states')) {
		$h = $st.I('h'); $d = $h - $newH; $st.SetI('h', $newH)
		foreach ($si in $st.List('state_images')) {
			if ($si.I('y') -ge ($h / 2)) { $si.SetI('y', $si.I('y') - $d) }
			elseif (($si.I('y') + $si.I('h')) -gt ($h / 2)) { $si.SetI('h', [Math]::Max(1, $si.I('h') - $d)) }
		}
	}
}

$panelH = 101; $gap = 4
$top = 92
$colX = @(30, 817)
$rowY = @(0..3 | ForEach-Object { $top + $_ * ($panelH + $gap) })    # 92, 197, 302, 407 (last ends at 508)
# slot n (1-based) -> column (n-1)%2, row floor((n-1)/2)
function PosOf($n) { @($colX[($n - 1) % 2], $rowY[[Math]::Floor(($n - 1) / 2)]) }

# panel-relative positions; 'player' children are relative to 'player'
function Compact($panel) {
	Shorten $panel $panelH
	SetXY (Child $panel 'icon_frame') 4 3                           # 70x70 portrait frame: panel 4..74, 3..73
	$pl = Child $panel 'player'; $px = 7; $py = 6; SetXY $pl $px $py  # 64x64 portrait
	$name = Child $pl 'dy_player_name'; Resize $name 140 26; SetXY $name (80 - $px) (3 - $py)   # panel 80..220, 3..29
	$flag = Child $pl 'flag'; Resize $flag 28 28; SetXY $flag (226 - $px) (2 - $py)            # panel 226..254, 2..30
	SetXY (Child $pl 'faction_dropdown') (70 - $px) (36 - $py)      # 186x30: panel 70..256, 36..66
	$status = Child $pl 'status'; Resize $status 250 26; SetXY $status (4 - $px) (74 - $py)   # full width: panel 4..254, 74..100
	SetXY (Child $pl 'icon_voicechat') (34 - $px) (36 - $py)        # 50x50, its icon sits on the portrait's corner
	$fx = Child $pl 'subpanel_effect_bundles'; SetXY $fx -5000 -5000 # a column of faction effect icons: no room (the game re-shows it)
	$kick = $panel.Child('button_kick'); if ($kick) { SetXY $kick 2 2 }
}

$p1 = Child $main 'panel_player1'; $p2 = Child $main 'panel_player2'; $bg = Child $main 'bg'
$vc2 = $p2.Child('victory_conditions'); $vc2AbsY = $p2.I('y') + $vc2.I('y')
Compact $p1; Compact $p2
Shorten $bg $panelH
SetXY (Child $bg 'button_invite') 103 25
$xy = PosOf 1; SetXY $p1 $xy[0] $xy[1]
$xy = PosOf 2; SetXY $p2 $xy[0] $xy[1]; SetXY $bg $xy[0] $xy[1]
$vc2.SetI('y', $vc2AbsY - $p2.I('y'))        # panel 2's victory box stays where it was on screen

# fresh uids, deterministic so every player builds byte-identical output
$taken = New-Object 'System.Collections.Generic.HashSet[uint32]'
foreach ($u in [Layout]::AllU32($root)) { [void]$taken.Add($u) }
$script:nextUid = [uint32]0x7E4D0001
$alloc = [Func[uint32]] { while ($taken.Contains($script:nextUid)) { $script:nextUid++ }; $u = $script:nextUid; [void]$taken.Add($u); $script:nextUid++; return $u }

$insertAt = $kids.IndexOf($p2) + 1
foreach ($n in 3..8) {
	$xy = PosOf $n
	$frame = [Layout]::DeepClone($bg); [Layout]::Reassign($frame, $alloc); SetXY $frame $xy[0] $xy[1]
	$frame.SetStr('name', "invite_frame$n")
	$panel = [Layout]::DeepClone($p2); [Layout]::Reassign($panel, $alloc); SetXY $panel $xy[0] $xy[1]
	$panel.SetStr('name', "panel_player$n")
	$vc = $panel.Child('victory_conditions')
	if ($vc) { Hide $vc; $vc.SetI('y', 3000) }                      # one victory box per side is enough
	$kids.Insert($insertAt, $frame); $kids.Insert($insertAt + 1, $panel); $insertAt += 2
}

# title
$heading = Child $main 'panel_heading'
foreach ($st in $heading.List('states')) { $st.SetW('text', 'Eight Player Campaign'); $st.SetW('text_key', '') }
$heading.SetW('tooltip', 'Up to 8 players: team up against the AI or play against each other.'); $heading.SetW('tooltip_key', '')

# write and verify
$bytes = [Layout]::Write($root)
$check = [Layout]::Read($bytes)
$all = [Layout]::DefinedUidList($check)
$dups = $all | Group-Object | Where-Object Count -gt 1
if ($dups) { throw "duplicate uids after build" }
foreach ($n in 1..8) { if (-not [Layout]::Find($check, "panel_player$n")) { throw "panel_player$n missing" } }
New-Item -ItemType Directory -Force (Split-Path $Out -Parent) | Out-Null
[IO.File]::WriteAllBytes($Out, $bytes)
"wrote {0} ({1:N0} bytes, original {2:N0}); {3} uids, all unique" -f $Out, $bytes.Length, (Get-Item -LiteralPath $Original).Length, $all.Count
