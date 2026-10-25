// Reader/writer for Rome II PFH4 .pack archives (uncompressed).
// Layout: "PFH4", u32 type (low bits: 0 boot, 1 release, 2 patch, 3 mod, 4 movie; flag 0x40 = per-file u32 timestamps),
//         u32 ref count, u32 ref index size, u32 file count, u32 index size, u32 timestamp,
//         ref index, file index (u32 size [+ u32 stamp] + NUL-terminated path per file), then file data in index order.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public class PackEntry { public string Name; public long Offset; public uint Size; }

public static class Pack {
	static readonly Encoding Enc = Encoding.GetEncoding(1252);
	public const uint TypeMovie = 4;

	public static uint Type(string pack) {
		using (var fs = File.OpenRead(pack)) {
			var h = new byte[8];
			if (fs.Read(h, 0, 8) != 8 || Encoding.ASCII.GetString(h, 0, 4) != "PFH4") return uint.MaxValue;
			return BitConverter.ToUInt32(h, 4);
		}
	}

	public static List<PackEntry> Index(string pack) {
		var res = new List<PackEntry>();
		using (var fs = File.OpenRead(pack)) {
			var br = new BinaryReader(fs);
			string magic = Encoding.ASCII.GetString(br.ReadBytes(4)); uint type = br.ReadUInt32();
			if (magic != "PFH4" || (type & ~0x4Fu) != 0) throw new Exception("unsupported pack " + magic + " type " + type);
			bool stamps = (type & 0x40) != 0;
			br.ReadUInt32(); uint refsz = br.ReadUInt32(); uint n = br.ReadUInt32(); uint idx = br.ReadUInt32();
			br.ReadUInt32(); br.ReadBytes((int)refsz);
			byte[] raw = br.ReadBytes((int)idx); long pos = fs.Position; int p = 0;
			for (int i = 0; i < n; i++) {
				uint size = BitConverter.ToUInt32(raw, p); p += 4;
				if (stamps) p += 4;
				int e = Array.IndexOf(raw, (byte)0, p);
				res.Add(new PackEntry { Name = Enc.GetString(raw, p, e - p), Offset = pos, Size = size });
				p = e + 1; pos += size;
			}
		}
		return res;
	}

	public static byte[] Read(string pack, PackEntry e) {
		using (var fs = File.OpenRead(pack)) {
			fs.Position = e.Offset; var b = new byte[e.Size]; int got = 0;
			while (got < b.Length) { int r = fs.Read(b, got, b.Length - got); if (r <= 0) throw new EndOfStreamException(e.Name); got += r; }
			return b;
		}
	}

	// the game's current copy of a file: the last pack in load order that has it (data_rome2 overrides data)
	public static byte[] ReadLatest(string dataDir, string name, string[] packsInOrder) {
		byte[] found = null;
		foreach (var p in packsInOrder) {
			string path = Path.Combine(dataDir, p);
			if (!File.Exists(path)) continue;
			foreach (var e in Index(path)) if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) { found = Read(path, e); break; }
		}
		return found;
	}

	// Writes a "movie" pack (type 4): the game loads every movie pack in data\ by itself, so no launcher/mod list
	// is needed. Fixed timestamp so every player's pack is byte-identical (multiplayer compares game data).
	public static void WriteMovie(string path, IList<KeyValuePair<string, byte[]>> files) {
		var index = new MemoryStream();
		foreach (var f in files) {
			index.Write(BitConverter.GetBytes((uint)f.Value.Length), 0, 4);
			var name = Enc.GetBytes(f.Key.Replace('/', '\\'));
			index.Write(name, 0, name.Length);
			index.WriteByte(0);
		}
		using (var fs = File.Create(path)) {
			var w = new BinaryWriter(fs);
			w.Write(Encoding.ASCII.GetBytes("PFH4"));
			w.Write(TypeMovie);
			w.Write(0u);                                  // pack reference count
			w.Write(0u);                                  // pack reference index size
			w.Write((uint)files.Count);
			w.Write((uint)index.Length);
			w.Write(0x6A000000u);                         // fixed timestamp
			w.Write(index.ToArray());
			foreach (var f in files) w.Write(f.Value);
		}
	}
}
