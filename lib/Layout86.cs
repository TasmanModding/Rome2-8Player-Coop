// Reader/writer for Rome II UI layout files (Version086), reverse-engineered from ui\frontend ui\mp_grand_campaign.
// Every field is kept, in file order, so Write(Read(file)) reproduces the file byte-for-byte.
// Fixed-size spans whose meaning is unknown are kept as raw bytes ('r'); every uid or uid reference is a 'u'.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

// T: 'b' u8, 'u' u32, 'i' i32, 's' str (u16 length + bytes), 'w' wstr (u16 length + UTF-16LE), 'r' raw fixed bytes,
//    'L' u32-counted list of nodes, 'B' nested block node
public class LField { public string Name; public char T; public object V; }

public class LNode {
	public string Kind;
	public List<LField> F = new List<LField>();
	public LField Field(string name) { foreach (var f in F) if (f.Name == name) return f; return null; }
	public string Str(string name) { var f = Field(name); return f == null ? null : Layout.L1.GetString((byte[])f.V); }
	public void SetStr(string name, string value) { Field(name).V = Layout.L1.GetBytes(value); }
	public void SetW(string name, string value) { Field(name).V = Encoding.Unicode.GetBytes(value); }
	public int I(string name) { return (int)Field(name).V; }
	public void SetI(string name, int v) { Field(name).V = v; }
	public uint U(string name) { return (uint)Field(name).V; }
	public List<LNode> List(string name) { var f = Field(name); return f == null ? new List<LNode>() : (List<LNode>)f.V; }
	public LNode Child(string name) { return List("children").FirstOrDefault(c => c.Str("name") == name); }
}

public class Layout {
	public static readonly Encoding L1 = Encoding.GetEncoding(28591);  // 1:1 byte <-> char
	public const string Header = "Version086";

	byte[] b; int p; readonly Stack<string> path = new Stack<string>();

	public static LNode Read(byte[] data) {
		if (data.Length < 10 || L1.GetString(data, 0, 10) != Header) throw new InvalidDataException("not a " + Header + " layout");
		var r = new Layout { b = data, p = 10 };
		LNode root;
		try { root = r.Component(); }
		catch (Exception e) { throw new InvalidDataException(string.Format("{0} at 0x{1:X} in {2}", e.Message, r.p, string.Join(" > ", r.path.Reverse())), e); }
		if (r.p != data.Length) throw new InvalidDataException(string.Format("parsed to 0x{0:X} but file is 0x{1:X} bytes", r.p, data.Length));
		return root;
	}

	void Need(int n) { if (n < 0 || p + n > b.Length) throw new EndOfStreamException("unexpected end of data"); }
	void Add(LNode n, string name, char t, object v) { n.F.Add(new LField { Name = name, T = t, V = v }); }
	void B8(LNode n, string name) { Need(1); Add(n, name, 'b', b[p++]); }
	void U32(LNode n, string name) { Need(4); Add(n, name, 'u', BitConverter.ToUInt32(b, p)); p += 4; }
	void I32(LNode n, string name) { Need(4); Add(n, name, 'i', BitConverter.ToInt32(b, p)); p += 4; }
	void Raw(LNode n, string name, int len) { Need(len); var v = new byte[len]; Buffer.BlockCopy(b, p, v, 0, len); p += len; Add(n, name, 'r', v); }
	int Len() { Need(2); int v = BitConverter.ToUInt16(b, p); p += 2; return v; }
	void S(LNode n, string name) { int len = Len(); Need(len); var v = new byte[len]; Buffer.BlockCopy(b, p, v, 0, len); p += len; Add(n, name, 's', v); }
	void W(LNode n, string name) { int len = Len() * 2; Need(len); var v = new byte[len]; Buffer.BlockCopy(b, p, v, 0, len); p += len; Add(n, name, 'w', v); }
	void L(LNode n, string name, Func<LNode> item) {
		Need(4); uint count = BitConverter.ToUInt32(b, p); p += 4;
		if (count > 10000) throw new InvalidDataException("implausible count " + count + " for " + name);
		var list = new List<LNode>();
		for (uint i = 0; i < count; i++) list.Add(item());
		Add(n, name, 'L', list);
	}

