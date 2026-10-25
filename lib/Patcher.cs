// Builds a patched copy of Rome II's empire.retail.dll for the 8-player MP campaign mod.
// Never modifies its input: it checks the input is exactly the expected build and writes a new file.
//
// Patches (all new code lives in a new section ".r8p" appended after .reloc; it uses only relative calls/jumps
// and position-independent data, so nothing needs relocating under ASLR):
//   1. Session capacity: the host-campaign screen builds its session settings with 2 players -> 8.
//   2. Lobby panel binding: after "panel_player1"/"panel_player2", also bind panel_player3..8 (skipped if absent).
//   3. Listeners: the lobby only listened to the FIRST button_kick / button_invite; also listen to the kick button
//      in panel_player3..8 and the invite button in invite_frame3..8 (shown to the host only).
//   4. Player joins: the kick button looked up from the lobby root (always panel 2's) and was shown only for slot 1;
//      now the joining slot's own panel's kick button is used and shown to the host for every slot but the host's.
//   5. Player leaves: hides the leaving slot's own kick button instead of panel 2's.
//   6. Kick click: kicked the first non-host player whatever button was pressed; now kicks the player whose panel
//      holds the clicked button.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

public static class EightPlayerPatcher {
	public const string OriginalSha256 = "07CB2215B80591224A5F8889963344D560492F09C4D22696C988B957CA49B2B7";
	const uint ImageBase = 0x10000000;
	public const int MaxPlayers = 8;

	// engine functions (absolute VAs in the original image)
	const uint StrCtor = 0x100F2DC0;      // thiscall(string; const char*) ret 4
	const uint StrDtor = 0x100F3470;      // thiscall(string)
	const uint FindChild = 0x110463E0;    // cdecl(component root, string* name, bool recursive) -> component
	const uint PushBack = 0x100E89B0;     // thiscall(vector; T*) ret 4
	const uint AddListener = 0x10CD2990;  // thiscall(handler ? handler+2Ch : 0; listener) ret 4
	const uint SetVisible = 0x1105D520;   // thiscall(component; bool) ret 4

	// 1. capacity
	const uint CapacityPushes = 0x111EFED3;   // push 2 / push 0 / push 2 (sides, spectators, players) -> call 10F6D870

	// 2. panel binding (lobby init FUN_11225C80; edi = lobby, [edi+4] = root, panels vector at edi+0B0h)
	const uint BindHookAt = 0x11225E4A;       // call StrDtor (end of the panel_player2 lookup)
	const uint BindResume = 0x11225E4F;

	// 3. listeners (FUN_111FA890; ebx = lobby, edi = lobby+44h = listener)
	const uint ListenHookAt = 0x111FA976;     // mov esi,[ebx+4] / lea ecx,[esp+1Ch]  (7 bytes)
	const uint ListenResume = 0x111FA97D;

	// 4. join (FUN_111FB370; ebx = lobby+64h, [ebx-60h] = root, panels count [ebx+50h] data [ebx+54h], slot [esp+38h])
	const uint JoinHookAt = 0x111FB385;       // mov esi,[ebx-60h] / lea ecx,[esp+1Ch]  (7 bytes)
	const uint JoinResume = 0x111FB38C;
	const uint JoinSlotTest = 0x111FB3B5;     // cmp ebp,1 / jne hide -> jb hide

	// 5. leave (FUN_111FB750; ebx = the leaving slot's panel)
	const uint LeaveKickRoot = 0x111FB8EA;    // mov esi,[ebp-60h] -> mov esi,ebx

	// 6. kick click (FUN_111FACE0; ebx = lobby+44h, [[ebp]] = clicked component, eax = session (checked non-null))
	const uint KickHookAt = 0x111FAF72;       // mov ecx,[ebx+64h] / xor edi,edi  (5 bytes)
	const uint KickResume = 0x111FAF77;
	const uint KickDone = 0x111FAFC0;

