using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MCCTheaterCapture;

// Independently decoded retail film wire format. Native reference points:
// Reach 43fc00/3c7698 (inputs), 466e64 (incident); Halo4 582a24,
// 582b80/583364 (queues), 1d64e4 (incident); H2A 1d5828 (incident).
// The queue allocation is a native memory accounting value, not wire bytes.
internal static class OtherFilmEventIndex
{
    internal sealed record Command(int Kind, int Queue, int Bit, byte[] Payload, bool Management = false);
    private sealed record Queue(int Start, int End, List<Command> Commands);
    // The incident's trailing sequence is distinct from its small leading
    // control field. Native consumers use sequence-ring occupancy, not Halo 3's
    // timed queue-ID suppression (Reach 10c764; Halo4 1d6be0; H2A 1d5f24).
    // This scanner retains generations across wrap and supports 32 positions
    // of reordering. That is a conservative scanner bound, not a claim about
    // native dispatch timing: native draining also uses a separate game clock.
    // Refuse ambiguous histories instead of manufacturing highlight kills.
    internal sealed class SequenceHistory(int width)
    {
        private readonly int modulus = 1 << width;
        private long? highest;
        private readonly HashSet<long> seen = new();
        public bool Accept(int wire)
        {
            if (wire < 0 || wire >= modulus) throw new IOException("Unsequenced title incident is unsupported.");
            long sequence = wire;
            if (highest is long prior)
            {
                int delta = wire - (int)(prior & (modulus - 1));
                if (delta == modulus / 2 || delta == -modulus / 2) throw new IOException("Ambiguous incident sequence wrap.");
                if (delta > modulus / 2) delta -= modulus;
                else if (delta < -modulus / 2) delta += modulus;
                sequence = prior + delta;
                if (sequence < prior - 32 && !seen.Contains(sequence)) throw new IOException("Incident reordering exceeds the supported scanner history.");
            }
            if (!seen.Add(sequence)) return false;
            highest = Math.Max(highest ?? sequence, sequence);
            long oldest = highest.Value - modulus / 2;
            seen.RemoveWhere(s => s < oldest);
            return true;
        }
    }
    private sealed class Bits(byte[] bytes, int position = 0)
    {
        public int Position = position;
        public int Remaining => checked(bytes.Length * 8 - Position);
        public int Read(int count)
        {
            if (count is < 0 or > 32 || count > Remaining) throw new IOException("Truncated title film bit field.");
            uint value = 0;
            while (count > 0)
            {
                int take = Math.Min(count, 8 - Position % 8);
                value = value << take | (uint)((bytes[Position / 8] >> (8 - Position % 8 - take)) & ((1 << take) - 1));
                Position += take; count -= take;
            }
            return unchecked((int)value);
        }
        public bool Flag() => Read(1) != 0;
        public void Skip(int count)
        {
            if (count < 0 || count > Remaining) throw new IOException("Title film field exceeds its record.");
            Position += count;
        }
        public byte[] Bytes(int length)
        {
            if (length < 0 || length > Remaining / 8) throw new IOException("Truncated title command payload.");
            byte[] result = new byte[length]; for (int i = 0; i < length; i++) result[i] = (byte)Read(8); return result;
        }
        public void End()
        {
            if (Remaining > 7 || Read(Remaining) != 0) throw new IOException("Unsupported title film fields or nonzero padding.");
        }
    }