	LNode Component() {
		var n = new LNode { Kind = "component" };
		U32(n, "uid"); S(n, "name"); path.Push(n.Str("name"));
		S(n, "class"); I32(n, "x"); I32(n, "y");
		B8(n, "f1"); B8(n, "visible"); B8(n, "f3"); U32(n, "f4"); B8(n, "f5"); U32(n, "f6"); B8(n, "f7");
		W(n, "tooltip"); W(n, "tooltip_key");
		U32(n, "dock"); I32(n, "dock_x"); I32(n, "dock_y"); B8(n, "g4");
		U32(n, "default_state");
		L(n, "images", Image);
		U32(n, "h1"); U32(n, "h2");
		L(n, "states", State);
		L(n, "properties", Property);
		U32(n, "priority");
		L(n, "anims", Anim);
		L(n, "children", Component);
		byte hasBlock = b[p];
		B8(n, "has_block");
		if (hasBlock != 0) Add(n, "block", 'B', Block());
		S(n, "template");
		Raw(n, "tail", 6);
		path.Pop();
		return n;
	}

	LNode Image() {
		var n = new LNode { Kind = "image" };
		U32(n, "uid"); S(n, "path"); U32(n, "w"); U32(n, "h"); B8(n, "extra");
		return n;
	}

	LNode State() {
		var n = new LNode { Kind = "state" };
		U32(n, "uid"); S(n, "name"); path.Push("state:" + n.Str("name"));
		I32(n, "w"); I32(n, "h"); W(n, "text"); W(n, "tooltip");
		Raw(n, "a", 21); W(n, "text_key"); W(n, "w4");
		S(n, "font"); Raw(n, "font_params", 16); S(n, "font_category");
		Raw(n, "c", 24);
		S(n, "shader"); Raw(n, "shader_params", 16); S(n, "shader2"); Raw(n, "shader2_params", 16);
		L(n, "state_images", StateImage);
		I32(n, "ox"); I32(n, "oy");
		L(n, "transitions", Transition);
		path.Pop();
		return n;
	}

	LNode StateImage() {
		var n = new LNode { Kind = "state_image" };
		U32(n, "image"); I32(n, "x"); I32(n, "y"); I32(n, "w"); I32(n, "h"); U32(n, "colour");
		Raw(n, "m", 29); S(n, "shader"); Raw(n, "n", 24);
		return n;
	}

	LNode Transition() {
		var n = new LNode { Kind = "transition" };
		U32(n, "type"); U32(n, "state"); U32(n, "t3"); U32(n, "t4");
		L(n, "targets", TransitionTarget);
		return n;
	}

	LNode TransitionTarget() { var n = new LNode { Kind = "target" }; U32(n, "component"); S(n, "s"); U32(n, "v"); return n; }
	LNode Property() { var n = new LNode { Kind = "property" }; S(n, "key"); S(n, "value"); return n; }

	LNode Anim() {
		var n = new LNode { Kind = "anim" };
		S(n, "name"); B8(n, "a1"); B8(n, "a2");
		L(n, "elements", AnimElement);
		return n;
	}

	LNode AnimElement() {
		var n = new LNode { Kind = "anim_element" };
		Raw(n, "k", 60); S(n, "interpolation");
		L(n, "entries", () => { throw new InvalidDataException("anim element entries are not supported"); });
		return n;
	}

	LNode Block() {
		var n = new LNode { Kind = "block" };
		S(n, "type");
		if (n.Str("type") != "List") throw new InvalidDataException("unsupported block type " + n.Str("type"));
		Raw(n, "list", 22);
		return n;
	}

	// ---------- writing ----------
	public static byte[] Write(LNode root) {
		var ms = new MemoryStream();
		var w = new BinaryWriter(ms);
		w.Write(L1.GetBytes(Header));
		WriteNode(w, root);
		w.Flush();
		return ms.ToArray();
	}

