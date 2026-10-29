# Rome II 8-player co-op: reverse-engineering notes

Base: `empire.retail.dll` sha256 `07CB2215…B2B7` (Steam build 25370349), image base 0x10000000, 32-bit, MSVC (static CRT).
Tools: Ghidra 12.1.4 headless (`re/ghidra_proj`, project `r2`; `re/q.ps1` runs `re/query.txt` through
`re/ghidra_scripts/Query.java`; `$env:R2SCRIPT=FindFn.java` for name searches), capstone helper `re/tools/r2tool.py`
(`str`, `imm`, `xref`, `dis`, `func`), PFH4 reader `re/tools/pack.py`, exploratory layout parser `re/tools/layout86.py`.

## Patches (lib/Patcher.cs), code in new section `.r8p` after `.reloc`
| Patch | Site | Notes |
|---|---|---|
| Session capacity 8 | 111EFED7 `push 2` -> `push 8` | host-campaign screen; ctor 10F6D870(players, spectators, sides, ...) stores at +40/44/48, no clamp. Other 2-player sites: 10FC310A quick battle, 10FB4B8F drop-in battle, 10ED504D/10ED2262 debug |
| Bind panel_player3..8 | 11225E4A (call StrDtor) -> stub | lobby init FUN_11225C80, edi = lobby, panels vector lobby+B0h (count B4h, data B8h) |
| Listeners for all kick/invite buttons | 111FA976 (7 bytes) | FUN_111FA890, ebx = lobby, edi = lobby+44h listener; AddListener 10CD2990 on ([c+174h] ? +2Ch : 0) |
| Join: kick button of the joining slot | 111FB385 (7 bytes) + 111FB3B5 `jne`->`jb` | FUN_111FB370, ebx = lobby+64h, slot [esp+38h]; al after the hook = is host (keep eax) |
| Leave: kick button of the leaving slot | 111FB8EA `mov esi,[ebp-60h]` -> `mov esi,ebx` | FUN_111FB750, ebx = panels[slot] there |
| Kick click: kick the clicked slot | 111FAF72 (5 bytes) | FUN_111FACE0, ebx = lobby+44h, [[ebp]] = clicked component; session vfunc 74h(slot) kicks |

Engine helpers: StrCtor 100F2DC0 (thiscall, ret 4), StrDtor 100F3470, FindChild 110463E0 (cdecl root, &name, recursive),
vector push_back 100E89B0 (ret 4), SetVisible 1105D520 (ret 4). Lobby players vector lobby+A8h count / ACh data,
stride 38h, +0Ch slot, +10h is-host flag. is-host: [[lobby+98h]+8] vfunc 8.

## Layout format Version086 (lib/Layout86.cs)
Component: uid, name, class, x, y, 13 bytes (b, visible, b, u32, b, u32, b), tooltip W, tooltip key W, 13 bytes
(u32, i32, i32, b), default_state, images L(uid, path, w, h, b), 8 bytes, states L, properties L(key, value),
u32 priority, anims L, children L, has_block b [type S "List" + 22 bytes], template S, 6 bytes.
State: uid, name, w, h, text W, tooltip W, 21 bytes, text key W, W, font S, 16 bytes, font category S, 24 bytes,
shader S, 16 bytes, shader S, 16 bytes, state images L, ox, oy, transitions L(type, state uid, u32, u32, targets L(uid, S, u32)).
State image: image uid, x, y, w, h, colour, 29 bytes, shader S, 24 bytes. Anim: name S, b, b, elements L(60 bytes, S, L).
Verified by a byte-exact round trip of ui\frontend ui\mp_grand_campaign (other layouts use parts not decoded yet).

## Other findings
- The Shogun 2 save-screen locale leak is gone in Rome II: FUN_111895E0 only queries setlocale(LC_ALL, NULL).
- MP save names are built for two players (FUN_106C3FC0, "%S (%S) vs. %S (%S)"): cosmetic.
- Co-op faction groups: db campaign_mp_coop_groups(_to_factions); main_rome_coop has 36 factions.

## Bugs found by in-game testing (v1.1)
- lobby+0BCh is a 2-entry string array (slot 0/1 faction keys, "NULL" = none); the lobby assumes entry i = slot i =
  panels[i]. Writers: init loop over panels (11225E77, index esi), player loop (1122631E, by player order), faction
  change 111FBED0 (111FBF8D / 111FBFFB, [slot], no bounds check). With 8 panels they overwrote lobby+0D4h.. (+0DCh = map
  component): no host panel, no map, later crashes. Now: entries written for slots 0/1 only, the player loop writes by slot.
- 11232220 sets up panels[i]'s faction dropdown for i < 2 when entry i != "NULL": crashed (1122F297, null [ctl+5Ch]) on a
  panel nobody had joined. Guarded at 11232322.
