using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MCCTheaterCapture;

internal sealed record FilmPlayer(int Slot, string Identifier, string Name, string Machine, bool Left = false);
internal sealed record FilmEvent(int Update, int Clock, int Segment, long SourceBit, int Category, int EventId,
    int QueueId, int CauseSlot, int EffectSlot, string CauseId, string CauseName, bool Recorder, bool RecorderDied);
internal sealed class FilmIndex
{
    public string Schema { get; set; } = "mcc-film-events/v1";
    public string Film { get; set; } = "";
    public string Title { get; set; } = "halo3";
    public string Hash { get; set; } = "";
    public string Map { get; set; } = "";
    public string Recorder { get; set; } = "";
    public int TotalTicks { get; set; }
    public List<FilmEvent> Events { get; set; } = new();
}

// Bounded structural reader for the pinned retail Halo 3 saved-film grammar.
// No world/map decoding or sibling-project runtime is needed. Wire fields are
// MSB-first; BLF sizes are BE except flmd and its packed record headers (LE).
internal static class FilmEventIndex
{
    internal sealed class Bits(byte[] data)
    {
        public int Position { get; private set; }
        public int Remaining => checked(data.Length * 8 - Position);
        public ulong Read(int count)
        {
            if (count < 0 || count > 64 || count > Remaining) throw new IOException("Truncated film bit field.");
            ulong value = 0;
            for (int i = 0; i < count; i++, Position++) value = (value << 1) | (uint)((data[Position / 8] >> (7 - Position % 8)) & 1);
            return value;
        }
        public int Int(int count) => checked((int)Read(count));
        public bool Flag() => Read(1) != 0;
        public void Skip(int count)
        {
            if (count < 0 || count > Remaining) throw new IOException("Film field extends outside its record.");
            Position += count;
        }
        public byte[] Bytes(int count)
        {
            if (count < 0 || count > Remaining / 8) throw new IOException("Truncated queue payload.");
            var bytes = new byte[count]; for (int i = 0; i < count; i++) bytes[i] = (byte)Read(8); return bytes;
        }
        public string Hex(int bytes) => Convert.ToHexString(Bytes(bytes)).ToLowerInvariant();
        public void End()
        {
            if (Remaining > 7 || Read(Remaining) != 0) throw new IOException("Unsupported film payload or nonzero padding.");
        }
    }