	static void WriteNode(BinaryWriter w, LNode n) {
		foreach (var f in n.F) {
			switch (f.T) {
				case 'b': w.Write((byte)f.V); break;
				case 'u': w.Write((uint)f.V); break;
				case 'i': w.Write((int)f.V); break;
				case 'r': w.Write((byte[])f.V); break;
				case 's': { var v = (byte[])f.V; w.Write((ushort)v.Length); w.Write(v); break; }
				case 'w': { var v = (byte[])f.V; w.Write((ushort)(v.Length / 2)); w.Write(v); break; }
				case 'L': { var l = (List<LNode>)f.V; w.Write((uint)l.Count); foreach (var x in l) WriteNode(w, x); break; }
				case 'B': WriteNode(w, (LNode)f.V); break;
				default: throw new InvalidOperationException("unknown field type " + f.T);
			}
		}
	}

	// ---------- tree helpers ----------
	public static IEnumerable<LNode> Components(LNode root) {
		yield return root;
		foreach (var c in root.List("children")) foreach (var d in Components(c)) yield return d;
	}

	public static LNode Find(LNode root, string name) { return Components(root).FirstOrDefault(c => c.Str("name") == name); }

	static IEnumerable<LNode> AllNodes(LNode n) {
		yield return n;
		foreach (var f in n.F) {
			if (f.T == 'L') foreach (var x in (List<LNode>)f.V) foreach (var y in AllNodes(x)) yield return y;
			else if (f.T == 'B') foreach (var y in AllNodes((LNode)f.V)) yield return y;
		}
	}

	public static LNode DeepClone(LNode n) {
		var c = new LNode { Kind = n.Kind };
		foreach (var f in n.F) {
			object v = f.V;
			if (f.T == 'L') v = ((List<LNode>)f.V).Select(DeepClone).ToList();
			else if (f.T == 'B') v = DeepClone((LNode)f.V);
			else if (f.T == 's' || f.T == 'w' || f.T == 'r') v = ((byte[])f.V).Clone();
			c.F.Add(new LField { Name = f.Name, T = f.T, V = v });
		}
		return c;
	}

	// every uid defined in the subtree (components, states, images)
	public static HashSet<uint> DefinedUids(LNode n) {
		var s = new HashSet<uint>();
		foreach (var x in AllNodes(n)) if (x.Kind == "component" || x.Kind == "state" || x.Kind == "image") s.Add(x.U("uid"));
		return s;
	}

	public static List<uint> DefinedUidList(LNode n) {
		var s = new List<uint>();
		foreach (var x in AllNodes(n)) if (x.Kind == "component" || x.Kind == "state" || x.Kind == "image") s.Add(x.U("uid"));
		return s;
	}

	public static HashSet<uint> AllU32(LNode n) {
		var s = new HashSet<uint>();
		foreach (var x in AllNodes(n)) foreach (var f in x.F) if (f.T == 'u') s.Add((uint)f.V);
		return s;
	}

	// Give every uid defined in the subtree a fresh value and rewrite every u32 field in the subtree holding one
	// (default_state, state image refs, transition targets).
	public static void Reassign(LNode n, Func<uint> next) {
		var map = new Dictionary<uint, uint>();
		foreach (var u in DefinedUids(n)) map[u] = next();
		foreach (var x in AllNodes(n))
			foreach (var f in x.F)
				if (f.T == 'u') { uint nv; if (map.TryGetValue((uint)f.V, out nv)) f.V = nv; }
	}

	public static string Outline(LNode n, int depth, int maxDepth) {
		var sb = new StringBuilder();
		var st = n.List("states");
		sb.AppendFormat("{0}{1} [{2}] ({3},{4}) {5}\n", new string(' ', depth * 2), n.Str("name"), n.Str("class"), n.I("x"), n.I("y"),
			st.Count > 0 ? st[0].I("w") + "x" + st[0].I("h") : "");
		if (depth < maxDepth) foreach (var c in n.List("children")) sb.Append(Outline(c, depth + 1, maxDepth));
		return sb.ToString();
	}
}