    public static FilmIndex Read(string path, string? cache, CancellationToken token, Action<double>? progress = null, string? mccRoot = null)
    {
        path = Path.GetFullPath(path);
        using var input = File.OpenRead(path);
        if (input.Length > 4L * 1024 * 1024 * 1024) throw new IOException("Film size exceeds 4 GiB.");
        var header = FilmTitle.ReadHeader(input); var title = header.Title;
        if (title == FilmTitle.Halo3) throw new IOException("Use the Halo 3 event reader for this film.");
        var metadata = FilmMetadata.Read(path) ?? throw new IOException("Unsupported title film metadata.");
        if (string.IsNullOrWhiteSpace(mccRoot)) throw new IOException("Select the MCC installation to resolve this title's map incident names.");
        var strings = MapStringIds.Load(Path.Combine(title.MapsFolder(mccRoot), metadata.Map + ".map"), title.Id);
        // No cross-map cache reuse: decode against the selected map's current
        // string table on every scan. Halo 3's v1 cache is never consulted.
        var index = new FilmIndex { Title = title.Id, Film = path, Hash = FilmEventIndex.ContentHash(input, token), Map = metadata.Map, TotalTicks = metadata.TotalTicks };
        var roster = Roster(header.Bytes, title);
        input.Position = 0; byte[] chunk = new byte[12]; long dataStart = -1, dataEnd = -1; string author = "", authorName = ""; bool filmHeader = false;
        for (int count = 0; count < 64 && input.Position < input.Length; count++)
        {
            long start = input.Position; input.ReadExactly(chunk); string tag = Encoding.ASCII.GetString(chunk, 0, 4);
            uint size = tag == "flmd" ? BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4)) : BinaryPrimitives.ReadUInt32BigEndian(chunk.AsSpan(4));
            long extent = size + (tag == "flmd" ? 4L : 0L);
            if (size < 12 || extent > input.Length - start) throw new IOException("Invalid title film chunk extent.");
            if (tag == "flmh")
            {
                if (filmHeader || start != header.Offset) throw new IOException("Duplicate title film header.");
                filmHeader = true;
            }
            if (tag == "chdr")
            {
                if (size < 0x58 || author.Length != 0) throw new IOException("Invalid title film author header.");
                byte[] bytes = new byte[size]; chunk.CopyTo(bytes, 0); input.ReadExactly(bytes.AsSpan(12));
                author = Convert.ToHexString(bytes.AsSpan(0x50, 8)).ToLowerInvariant();
                authorName = Encoding.ASCII.GetString(bytes, 0x58, 16).Split('\0')[0];
                if (authorName.Length == 0 || authorName.Any(char.IsControl)) authorName = author;
            }
            if (tag == "flmd") { dataStart = start + 16; dataEnd = start + extent; break; }
            input.Position = start + extent;
        }
        var recorder = roster.Values.Where(p => p.Identifier == author).ToArray();
        if (author.Length == 0 || author == "0000000000000000") throw new IOException("Film has no author identity to resolve the recording player.");
        // Highlights follow the recording player only. A film saved by an
        // account absent from the starting roster (e.g. not in the match, or
        // joined after start) has no recorder events to plan clips from.
        if (recorder.Length == 0)
            throw new IOException($"The account that saved this film ({authorName}) was not a player in it. Players: {string.Join(", ", roster.Values.Select(p => p.Name))}.");
        if (recorder.Length != 1) throw new IOException("Film author does not identify exactly one recording player.");
        index.Recorder = recorder[0].Name;
        if (dataStart < 0) throw new IOException("Title film has no update stream.");
        input.Position = dataStart; int update = 0, segment = 0; byte[] packed = new byte[4];
        var sequences = new SequenceHistory(title == FilmTitle.Reach ? 8 : 9);
        while (input.Position < dataEnd)
        {
            token.ThrowIfCancellationRequested();
            if (dataEnd - input.Position < 4) throw new IOException("Truncated title film record header.");
            input.ReadExactly(packed); uint record = BinaryPrimitives.ReadUInt32LittleEndian(packed); int length = (int)(record & 0x3fffffff);
            if (record >> 30 != 0 || length is < 1 or > 1048576 || length > dataEnd - input.Position) throw new IOException("Unsupported title film record type or extent.");
            long offset = input.Position; byte[] bytes = new byte[length]; input.ReadExactly(bytes);
            var commands = title == FilmTitle.Reach ? ReachFrame(bytes, update) : LaterFrame(bytes, update, title == FilmTitle.Halo2A);
            foreach (var command in commands)
            {
                if (command.Management)
                {
                    if (title == FilmTitle.Reach) throw new IOException("Reach management commands are outside the verified multiplayer profile.");
                    CheckPlayerProperties(command, roster); continue;
                }
                if (command.Kind != 1) continue;
                var incident = Incident(command.Payload, title == FilmTitle.Reach);
                if (incident == null) continue;
                var (id, queue, cause, effect) = incident.Value;
                if (!sequences.Accept(queue)) continue;
                if (!strings.TryGetValue(id, out string? name)) throw new IOException($"Map incident string 0x{id:x8} is unresolved.");
                int category = -1, eventId = -1;
                if (name == "kill") { category = 0; eventId = 0; }
                else if (name is "death" or "suicide") { category = 0; eventId = 1; }
                else if (name.StartsWith("multikill_x", StringComparison.Ordinal) && int.TryParse(name.AsSpan(11), out int tier) && tier is >= 2 and <= 10) { category = 1; eventId = tier; }
                if (name == "round_over") segment++;
                if (category < 0) continue;
                if (cause >= 0 && !roster.ContainsKey(cause) || effect >= 0 && !roster.ContainsKey(effect)) throw new IOException("Incident refers to an unrecognized player slot.");
                roster.TryGetValue(cause, out var player);
                bool local = player?.Identifier == author && !player.Left;
                bool effectLocal = roster.TryGetValue(effect, out var victim) && victim.Identifier == author && !victim.Left;
                // Reach's death incident retains killer/victim order; the
                // later titles emit death with victim/killer order. Kill's
                // effect is consistently the victim and independently resets
                // the recorder's chain, including when a death label is absent.
                bool died = category == 0 && (name == "kill" && effectLocal ||
                    name == "death" && (title == FilmTitle.Reach ? effectLocal : local) ||
                    name == "suicide" && (local || effectLocal));
                index.Events.Add(new(update, update, segment, offset * 8 + command.Bit, category, eventId, queue,
                    cause, effect, player?.Identifier ?? "", player?.Name ?? "", local, died));
                if (index.Events.Count > 200000) throw new IOException("Film contains too many indexed incidents.");
            }
            update++; if (update > index.TotalTicks) throw new IOException("Title film contains more updates than its header.");
            if ((update & 255) == 0) progress?.Invoke((double)update / index.TotalTicks);
        }
        if (update != index.TotalTicks || input.Position != dataEnd) throw new IOException("Title film update count does not match its header.");
        ValidateEnd(input, dataEnd);
        if (FilmEventIndex.ContentHash(input, token) != index.Hash) throw new IOException("Film changed while scanning.");
        progress?.Invoke(1); return index;
    }

    // Retail title films end with the 17-byte BLF EOF record, containing its
    // own absolute file offset, followed by at most 15 zero alignment bytes.
    internal static void ValidateEnd(Stream input, long dataEnd)
    {
        if (input.Length - dataEnd is < 17 or > 32) throw new IOException("Missing or invalid title film EOF extent.");
        input.Position = dataEnd; byte[] eof = new byte[17]; input.ReadExactly(eof);
        if (Encoding.ASCII.GetString(eof, 0, 4) != "_eof" || BinaryPrimitives.ReadUInt32BigEndian(eof.AsSpan(4)) != 17 ||
            BinaryPrimitives.ReadUInt16BigEndian(eof.AsSpan(8)) != 1 || BinaryPrimitives.ReadUInt16BigEndian(eof.AsSpan(10)) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(eof.AsSpan(12)) != dataEnd || eof[16] != 0)
            throw new IOException("Unsupported or inconsistent title film EOF record.");
        while (input.Position < input.Length) if (input.ReadByte() != 0) throw new IOException("Nonzero title film alignment padding.");
    }

    private static Dictionary<int, FilmPlayer> Roster(byte[] h, FilmTitle title)
    {
        var result = new Dictionary<int, FilmPlayer>();
        int begin = title == FilmTitle.Reach ? 0x870 : 0x2c060, stride = title == FilmTitle.Reach ? 0xa0 : 0x148;
        for (int slot = 0; slot < 16; slot++)
        {
            int p = begin + slot * stride; if (BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(p)) == 0) continue;
            string id = Convert.ToHexString(h.AsSpan(p + 0x16, 8)).ToLowerInvariant();
            string name = Encoding.Unicode.GetString(h, p + 0x20, 32).Split('\0')[0];
            if (name.Length == 0 || name.Any(char.IsControl) || id == "0000000000000000" || result.Values.Any(p => p.Identifier == id)) throw new IOException("Invalid or duplicate title film player identity.");
            result.Add(slot, new(slot, id, name, Convert.ToHexString(h.AsSpan(p + 0x10, 6)).ToLowerInvariant()));
        }
        return result;
    }

    private static Queue ReadQueue(byte[] data, int start, int flags, int overhead)
    {
        var r = new Bits(data, start); int count = r.Read(12), allocation = r.Read(17); r.Skip(flags);
        if ((long)count * (overhead + 1) > allocation || (long)count * 57 > r.Remaining) throw new IOException("Invalid command queue allocation.");
        int used = 0; var commands = new List<Command>(count);
        for (int i = 0; i < count; i++)
        {
            int kind = r.Read(4); r.Skip(16); int queue = r.Read(16), length = r.Read(13);
            if (kind == 0 || length == 0) throw new IOException("Invalid title command header.");
            int bit = r.Position; commands.Add(new(kind, queue, bit, r.Bytes(length))); used = checked(used + overhead + length);
        }
        if (used != allocation) throw new IOException("Command queue allocation does not match its payloads.");
        return new(start, r.Position, commands);
    }

    internal static (uint Id, int Queue, int Cause, int Effect)? Incident(byte[] bytes, bool reach)
    {
        var r = new Bits(bytes); int type = r.Read(7), refs = r.Read(2);
        if (refs > 2 || type > (reach ? 0x47 : 0x7f)) throw new IOException("Unsupported native event header.");
        for (int i = 0; i < refs; i++) if (r.Flag()) r.Skip(27);
        if (type != (reach ? 14 : 20)) return null;
        uint id = unchecked((uint)r.Read(32)); r.Skip(reach ? 6 : 8);
        int cause = r.Flag() ? -1 : r.Read(4), effect = r.Flag() ? -1 : r.Read(4);
        r.Skip(reach ? 5 + 5 + 7 + 3 + 11 : 16 + 16 + 7 + 3 + 11 + 2);
        if (!r.Flag()) r.Skip(16);
        int queue;
        if (reach) queue = r.Flag() ? -1 : r.Read(8);
        else { if (!r.Flag()) r.Skip(5); queue = r.Flag() ? -1 : r.Read(9); }
        r.End(); return (id, queue, cause, effect);
    }

    internal static List<Command> ReachFrame(byte[] bytes, int update)
    {
        var r = new Bits(bytes);
        if (r.Flag()) throw new IOException("Reach actor-control input is unsupported.");
        if (r.Flag()) Mask(r, 17, () => r.Skip(48));
        if (r.Flag()) Mask(r, 16, () => ReachAction(r));
        Mask(r, 17, () => r.Skip(4));
        if (r.Flag()) throw new IOException("Reach extension input is unsupported.");
        for (int i = 0; i < 4; i++) if (r.Flag()) { r.Skip(32); r.Skip(r.Read(32)); }
        if (r.Read(32) != update) throw new IOException("Reach update clock is discontinuous.");
        r.Skip(5); if (r.Flag()) Mask(r, 16, () => ReachPlayer(r)); r.Skip(4);
        var a = ReadQueue(bytes, r.Position, 0, 32); var b = ReadQueue(bytes, a.End, 2, 32);
        r.Position = b.End; r.End(); return Commands(a, b);
    }
    private static void Mask(Bits r, int width, Action action)
    { int mask = r.Read(width); for (int i = 0; i < width; i++) if ((mask & (1 << i)) != 0) action(); }
    private static void Direction(Bits r, int width) { if (!r.Flag()) r.Skip(width); }
    private static List<Command> Commands(Queue a, Queue b)
    {
        // Only the replication queue's event/create/update/destroy namespace is
        // indexed. Management commands may replace player slots, so reject them
        // until their identity transitions have a separately verified decoder.
        if (a.Commands.Any(c => c.Kind != 9) || b.Commands.Any(c => c.Kind is < 1 or > 4))
            throw new IOException("This film contains unsupported management or player identity changes.");
        return a.Commands.Select(c => c with { Management = true }).Concat(b.Commands).ToList();
    }
    // Halo4 519088 and H2A 519408: slot4, XUID64, operation3. Operation
    // four changes properties; three adds machine/left fields; one swaps two
    // slots after independently checking both old XUIDs. The remaining property
    // payload is length-delimited and has no further identity fields.
    internal static void CheckPlayerProperties(Command command, Dictionary<int, FilmPlayer> roster)
    {
        var r = new Bits(command.Payload); int slot = r.Read(4); string id = Convert.ToHexString(r.Bytes(8)).ToLowerInvariant(); int operation = r.Read(3);
        roster.TryGetValue(slot, out var player);
        const string empty = "0000000000000000";
        if (operation == 1)
        {
            int other = r.Read(4); string otherId = Convert.ToHexString(r.Bytes(8)).ToLowerInvariant(); roster.TryGetValue(other, out var right);
            if (slot == other || (player?.Identifier ?? empty) != id || (right?.Identifier ?? empty) != otherId)
                throw new IOException("Ambiguous recorded player slot swap.");
            r.End();
            if (right == null) roster.Remove(slot); else roster[slot] = right with { Slot = slot };
            if (player == null) roster.Remove(other); else roster[other] = player with { Slot = other };
            return;
        }
        if (operation is not (3 or 4) || id == empty || roster.Values.Any(p => p.Slot != slot && p.Identifier == id))
            throw new IOException("Unsupported recorded player identity transition.");
        if (operation == 4)
        {
            if (player?.Identifier != id) throw new IOException("Player properties changed an unverified XUID.");
            return;
        }
        bool left = r.Flag(); r.Skip(1); string machine = "";
        if (!left) { machine = Convert.ToHexString(r.Bytes(6)).ToLowerInvariant(); r.Skip(4); }
        if (player != null && player.Identifier != id && !player.Left) throw new IOException("An active player slot was replaced without a recorded departure.");
        string name = player?.Identifier == id ? player.Name : "Player " + id;
        roster[slot] = new(slot, id, name, machine, left);
    }
    private static void ReachAction(Bits r)
    {
        int flags = r.Read(2); r.Skip(27); if (r.Flag()) r.Skip(27); if ((flags & 2) == 0) return;
        if (r.Flag()) r.Skip(27); r.Skip(7); if (r.Flag()) r.Skip(27); r.Skip(3);
        Direction(r, 2); Direction(r, 2);
        throw new IOException("Reach map-dependent action coordinates are unsupported.");
    }
    private static void ReachPlayer(Bits r)
    {
        if (r.Flag()) r.Skip(2); if (r.Flag()) r.Skip(48); if (r.Flag()) r.Skip(32);
        r.Skip(25); if (r.Flag()) r.Skip(12); r.Skip(10); if (r.Flag()) r.Skip(5);
        bool flags = r.Flag(); r.Skip(1); if (flags) r.Skip(10); r.Skip(2);
        if (r.Flag()) { r.Skip(5); if (r.Flag()) r.Skip(5); }
        r.Skip(3); Direction(r, 2); Direction(r, 2); Direction(r, 1); r.Skip(2);
        if (r.Flag()) throw new IOException("Reach map-dependent unit selection is unsupported.");
        if (r.Flag()) for (int i = 0; i < 3; i++)
        {
            int kind = r.Read(4); if (kind == 0) continue; r.Skip(32);
            if (kind is 2 or 10) r.Skip(4); else if (kind is 5 or 6 or 9) Direction(r, 6);
        }
        if (r.Flag())
        {
            if (r.Flag()) { int kind = r.Read(2); if (kind == 1) { r.Skip(32); if (r.Flag()) r.Skip(5); } else if (kind == 2) r.Skip(32); }
            r.Skip(8); if ((r.Read(3) & 2) != 0) Direction(r, 34);
        }
        if (r.Flag()) r.Skip(4); r.Skip(34);
    }

    // Halo4 518354. Shared by the H2A retail input format; different from
    // Reach in button widths, weapon selection, context types and user data.
    private static void LaterPlayer(Bits r, int authority, bool h2a)
    {
        if (r.Flag()) r.Skip(2 + 2 * authority); if (r.Flag()) r.Skip(50); if (r.Flag()) r.Skip(40);
        r.Skip(25); if (r.Flag()) r.Skip(12); r.Skip(10); if (r.Flag()) r.Skip(5);
        bool flags = r.Flag(); r.Skip(1); if (flags) r.Skip(10); r.Skip(2);
        if (r.Flag()) { r.Skip(5); if (r.Flag()) r.Skip(5); }
        r.Skip(3); Direction(r, 2); Direction(r, 2); r.Skip(3); Direction(r, 3); r.Skip(2);
        if (r.Flag()) throw new IOException("Title map-dependent unit selection is unsupported."); r.Skip(6);
        if (r.Flag()) for (int i = 0; i < 3; i++)
        {
            int kind = r.Read(5); if (kind == 0) continue; r.Skip(32);
            if (kind is 5 or 8 or 13) r.Skip(4);
            else if (kind is 9 or 10 or 12 or 18) { if (r.Flag()) r.Skip(6); }
        }
        if (r.Flag())
        {
            if (r.Flag()) { int kind = r.Read(2); if (kind == 1) { r.Skip(32); if (r.Flag()) r.Skip(6); } else if (kind == 2) r.Skip(32); }
            r.Skip(8); if ((r.Read(3) & 2) != 0) Direction(r, 34);
        }
        if (r.Flag()) r.Skip(4);
        if (r.Flag()) r.Skip(384);
        r.Skip(h2a ? 39 : 40);
    }

    internal static List<Command> LaterFrame(byte[] bytes, int update, bool h2a = false)
    {
        var r = new Bits(bytes);
        if (r.Read(32) != update) throw new IOException("Title film update clock is discontinuous.");
        r.Skip(5); Mask(r, 16, () => LaterPlayer(r, 0, h2a));
        if (r.Flag()) throw new IOException("Title actor-control input is unsupported.");
        if (r.Flag()) Mask(r, 17, () => r.Skip(48));
        if (r.Flag()) Mask(r, 16, () => LaterAction(r));
        Mask(r, 17, () => r.Skip(4));
        if (r.Flag()) throw new IOException("Title extension input is unsupported.");
        r.Skip(4);
        for (int i = 0; i < 4; i++) if (r.Flag()) { r.Skip(32); r.Skip(r.Read(32)); }
        var a = ReadQueue(bytes, r.Position, 0, 16); var b = ReadQueue(bytes, a.End, 2, 16);
        r.Position = b.End; r.End(); return Commands(a, b);
    }
    private static void LaterAction(Bits r)
    {
        int flags = r.Read(2); r.Skip(27); if (r.Flag()) r.Skip(27); if ((flags & 2) == 0) return;
        if (r.Flag()) r.Skip(27); r.Skip(13); if (r.Flag()) r.Skip(27); r.Skip(11); if (r.Flag()) r.Skip(27);
        r.Skip(3); Direction(r, 2); Direction(r, 2); r.Skip(3); Direction(r, 3);
        throw new IOException("Title map-dependent action coordinates are unsupported.");
    }
}