    internal static string ContentHash(Stream stream, CancellationToken token)
    {
        stream.Position = 0; using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[65536]; int count;
        while ((count = stream.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static FilmIndex Read(string path, string? cache, CancellationToken token, Action<double>? progress = null, string? mccRoot = null)
    {
        path = Path.GetFullPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 12 || stream.Length > 4L * 1024 * 1024 * 1024) throw new IOException("Film size is unsupported (maximum 4 GiB).");
        if(FilmTitle.ReadHeader(stream).Title!=FilmTitle.Halo3)return OtherFilmEventIndex.Read(path,cache,token,progress,mccRoot);
        var hash = ContentHash(stream, token);
        var cacheFile = cache == null ? null : Path.Combine(cache, hash + "-v1.json");
        if (cacheFile != null && File.Exists(cacheFile))
        {
            try
            {
                var json = File.ReadAllText(cacheFile);
                var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
                var saved = File.Exists(cacheFile + ".sha256") && File.ReadAllText(cacheFile + ".sha256") == digest ? JsonSerializer.Deserialize<FilmIndex>(json) : null;
                if (saved?.Schema == "mcc-film-events/v1" && saved.Hash == hash && saved.TotalTicks > 0)
                { saved.Film = path; progress?.Invoke(1); return saved; }
            }
            catch (Exception e) when (e is IOException or JsonException) { }
        }
        stream.Position = 0; var index = new FilmIndex { Film = path, Hash = hash };
        var roster = new Dictionary<int, FilmPlayer>(); string local = ""; long dataStart = -1, dataEnd = -1;
        byte[] header = new byte[12]; bool eof = false, filmHeader = false;
        for (int chunks = 0; stream.Position < stream.Length && chunks < 64; chunks++)
        {
            long start = stream.Position; stream.ReadExactly(header);
            string tag = Encoding.ASCII.GetString(header, 0, 4);
            if (start == 0 && tag != "_blf") throw new IOException("Not a BLF Theater film.");
            uint length = tag == "flmd" ? BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) : BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            if (length < 12 || length > stream.Length - start) throw new IOException("Invalid film chunk extent.");
            if (tag == "flmh")
            {
                if (filmHeader || length != 0xfc80 || BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8)) != 10 || BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(10)) != 1)
                    throw new IOException("Unsupported film header; expected Halo 3 flmh 10.1.");
                filmHeader = true; byte[] h = new byte[length]; header.CopyTo(h, 0); stream.ReadExactly(h.AsSpan(12));
                index.TotalTicks = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(0x98));
                if (index.TotalTicks is < 1 or > 2592000) throw new IOException("Invalid film record count.");
                index.Map = Encoding.ASCII.GetString(h, 0x164, 0x54).Split('\0')[0].Replace('\\', '/').Split('/').Last();
                if (h[0x120 + 0xdda] == 0) throw new IOException("The film does not identify its local recording machine.");
                local = Convert.ToHexString(h.AsSpan(0x120 + 0xddb, 6)).ToLowerInvariant();
                if (local == "000000000000") throw new IOException("The recording machine identity is empty.");
                for (int slot = 0; slot < 16; slot++)
                {
                    int p = 0x310 + slot * 0xb8; if (h[p] == 0) continue;
                    roster[slot] = new(slot, Convert.ToHexString(h.AsSpan(p + 0x10, 8)).ToLowerInvariant(),
                        Encoding.Unicode.GetString(h, p + 0x18, 32).Split('\0')[0], Convert.ToHexString(h.AsSpan(p + 8, 6)).ToLowerInvariant());
                }
                if (roster.Values.Any(p => p.Identifier == "0000000000000000") || roster.Values.Select(p => p.Identifier).Distinct().Count() != roster.Count)
                    throw new IOException("Invalid initial recorded player identities.");
            }
            if (tag == "flmd") { if (dataStart >= 0) throw new IOException("Duplicate film data chunk."); dataStart = start + 12; dataEnd = start + length; }
            stream.Position = start + length;
            if (tag == "_eof") { eof = true; break; }
        }
        if (!eof || !filmHeader || dataStart < 0) throw new IOException("Film is missing a required header, data or EOF chunk.");
        stream.Position = dataStart; int update = 0, segment = 0, previousClock = -1; var busy = new Dictionary<int, long>();
        byte[] packedBytes = new byte[4];
        while (stream.Position < dataEnd)
        {
            token.ThrowIfCancellationRequested(); long recordStart = stream.Position;
            stream.ReadExactly(packedBytes);
            uint packed = BinaryPrimitives.ReadUInt32LittleEndian(packedBytes); int size = (int)(packed & 0x3fffffff);
            if ((packed >> 30) != 0 || size < 1 || size > 1024 * 1024 || size > dataEnd - stream.Position) throw new IOException($"Unsupported or invalid saved-state record at update {update}.");
            var bytes = new byte[size]; stream.ReadExactly(bytes); var bits = new Bits(bytes);
            SkipPrefix(bits);
            if (bits.Read(32) != (uint)update) throw new IOException("Film update sequence is invalid.");
            bits.Skip(4); int mask = bits.Flag() ? bits.Int(16) : 0;
            for (int p = 0; p < 16; p++) if ((mask & (1 << p)) != 0) SkipAction(bits);
            uint clockValue = (uint)bits.Read(32); if (clockValue > int.MaxValue) throw new IOException("Unsupported game clock.");
            int clock = (int)clockValue; bits.Skip(32);
            if (bits.Flag()) throw new IOException("Film verification records are not supported by the highlight scanner.");
            if (clock < previousClock) { segment++; busy.Clear(); } previousClock = clock;
            for (int q = 0; q < 2; q++)
            {
                int count = bits.Int(12), allocation = bits.Int(17), actual = 0;
                for (int item = 0; item < count; item++)
                {
                    int type = bits.Int(4), length = bits.Int(10); long sourceBit = (recordStart + 4) * 8 + bits.Position;
                    byte[] payload = bits.Bytes(length); actual += length + 32;
                    if (type == 9) ReadPlayer(new Bits(payload), roster);
                    if (type != 1) continue;
                    var ev = new Bits(payload); int eventType = ev.Int(6), refs = ev.Int(2);
                    if (eventType != 7) continue; // Other event branches are length-delimited.
                    if (refs != 0) throw new IOException("Game-engine event has unexpected references.");
                    int category = ev.Int(4), id = ev.Int(8), queueId = ev.Int(6);
                    int[] fields = new int[5]; int[] widths = { 4, 4, 5, 4, 5 };
                    for (int i = 0; i < fields.Length; i++) fields[i] = ev.Flag() ? -1 : ev.Int(widths[i]);
                    if (!ev.Flag() && !ev.Flag()) ev.Skip(32);
                    if (category == 9) ev.Skip(4);
                    if (ev.Flag()) ev.Skip(16); ev.End();
                    if (busy.TryGetValue(queueId, out long until) && until >= clock) continue;
                    busy[queueId] = (long)clock + (category == 0 && id is 12 or 25 or 58 ? 120 : 9);
                    var localPlayers = roster.Values.Where(p => !p.Left && p.Machine == local).ToArray();
                    if (localPlayers.Length > 1) throw new IOException("Multiple local players: recorder POV is ambiguous.");
                    var recorder = localPlayers.SingleOrDefault();
                    if (recorder != null) index.Recorder = recorder.Name;
                    roster.TryGetValue(fields[1], out var cause); roster.TryGetValue(fields[3], out var effect);
                    if (category == 0 || category == 1 && id is >= 2 and <= 10)
                        index.Events.Add(new(update, clock, segment, sourceBit, category, id, queueId, fields[1], fields[3],
                            cause?.Identifier ?? "", cause?.Name ?? "", recorder != null && cause?.Identifier == recorder.Identifier,
                            recorder != null && effect?.Identifier == recorder.Identifier));
                    if (index.Events.Count > 200000) throw new IOException("Film contains too many indexed events.");
                }
                if (allocation != actual) throw new IOException("Invalid film queue allocation size.");
            }
            bits.End(); update++;
            if (update % 240 == 0) progress?.Invoke((stream.Position - dataStart) / (double)(dataEnd - dataStart));
        }
        if (update != index.TotalTicks || stream.Position != dataEnd) throw new IOException("Film record count does not match its header.");
        if (index.Recorder.Length == 0)
        {
            var locals = roster.Values.Where(p => !p.Left && p.Machine == local).ToArray();
            if (locals.Length > 1) throw new IOException("Multiple local players: recorder POV is ambiguous.");
            index.Recorder = locals.SingleOrDefault()?.Name ?? "";
        }
        if (index.Recorder.Length == 0) throw new IOException("Could not resolve the recorder from the recorded roster.");
        if (ContentHash(stream, token) != hash) throw new IOException("Film changed while scanning.");
        if (cacheFile != null)
        {
            Directory.CreateDirectory(cache!); var temp = cacheFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                string json = JsonSerializer.Serialize(index); File.WriteAllText(temp, json); File.Move(temp, cacheFile, true);
                File.WriteAllText(temp, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))) ; File.Move(temp, cacheFile + ".sha256", true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        progress?.Invoke(1); return index;
    }

    private static void SkipPrefix(Bits b)
    {
        if (b.Flag()) throw new IOException("Actor records are unsupported by the highlight scanner.");
        if (b.Flag()) { int mask = b.Int(17); for (int i = 0; i < 17; i++) if ((mask & (1 << i)) != 0) b.Skip(48); }
        if (b.Flag()) throw new IOException("Player prediction records are unsupported by the highlight scanner.");
        if (b.Flag()) { int kind = b.Int(2); if (kind != 0) b.Skip(5); if (kind == 2) b.Skip(193); }
        for (int i = 0; i < 4; i++) if (b.Flag()) { b.Skip(32); uint count = (uint)b.Read(32); if (count > b.Remaining) throw new IOException("Invalid optional payload length."); b.Skip((int)count); }
    }
    private static void SkipAction(Bits b)
    {
        if (b.Flag()) b.Skip(4);
        b.Skip(34 + 18 + 13 + 12 + 5 + 5); if (b.Flag()) b.Skip(10);
        b.Skip(1 + 2 + 20 + 5 + 3 + 3 + 3 + 2); int context = b.Int(4);
        if (context != 0) b.Skip(32); if (context is 1 or 9) b.Skip(4); else if (context is 4 or 5 or 8) b.Skip(6);
        if (b.Flag()) b.Skip(32);
        if (b.Flag())
        {
            b.Skip(8); int flags = b.Int(4); if ((flags & 4) != 0 && !b.Flag()) b.Skip(27);
            if (b.Read(32) != uint.MaxValue && b.Flag()) b.Skip(5);
        }
        if (b.Flag()) b.Skip(4); b.Skip(30);
    }
    private static string PlayerName(Bits b)
    {
        const string alphabet = "\0" + "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ() -_";
        var text = new StringBuilder();
        for (int i = 0; i < 16; i++) { int c = b.Int(7); if (c == 0) return text.ToString(); text.Append(c < alphabet.Length ? alphabet[c] : (char)c); }
        throw new IOException("Unterminated player name.");
    }
    private static void Appearance(Bits b)
    {
        b.Skip(1 + 18 + 1 + 112);
        bool ended = false; for (int i = 0; i < 4; i++) if (b.Read(16) == 0) { ended = true; break; }
        if (!ended) throw new IOException("Unterminated player service tag.");
        b.Skip(7 + 7 + 3 + 6 + 6 + 6 + 23 * 8);
    }
    private static void ReadPlayer(Bits b, Dictionary<int, FilmPlayer> roster)
    {
        int slot = b.Int(4); string id = b.Hex(8); int operation = b.Int(3); roster.TryGetValue(slot, out var prior);
        if (operation == 1)
        {
            int other = b.Int(4); string otherId = b.Hex(8); roster.TryGetValue(other, out var right);
            if (slot == other || (prior?.Identifier ?? "0000000000000000") != id || (right?.Identifier ?? "0000000000000000") != otherId)
                throw new IOException("Ambiguous recorded player-slot swap.");
            if (right == null) roster.Remove(slot); else roster[slot] = right with { Slot = slot };
            if (prior == null) roster.Remove(other); else roster[other] = prior with { Slot = other };
        }
        else if (operation is 3 or 4)
        {
            bool left = prior?.Left ?? false; string machine = prior?.Machine ?? "";
            if (operation == 3) { left = b.Flag(); b.Skip(1); if (!left) { machine = b.Hex(6); b.Skip(4); } }
            string name = PlayerName(b); Appearance(b); b.Skip(64 + 1); if (b.Flag()) b.Skip(5);
            b.Skip(1 + 1 + 10 + 4 + 2); PlayerName(b); if (b.Flag()) b.Skip(5); if (b.Flag()) b.Skip(5);
            if (prior != null && prior.Identifier != id || roster.Values.Any(p => p.Slot != slot && p.Identifier == id))
                throw new IOException("Player identity changed without a supported roster transition.");
            roster[slot] = new(slot, id, name, machine, left);
        }
        else throw new IOException($"Unsupported player roster operation {operation}.");
        b.End();
    }
}