- Session slot picker FUN_10F78DF0 took the LAST matching open/free slot: with 8 slots the host got slot 6. Both searches
  now take the first match (host = slot 0, joiners fill in order).
- Leave: SetVisible(null) when the slot's panel has no kick button (panel_player1): null-safe call at 111FB3C3/111FB90F.
- Debugging aids: R2MASK (patch groups) and R2TRACE (lobby pointer + join slots at the start of .r8p, section made writable)
  environment variables for the patcher; crash dumps are in %APPDATA%\The Creative Assembly\Rome2\crash_report.

## v1.2
- Leave handler 111FB750 also wrote entry [slot] of lobby+0BCh unbounded (111FB991, assign ""): a player in slot 2..7
  leaving, or leaving the lobby, corrupted the lobby and crashed on the next screen (crash report 2026-10-22 04:44:
  string ctor on a garbage pointer during a front-end screen change). Bounded at 111FB991.
- Campaign start check FUN_10FC4530 required joined players == settings+48h (2): changed je -> jae at 10FC4624
  (2..8 players can start). 10F8198E is the same test for party matchmaking (not campaigns), left alone.

## v1.3
- Crash handler FUN_100ECC80 calls MiniDumpWriteDump (100ECDA7) with DumpType 41h (WithDataSegs |
  WithIndirectlyReferencedMemory): ~68 MB per crash, which its crash uploader then sends. Changed to 0 (MiniDumpNormal:
  threads, stacks, modules) at 100ECD9F. The crash_report .stack.txt it also writes only says "unknown"; the .mdmp
  stack walk (faulting thread's stack -> empire.retail.dll return addresses) is what locates a crash.

- 3+ humans, players not in a battle: FUN_107508c0 (pending battle; faction) = Shogun 2's FUN_105cefa0. Counts human
  records (vector at [[[pb+28h]+58h]+0EE0h]+80h, +10h count, +14h data, 38h each, flag +0Ch): exactly 2 -> follow the
  human in the battle (FUN_1014c9b0(alliance, army, 0, 1)); otherwise FUN_1014c9b0(0, 0, 1, 0) (plain spectator), the
  path that closed Shogun 2. 107509DC jne -> jb; 107509B0 (play-as-AI votes, humans != 2) -> follow loop 107509FC;
  107509F6 je removed (votes == humans and play-as-AI: dead with 2 humans, reachable with 3+).
- Play-as-AI test inlined 4x (Shogun 2's FUN_105cbb50): pending battle +0ECh vote count, +0F0h votes (20h each,
  choice +0Ch: 1 fight, 4 play as AI): count == 2 && votes[1] == 4 && votes[0] == 1. Sites 1074AF17 (FUN_1074aed0),
  1074DA56 (FUN_1074d040, battle setup: humans == 2 hands the AI army over), 10750997 (FUN_107508c0), 107520C8.
  Left as is: with 3+ humans nobody gets the AI army.
- Side lookup FUN_1074aed0 (pending battle; faction, bool*) -> side or null (FUN_1074ae60 on [pb+48h] then [pb+4Ch],
  then a fallback over [pb+68h]). 44 callers; 5 read [side+4] unchecked: 1074A5A2 / 1074A5EB (FUN_1074a580 = Shogun 2's
  FUN_105b50b0, the H2H crash: AV read 4), 110F0D76 (autoresolve), 1110FB07 / 1110FB7F (spectate / play as AI
  buttons, FUN_1110fab0). All go through a stub: [side+4], or the faction when null.
- Checked, no fix needed: quit battle (EMPIRE_MP::quit_battle, 10FC330F) has no player-count disconnect (Shogun 2's
  10F1F91E); save name FUN_106c3fc0 reads players 0/1 of a growing vector (2 names, no overrun).
- Crash report: the 100ECDA7 MiniDumpWriteDump call goes through a stub (crashing thread, MINIDUMP_EXCEPTION_INFORMATION
  arg 5) that writes text to eightplayer_crash.txt (working folder = game folder) and passes it as a CommentStreamA
  (type 10) user stream. Call stack = every stack dword in .text or .r8p, rebased to image base 10000000; stack read
  only when ctx.Esp is inside [fs:8, fs:4). Tested outside the game: lib loaded with DONT_RESOLVE_DLL_REFERENCES,
  four IAT slots pointed at the real functions, fake access violation -> 477 KB dump with the comment stream.

## Not done / next
- In-campaign behaviour with 3+ humans is untested: co-op alliances between all humans, battle results for
  allies who weren't in the battle, desync handling (handler near 10FD5700 / 10FB5890, kick setting mp_desync_kick_enabled).