	static byte[] b; static int peOff, secTable, nSec;
	// test switch: R2MASK env var selects patch groups (1 capacity, 2 bind, 4 listeners, 8 join, 16 leave, 32 kick, 64 invite mirror)
	static bool Trace { get { return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("R2TRACE")); } }
	static bool On(int bit) { var m = Environment.GetEnvironmentVariable("R2MASK"); return string.IsNullOrEmpty(m) || (Convert.ToInt32(m) & bit) != 0; }

	static int VaToOff(uint va) {
		uint rva = va - ImageBase;
		for (int i = 0; i < nSec; i++) {
			int s = secTable + i * 40;
			uint vaS = BitConverter.ToUInt32(b, s + 12), raw = BitConverter.ToUInt32(b, s + 16), rawOff = BitConverter.ToUInt32(b, s + 20);
			if (rva >= vaS && rva < vaS + raw) return (int)(rawOff + (rva - vaS));
		}
		throw new Exception(string.Format("VA {0:X8} not in any section's file data", va));
	}

	static string Sha(byte[] d) { using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(d)).Replace("-", ""); }

	class Asm {
		public uint Va; public List<byte> Bytes = new List<byte>();
		public uint Here { get { return Va + (uint)Bytes.Count; } }
		public void Raw(params byte[] x) { Bytes.AddRange(x); }
		public void Call(uint target) { Raw(0xE8); Rel32(target); }
		public void Jmp(uint target) { Raw(0xE9); Rel32(target); }
		void Rel32(uint target) { Bytes.AddRange(BitConverter.GetBytes((int)(target - (Here + 4)))); }

		class Fix { public int Idx; public uint Next; public string Label; public bool Short; public bool Abs; public uint Base; }
		readonly Dictionary<string, uint> labels = new Dictionary<string, uint>();
		readonly List<Fix> fixes = new List<Fix>();
		public void Mark(string label) { labels[label] = Here; }
		public void J8(byte opcode, string label) { Raw(opcode, 0); fixes.Add(new Fix { Idx = Bytes.Count - 1, Next = Here, Label = label, Short = true }); }
		public void J32(string label, params byte[] opcode) { Raw(opcode); Raw(0, 0, 0, 0); fixes.Add(new Fix { Idx = Bytes.Count - 4, Next = Here, Label = label }); }
		// "call $+5 / pop reg / add reg, label - popAt": reg = runtime address of label (no relocation needed)
		public void LoadAddr(byte popOpcode, byte addModrm, string label) {
			Raw(0xE8, 0, 0, 0, 0); uint popAt = Here; Raw(popOpcode);
			Raw(0x81, addModrm, 0, 0, 0, 0);
			fixes.Add(new Fix { Idx = Bytes.Count - 4, Label = label, Abs = true, Base = popAt });
		}
		public void Str(string label, string s) { Mark(label); Bytes.AddRange(Encoding.ASCII.GetBytes(s)); Raw(0); }
		public void Resolve() {
			foreach (var f in fixes) {
				int d = f.Abs ? (int)(labels[f.Label] - f.Base) : (int)labels[f.Label] - (int)f.Next;
				if (f.Short) {
					if (d < -128 || d > 127) throw new Exception("short jump to " + f.Label + " out of range");
					Bytes[f.Idx] = unchecked((byte)(sbyte)d);
				}
				else { var v = BitConverter.GetBytes(d); for (int i = 0; i < 4; i++) Bytes[f.Idx + i] = v[i]; }
			}
			fixes.Clear();
		}
	}

	// temp string at [esp] (10h bytes reserved): construct from eax-pointed name, FindChild(parent, &temp, 1) -> esi
	// parentPush: bytes of a "push <parent>" instruction valid after the two pushes FindChild's caller makes
	static void EmitFindChild(Asm a, byte[] pushParentAfter8) {
		a.Raw(0x50);                                      // push eax              (name)
		a.Raw(0x8D, 0x4C, 0x24, 0x04);                    // lea ecx,[esp+4]
		a.Call(StrCtor);
		a.Raw(0x6A, 0x01);                                // push 1
		a.Raw(0x8D, 0x44, 0x24, 0x04);                    // lea eax,[esp+4]
		a.Raw(0x50);                                      // push eax              (&temp)
		a.Raw(pushParentAfter8);                          // push parent
		a.Call(FindChild);
		a.Raw(0x83, 0xC4, 0x0C);                          // add esp,0Ch
		a.Raw(0x8B, 0xF0);                                // mov esi,eax
		a.Raw(0x8B, 0xCC);                                // mov ecx,esp
		a.Call(StrDtor);
	}

	// ---- 2. bind panel_player3..8 into the lobby's panel vector ---------------------------------------------------
	static uint EmitPanelBinding(Asm a) {
		uint at = a.Here;
		a.Call(StrDtor);                                  // replaced call (ecx = the panel_player2 temp string)
		a.Raw(0x60);                                      // pushad
		if (Trace) { a.LoadAddr(0x58, 0xC0, "trace"); a.Raw(0x89, 0x38); }   // debug: trace[0] = lobby
		a.LoadAddr(0x5B, 0xC3, "panels");                 // ebx = &"panel_player3"
		a.Mark("loop");
		a.Raw(0x83, 0xEC, 0x10);                          // sub esp,10h           (temp string)
		a.Raw(0x8B, 0xC3);                                // mov eax,ebx
		EmitFindChild(a, new byte[] { 0xFF, 0x77, 0x04 }); // push dword ptr [edi+4] (root)
		a.Raw(0x85, 0xF6);                                // test esi,esi
		a.J8(0x74, "skip");                               // jz skip               (panel missing: don't add it)
		a.Raw(0x56);                                      // push esi              (the element)
		a.Raw(0x54);                                      // push esp              (&element)
		a.Raw(0x8D, 0x8F, 0xB0, 0x00, 0x00, 0x00);        // lea ecx,[edi+0B0h]    (panel vector)
		a.Call(PushBack);
		a.Raw(0x83, 0xC4, 0x04);                          // add esp,4
		a.Mark("skip");
		a.Raw(0x83, 0xC4, 0x10);                          // add esp,10h
		a.Raw(0x83, 0xC3, 0x0E);                          // add ebx,14            (next name: 13 chars + NUL)
		a.Raw(0x80, 0x3B, 0x00);                          // cmp byte ptr [ebx],0
		a.J8(0x75, "loop");                               // jnz loop
		a.Raw(0x61);                                      // popad
		a.Jmp(BindResume);
		a.Mark("panels");
		for (int n = 3; n <= MaxPlayers; n++) a.Bytes.AddRange(Encoding.ASCII.GetBytes("panel_player" + n + "\0"));
		a.Raw(0);
		a.Resolve();
		return at;
	}

	// ---- 3. listen to every slot's kick and invite buttons ---------------------------------------------------------
	static uint EmitListeners(Asm a) {
		uint at = a.Here;
		a.Raw(0x60);                                      // pushad
		a.Raw(0x33, 0xED);                                // xor ebp,ebp           (record 0..11)
		a.Mark("loop");
		a.Raw(0x83, 0xEC, 0x10);                          // sub esp,10h
		a.LoadAddr(0x58, 0xC0, "frames");                 // eax = &records
		a.Raw(0x6B, 0xCD, 0x0E);                          // imul ecx,ebp,14
		a.Raw(0x03, 0xC1);                                // add eax,ecx           (frame name)
		EmitFindChild(a, new byte[] { 0xFF, 0x73, 0x04 }); // push dword ptr [ebx+4] (root)
		a.Raw(0x85, 0xF6);                                // test esi,esi
		a.J32("next", 0x0F, 0x84);                        // jz next
		a.Raw(0x89, 0x74, 0x24, 0x0C);                    // mov [esp+0Ch],esi     (frame, in the temp's spare dword)
		a.Raw(0x83, 0xFD, 0x06);                          // cmp ebp,6
		a.J8(0x73, "inv");                                // jae inv
		a.LoadAddr(0x58, 0xC0, "kick");                   // eax = &"button_kick"
		a.J8(0xEB, "find");
		a.Mark("inv");
		a.LoadAddr(0x58, 0xC0, "invite");                 // eax = &"button_invite"
		a.Mark("find");
		EmitFindChild(a, new byte[] { 0xFF, 0x74, 0x24, 0x14 }); // push dword ptr [esp+14h] (frame)
		a.Raw(0x85, 0xF6);                                // test esi,esi
		a.J8(0x74, "next");                               // jz next
		a.Raw(0x8B, 0x8E, 0x74, 0x01, 0x00, 0x00);        // mov ecx,[esi+174h]    (button handler, as the original)
		a.Raw(0x8D, 0x41, 0x2C);                          // lea eax,[ecx+2Ch]
		a.Raw(0xF7, 0xD9);                                // neg ecx
		a.Raw(0x1B, 0xC9);                                // sbb ecx,ecx
		a.Raw(0x23, 0xC8);                                // and ecx,eax
		a.Raw(0x57);                                      // push edi              (lobby listener)
		a.Call(AddListener);
		a.Raw(0x83, 0xFD, 0x06);                          // cmp ebp,6
		a.J8(0x72, "next");                               // jb next               (kick buttons: the join code shows them)
		a.Raw(0x8B, 0x8B, 0x98, 0x00, 0x00, 0x00);        // mov ecx,[ebx+98h]     (invite: visible to the host only)
		a.Raw(0x83, 0xC1, 0x08);                          // add ecx,8
		a.Raw(0x8B, 0x01);                                // mov eax,[ecx]
		a.Raw(0xFF, 0x50, 0x08);                          // call [eax+8]          (is host)
		a.Raw(0x0F, 0xB6, 0xC0);                          // movzx eax,al
		a.Raw(0x50);                                      // push eax
		a.Raw(0x8B, 0xCE);                                // mov ecx,esi
		a.Call(SetVisible);
		a.Mark("next");
		a.Raw(0x83, 0xC4, 0x10);                          // add esp,10h
		a.Raw(0x45);                                      // inc ebp
		a.Raw(0x83, 0xFD, 0x0C);                          // cmp ebp,12
		a.J32("loop", 0x0F, 0x82);                        // jb loop
		a.Raw(0x61);                                      // popad
		a.Raw(0x8B, 0x73, 0x04);                          // mov esi,[ebx+4]       (replaced instructions)
		a.Raw(0x8D, 0x4C, 0x24, 0x1C);                    // lea ecx,[esp+1Ch]
		a.Jmp(ListenResume);
		a.Mark("frames");
		for (int n = 3; n <= MaxPlayers; n++) a.Bytes.AddRange(Encoding.ASCII.GetBytes("panel_player" + n + "\0"));
		for (int n = 3; n <= MaxPlayers; n++) a.Bytes.AddRange(Encoding.ASCII.GetBytes("invite_frame" + n + "\0"));
		a.Str("kick", "button_kick");
		a.Str("invite", "button_invite");
		a.Resolve();
		return at;
	}

	// ---- 4. join: use the joining slot's own panel to find the kick button ----------------------------------------
	static uint EmitJoinKickRoot(Asm a) {
		uint at = a.Here;
		if (Trace) {                                      // debug: trace[1] = join count, trace[2..] = slots
			a.Raw(0x50, 0x51); a.LoadAddr(0x58, 0xC0, "trace");
			a.Raw(0x8B, 0x48, 0x04, 0x83, 0xE1, 0x0F, 0x52, 0x8B, 0x54, 0x24, 0x44, 0x89, 0x54, 0x88, 0x08, 0x5A, 0xFF, 0x40, 0x04, 0x59, 0x58);
		}
		// al holds the is-host flag the original code stores right after this: keep eax intact
		a.Raw(0x8B, 0x73, 0xA0);                          // mov esi,[ebx-60h]     (root, as before)
		a.Raw(0x8B, 0x54, 0x24, 0x38);                    // mov edx,[esp+38h]     (slot)
		a.Raw(0x85, 0xD2);                                // test edx,edx
		a.J8(0x74, "keep");                               // jz keep               (the host's own slot)
		a.Raw(0x3B, 0x53, 0x50);                          // cmp edx,[ebx+50h]     (panel count)
		a.J8(0x73, "keep");                               // jae keep
		a.Raw(0x8B, 0x4B, 0x54);                          // mov ecx,[ebx+54h]
		a.Raw(0x8B, 0x0C, 0x91);                          // mov ecx,[ecx+edx*4]
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "keep");                               // jz keep
		a.Raw(0x8B, 0xF1);                                // mov esi,ecx           (search inside that panel)
		a.Mark("keep");
		a.Raw(0x8D, 0x4C, 0x24, 0x1C);                    // lea ecx,[esp+1Ch]     (replaced instruction)
		a.Jmp(JoinResume);
		a.Resolve();
		return at;
	}

	// ---- 7. invite buttons on slots 3..8 follow the first one ------------------------------------------------------
	// Join and leave set the first invite button's visibility (host, lobby not full); at lobby start the host isn't
	// known yet, so the copies must follow these calls. Replaces "call SetVisible" there (ecx = first invite button).
	const uint JoinInviteVisCall = 0x111FB41D;   // ebx = lobby+64h -> root [ebx-60h]
	const uint LeaveInviteVisCall = 0x111FB8E5;  // ebp = lobby+64h -> root [ebp-60h]

	static void EmitInviteMirror(Asm a, out uint joinAt, out uint leaveAt) {
		joinAt = a.Here;
		a.Raw(0x8B, 0x43, 0xA0);                          // mov eax,[ebx-60h]     (root)
		a.J8(0xEB, "common");
		leaveAt = a.Here;
		a.Raw(0x8B, 0x45, 0xA0);                          // mov eax,[ebp-60h]     (root)
		a.Mark("common");
		a.Raw(0x60);                                      // pushad                (root saved at [esp+1Ch], visible at [esp+24h])
		a.Raw(0xFF, 0x74, 0x24, 0x24);                    // push dword ptr [esp+24h]
		a.Call(SetVisible);                               // the original call (ecx = first invite button)
		a.Raw(0x33, 0xED);                                // xor ebp,ebp
		a.Mark("loop");
		a.Raw(0x83, 0xEC, 0x10);                          // sub esp,10h
		a.LoadAddr(0x58, 0xC0, "frames");                 // eax = &"invite_frame3"
		a.Raw(0x6B, 0xCD, 0x0E);                          // imul ecx,ebp,14
		a.Raw(0x03, 0xC1);                                // add eax,ecx
		EmitFindChild(a, new byte[] { 0xFF, 0x74, 0x24, 0x34 }); // push dword ptr [esp+34h] (root)
		a.Raw(0x85, 0xF6);                                // test esi,esi
		a.J8(0x74, "next");
		a.Raw(0x89, 0x74, 0x24, 0x0C);                    // mov [esp+0Ch],esi     (frame)
		a.LoadAddr(0x58, 0xC0, "invite");                 // eax = &"button_invite"
		EmitFindChild(a, new byte[] { 0xFF, 0x74, 0x24, 0x14 }); // push dword ptr [esp+14h] (frame)
		a.Raw(0x85, 0xF6);                                // test esi,esi
		a.J8(0x74, "next");
		a.Raw(0xFF, 0x74, 0x24, 0x34);                    // push dword ptr [esp+34h] (visible)
		a.Raw(0x8B, 0xCE);                                // mov ecx,esi
		a.Call(SetVisible);
		a.Mark("next");
		a.Raw(0x83, 0xC4, 0x10);                          // add esp,10h
		a.Raw(0x45);                                      // inc ebp
		a.Raw(0x83, 0xFD, 0x06);                          // cmp ebp,6
		a.J32("loop", 0x0F, 0x82);                        // jb loop
		a.Raw(0x61);                                      // popad
		a.Raw(0xC2, 0x04, 0x00);                          // ret 4
		a.Mark("frames");
		for (int n = 3; n <= MaxPlayers; n++) a.Bytes.AddRange(Encoding.ASCII.GetBytes("invite_frame" + n + "\0"));
		a.Str("invite", "button_invite");
		a.Resolve();
	}

	// ---- 8. null-safe SetVisible for the per-slot kick lookups ------------------------------------------------------
	// Join/leave call SetVisible on the kick button without a null check (the root always had one). A slot's panel may
	// have none (panel_player1, the host's), and leave runs for slot 0 while the lobby is set up: SetVisible(null)
	// threw and aborted the lobby setup (no host panel, no map).
	const uint JoinKickVisCall = 0x111FB3C3;
	const uint LeaveKickVisCall = 0x111FB90F;

	static uint EmitSafeSetVisible(Asm a) {
		uint at = a.Here;
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "none");                               // jz none
		a.Jmp(SetVisible);                                // tail call (same arguments)
		a.Mark("none");
		a.Raw(0xC2, 0x04, 0x00);                          // ret 4
		a.Resolve();
		return at;
	}

	// ---- 9. the lobby's 2-entry slot faction array ------------------------------------------------------------------
	// lobby+0BCh holds exactly two strings (slot 0/1 faction keys, "NULL" when empty; FUN_11211BE0 reads 2 of them to
	// colour the lobby map). Two loops write one entry per panel / per player: with 8 panels they wrote 6 strings past
	// the end, over the lobby's map component pointer (+0DCh) and flags, so the map and the host's panel never appeared
	// and the game later crashed on the garbage pointer. Only entries 0 and 1 are written now.
	const uint StrAssignCStr = 0x100F4000;   // thiscall(string; const char*) ret 4
	const uint StrAssign = 0x100F3F60;       // thiscall(string; const string*) ret 4
	const uint PanelLoopAssign = 0x11225E77; // init loop over panels, index esi
	const uint PlayerLoopAssign = 0x1122631E; // loop over players, index ebx

	static uint EmitAssignIfFirstTwo(Asm a, byte cmpModrm, uint assign) {
		uint at = a.Here;
		a.Raw(0x83, cmpModrm, 0x02);                      // cmp esi/ebx,2
		a.J8(0x73, "skip");                               // jae skip
		a.Jmp(assign);                                    // tail call (same arguments)
		a.Mark("skip");
		a.Raw(0xC2, 0x04, 0x00);                          // ret 4
		a.Resolve();
		return at;
	}

	// ---- 10. slot-indexed faction array, everywhere ------------------------------------------------------------------
	// The lobby treats entry i of lobby+0BCh as slot i (11232220 sets up panels[i]'s faction dropdown from it; the faction
	// change handler 111FBED0 writes entry [slot] without a bounds check). Writes now go to entry [slot] for slots 0/1 only,
	// and the dropdown set-up skips panels nobody has joined (their controller has no faction set: [ctl+5Ch] = 0).
	const uint FactionChangeAssign = 0x111FBF8D;  // assign(string*) to entry [ebx = slot]
	const uint FactionClearAssign = 0x111FBFFB;   // assign("NULL") to entry [ebx = slot]
	const uint LeaveClearAssign = 0x111FB991;     // leave handler: assign("") to entry [slot], slot at [esp+34h] before the push
	const uint DropdownSetupCall = 0x11232322;    // call FUN_11230DD0 (ecx = panels[i] controller), ret 10h
	const uint DropdownSetup = 0x11230DD0;

	// players loop (edi = lobby): the pushed argument is player entry + 14h; the entry's slot is at +0Ch (= arg - 8)
	static uint EmitAssignBySlot(Asm a) {
		uint at = a.Here;
		a.Raw(0x8B, 0x44, 0x24, 0x04);                    // mov eax,[esp+4]
		a.Raw(0x8B, 0x50, 0xF8);                          // mov edx,[eax-8]       (slot)
		a.Raw(0x83, 0xFA, 0x02);                          // cmp edx,2
		a.J8(0x73, "skip");                               // jae skip
		a.Raw(0x8D, 0x14, 0x52);                          // lea edx,[edx+edx*2]
		a.Raw(0x8D, 0x8C, 0x97, 0xBC, 0x00, 0x00, 0x00);  // lea ecx,[edi+edx*4+0BCh]
		a.Jmp(StrAssign);
		a.Mark("skip");
		a.Raw(0xC2, 0x04, 0x00);                          // ret 4
		a.Resolve();
		return at;
	}

	// leave handler: the slot is a stack variable there ([esp+3Ch] inside this stub: 34h + pushed argument + return address)
	static uint EmitLeaveClearGuard(Asm a) {
		uint at = a.Here;
		a.Raw(0x83, 0x7C, 0x24, 0x3C, 0x02);              // cmp dword ptr [esp+3Ch],2
		a.J8(0x73, "skip");                               // jae skip
		a.Jmp(StrAssignCStr);
		a.Mark("skip");
		a.Raw(0xC2, 0x04, 0x00);                          // ret 4
		a.Resolve();
		return at;
	}

	static uint EmitDropdownGuard(Asm a) {
		uint at = a.Here;
		a.Raw(0x85, 0xC9);                                // test ecx,ecx
		a.J8(0x74, "skip");
		a.Raw(0x83, 0x79, 0x5C, 0x00);                    // cmp dword ptr [ecx+5Ch],0
		a.J8(0x74, "skip");
		a.Jmp(DropdownSetup);
		a.Mark("skip");
		a.Raw(0xC2, 0x10, 0x00);                          // ret 10h
		a.Resolve();
		return at;
	}

	// ---- 11. players fill the session's slots in order -------------------------------------------------------------
	// FUN_10F78DF0 gives a joining player the LAST matching open slot. With 8 slots over 2 sides the host got slot 6, and
	// the lobby (host = slot 0) and its faction logic broke. Both searches now take the first match.
	const uint PickPredefinedJump = 0x10F78E5C;   // jmp 10F78E62 (keep looking) -> jmp 10F78E71 (done)
	const uint PickFreeLoop = 0x10F78EE0;         // 30-byte loop body ending at 10F78EFE

	// ---- 12. the campaign starts with 2..8 players ------------------------------------------------------------------
	// FUN_10FC4530 ("all players ready") requires the joined player count to EQUAL the session's required count
	// (settings +48h, set to 2 by the host-campaign screen): with 3+ players the campaign could never start.
	// Now the count only has to reach it: cmp eax,[ebx+48h] / je ok -> jae ok.
	const uint StartPlayerCountTest = 0x10FC4621;

	static void PatchStartPlayerCount() {
		ExpectBytes(StartPlayerCountTest, 0x3B, 0x43, 0x48, 0x74, 0x17);
		b[VaToOff(StartPlayerCountTest) + 3] = 0x73;
	}

	static void PatchSlotPicker() {
		ExpectBytes(PickPredefinedJump, 0xEB, 0x04);
		b[VaToOff(PickPredefinedJump) + 1] = 0x13;
		ExpectBytes(PickFreeLoop, 0x69, 0xC1, 0xC8, 0x00, 0x00, 0x00, 0x8B, 0x84, 0x28, 0xC0, 0x00, 0x00, 0x00, 0xC1, 0xE8, 0x06,
			0xA8, 0x01, 0x8B, 0xC1, 0x0F, 0x45, 0xC3, 0x41, 0x8B, 0xD8, 0x3B, 0xCA, 0x72, 0xE2);
		var loop = new byte[] {
			0x69, 0xC1, 0xC8, 0x00, 0x00, 0x00,           // imul eax,ecx,0C8h
			0x8B, 0x84, 0x28, 0xC0, 0x00, 0x00, 0x00,     // mov eax,[eax+ebp+0C0h]
			0xA8, 0x40,                                   // test al,40h          (occupied)
			0x75, 0x04,                                   // jnz next
			0x8B, 0xD9,                                   // mov ebx,ecx          (first free slot)
			0xEB, 0x09,                                   // jmp 10F78EFE
			0x41,                                         // next: inc ecx
			0x3B, 0xCA,                                   // cmp ecx,edx
			0x72, 0xE6,                                   // jb loop
			0x90, 0x90, 0x90, 0x90 };
		Array.Copy(loop, 0, b, VaToOff(PickFreeLoop), loop.Length);
	}

	static void RedirectCall(uint at, uint expected, uint target) {
		int o = VaToOff(at);
		if (b[o] != 0xE8 || (uint)(at + 5 + BitConverter.ToInt32(b, o + 1)) != expected)
			throw new Exception(string.Format("unexpected call at {0:X8}", at));
		Array.Copy(BitConverter.GetBytes((int)(target - (at + 5))), 0, b, o + 1, 4);
	}

	// ---- 6. kick click: kick the slot whose panel holds the clicked button ----------------------------------------
	static uint EmitKickClick(Asm a) {
		uint at = a.Here;
		a.Raw(0x50);                                      // push eax              (session)
		a.Raw(0xBF, 0x01, 0x00, 0x00, 0x00);              // mov edi,1             (slot 0 = host, never kicked)
		a.Mark("loop");
		a.Raw(0x3B, 0x7B, 0x70);                          // cmp edi,[ebx+70h]     (panel count)
		a.J8(0x73, "notfound");                           // jae notfound
		a.Raw(0x8B, 0x43, 0x74);                          // mov eax,[ebx+74h]
		a.Raw(0x8B, 0x04, 0xB8);                          // mov eax,[eax+edi*4]
		a.Raw(0x85, 0xC0);                                // test eax,eax
		a.J8(0x74, "next");                               // jz next
		a.Raw(0x83, 0xEC, 0x10);                          // sub esp,10h
		a.Raw(0x89, 0x44, 0x24, 0x0C);                    // mov [esp+0Ch],eax     (panel)
		a.LoadAddr(0x58, 0xC0, "kick");                   // eax = &"button_kick"
		EmitFindChild(a, new byte[] { 0xFF, 0x74, 0x24, 0x14 }); // push dword ptr [esp+14h] (panel)
		a.Raw(0x83, 0xC4, 0x10);                          // add esp,10h
		a.Raw(0x8B, 0x45, 0x00);                          // mov eax,[ebp]         (clicked component)
		a.Raw(0x3B, 0xF0);                                // cmp esi,eax
		a.J8(0x74, "found");                              // je found
		a.Mark("next");
		a.Raw(0x47);                                      // inc edi
		a.J8(0xEB, "loop");
		a.Mark("found");
		a.Raw(0x59);                                      // pop ecx               (session)
		a.Raw(0x8B, 0x01);                                // mov eax,[ecx]
		a.Raw(0x57);                                      // push edi              (slot)
		a.Raw(0xFF, 0x50, 0x74);                          // call [eax+74h]        (kick, as the original)
		a.Jmp(KickDone);
		a.Mark("notfound");
		a.Raw(0x58);                                      // pop eax
		a.Raw(0x8B, 0x4B, 0x64);                          // mov ecx,[ebx+64h]     (replaced instructions: original search)
		a.Raw(0x33, 0xFF);                                // xor edi,edi
		a.Jmp(KickResume);
		a.Str("kick", "button_kick");
		a.Resolve();
		return at;
	}

	static void ExpectBytes(uint va, params byte[] expected) {
		int o = VaToOff(va);
		for (int i = 0; i < expected.Length; i++)
			if (b[o + i] != expected[i]) throw new Exception(string.Format("unexpected bytes at {0:X8}", va));
	}

	static void PutJmp(uint at, uint target, int patchLen) {
		int o = VaToOff(at);
		b[o] = 0xE9;
		Array.Copy(BitConverter.GetBytes((int)(target - (at + 5))), 0, b, o + 1, 4);
		for (int i = 5; i < patchLen; i++) b[o + i] = 0x90;
	}

	static void AddSection(string name, List<byte> code) {
		int opt = peOff + 24;
		uint fileAlign = BitConverter.ToUInt32(b, opt + 36), sectAlign = BitConverter.ToUInt32(b, opt + 32);
		uint sizeOfImage = BitConverter.ToUInt32(b, opt + 56), sizeOfHeaders = BitConverter.ToUInt32(b, opt + 60);
		int hdr = secTable + nSec * 40;
		if (hdr + 40 > sizeOfHeaders) throw new Exception("no room for another section header");
		for (int i = 0; i < 40; i++) if (b[hdr + i] != 0) throw new Exception("section header slot not empty");
		uint rawPtr = (uint)b.Length;
		if (rawPtr % fileAlign != 0) throw new Exception("file end is not aligned");
		uint rawSize = ((uint)code.Count + fileAlign - 1) / fileAlign * fileAlign;
		var nb = new byte[b.Length + rawSize];
		Array.Copy(b, nb, b.Length);
		code.CopyTo(nb, (int)rawPtr);
		b = nb;
		Array.Copy(Encoding.ASCII.GetBytes(name.PadRight(8, '\0')), 0, b, hdr, 8);
		Array.Copy(BitConverter.GetBytes((uint)code.Count), 0, b, hdr + 8, 4);   // VirtualSize
		Array.Copy(BitConverter.GetBytes(sizeOfImage), 0, b, hdr + 12, 4);       // VirtualAddress
		Array.Copy(BitConverter.GetBytes(rawSize), 0, b, hdr + 16, 4);
		Array.Copy(BitConverter.GetBytes(rawPtr), 0, b, hdr + 20, 4);
		Array.Copy(BitConverter.GetBytes(Trace ? 0xE0000020u : 0x60000020u), 0, b, hdr + 36, 4);       // code | execute | read (| write when tracing)
		nSec++;
		Array.Copy(BitConverter.GetBytes((ushort)nSec), 0, b, peOff + 6, 2);
		uint newImage = sizeOfImage + ((uint)code.Count + sectAlign - 1) / sectAlign * sectAlign;
		Array.Copy(BitConverter.GetBytes(newImage), 0, b, opt + 56, 4);
	}

	public static string Build(string inPath, string outPath) { return Build(inPath, outPath, true); }

	// capacityFix = false: everything but the 8-player session (for testing)
	public static string Build(string inPath, string outPath, bool capacityFix) {
		b = File.ReadAllBytes(inPath);
		string inHash = Sha(b);
		if (inHash != OriginalSha256) throw new Exception("input is not the expected empire.retail.dll build (sha256 " + inHash + ")");
		peOff = BitConverter.ToInt32(b, 0x3C);
		nSec = BitConverter.ToUInt16(b, peOff + 6);
		secTable = peOff + 24 + BitConverter.ToUInt16(b, peOff + 20);

		// 1. capacity: push 2 (players) -> push 8
		ExpectBytes(CapacityPushes, 0x6A, 0x02, 0x6A, 0x00, 0x6A, 0x02, 0x8D, 0x8C, 0x24);
		if (capacityFix && On(1)) b[VaToOff(CapacityPushes + 5)] = (byte)MaxPlayers;

		// 4b. join: show the kick button for every non-host slot (cmp ebp,1 / jne -> jb)
		ExpectBytes(JoinSlotTest, 0x83, 0xFD, 0x01, 0x75, 0x04);
		if (On(8)) b[VaToOff(JoinSlotTest) + 3] = 0x72;
		// 5. leave: look the kick button up in the leaving slot's panel (ebx)
		ExpectBytes(LeaveKickRoot, 0x8B, 0x75, 0xA0, 0x8D, 0x4C, 0x24, 0x18);
		if (On(16)) { b[VaToOff(LeaveKickRoot) + 1] = 0xF3; b[VaToOff(LeaveKickRoot) + 2] = 0x90; }

		uint sizeOfImage = BitConverter.ToUInt32(b, peOff + 24 + 56);
		var s = new Asm { Va = ImageBase + sizeOfImage };
		if (Trace) { s.Mark("trace"); s.Bytes.AddRange(new byte[0x50]); }
		uint bindAt = EmitPanelBinding(s);
		uint listenAt = EmitListeners(s);
		uint joinAt = EmitJoinKickRoot(s);
		uint kickAt = EmitKickClick(s);
		uint mirrorJoin, mirrorLeave;
		EmitInviteMirror(s, out mirrorJoin, out mirrorLeave);
		if (On(64)) RedirectCall(JoinInviteVisCall, SetVisible, mirrorJoin);
		if (On(64)) RedirectCall(LeaveInviteVisCall, SetVisible, mirrorLeave);
		uint assign1 = EmitAssignIfFirstTwo(s, 0xFE, StrAssignCStr);   // cmp esi,2
		RedirectCall(PanelLoopAssign, StrAssignCStr, assign1);
		uint assign2 = EmitAssignBySlot(s);
		RedirectCall(PlayerLoopAssign, StrAssign, assign2);
		uint assign3 = EmitAssignIfFirstTwo(s, 0xFB, StrAssign);       // cmp ebx,2
		RedirectCall(FactionChangeAssign, StrAssign, assign3);
		uint assign4 = EmitAssignIfFirstTwo(s, 0xFB, StrAssignCStr);   // cmp ebx,2
		RedirectCall(FactionClearAssign, StrAssignCStr, assign4);
		RedirectCall(DropdownSetupCall, DropdownSetup, EmitDropdownGuard(s));
		RedirectCall(LeaveClearAssign, StrAssignCStr, EmitLeaveClearGuard(s));
		if (On(1)) { PatchSlotPicker(); PatchStartPlayerCount(); }
		uint safeVis = EmitSafeSetVisible(s);
		if (On(8)) RedirectCall(JoinKickVisCall, SetVisible, safeVis);
		if (On(16)) RedirectCall(LeaveKickVisCall, SetVisible, safeVis);

		int o = VaToOff(BindHookAt);
		if (b[o] != 0xE8 || (uint)(BindHookAt + 5 + BitConverter.ToInt32(b, o + 1)) != StrDtor) throw new Exception("unexpected bytes at bind hook");
		if (On(2)) PutJmp(BindHookAt, bindAt, 5);
		ExpectBytes(ListenHookAt, 0x8B, 0x73, 0x04, 0x8D, 0x4C, 0x24, 0x1C);
		if (On(4)) PutJmp(ListenHookAt, listenAt, 7);
		ExpectBytes(JoinHookAt, 0x8B, 0x73, 0xA0, 0x8D, 0x4C, 0x24, 0x1C);
		if (On(8)) PutJmp(JoinHookAt, joinAt, 7);
		ExpectBytes(KickHookAt, 0x8B, 0x4B, 0x64, 0x33, 0xFF);
		if (On(32)) PutJmp(KickHookAt, kickAt, 5);

		AddSection(".r8p", s.Bytes);
		File.WriteAllBytes(outPath, b);
		return string.Format("patched: .r8p at {0:X8} ({1} bytes); bind {2:X8}, listeners {3:X8}, join {4:X8}, kick {5:X8}; output sha256 {6}",
			s.Va, s.Bytes.Count, bindAt, listenAt, joinAt, kickAt, Sha(b));
	}
}
