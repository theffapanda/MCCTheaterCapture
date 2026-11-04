using System.Buffers.Binary;
using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MCCTheaterCapture;

// Current MCC cache format facts corroborated against installed map bytes and
// Assembly's primary layout documentation (no implementation code imported):
// https://github.com/XboxChaos/Assembly/tree/dev/src/Blamite/Formats/ReachMCC
// https://github.com/XboxChaos/Assembly/tree/dev/src/Blamite/Formats/Halo4MCC
// https://github.com/XboxChaos/Assembly/tree/dev/src/Blamite/Formats/Halo2AMCC
// Cache v13 stores debug-section virtual pointers, a uint32 offset mask, indexed
// UTF-8 names and namespace lengths. String IDs use 19 index / 8 namespace /
// 5 length bits. Only this bounded string metadata is read, never tag geometry.
internal static class MapStringIds
{
    internal const uint IndexMask = (1u << 19) - 1;
    internal const uint NameMask = (1u << 27) - 1;
    private const int MaximumStrings = 1_000_000, MaximumBytes = 64 * 1024 * 1024, MaximumNameBytes = 4096;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static MapStringIdTable Load(string mapPath, string title)
    {
        string path = Path.GetFullPath(mapPath);
        if (!Path.GetExtension(path).Equals(".map", StringComparison.OrdinalIgnoreCase)) throw new IOException("Expected an installed MCC .map file for incident names.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var table = Read(file, title, path);
        if (!Path.GetFileNameWithoutExtension(path).Equals(table.MapName, StringComparison.OrdinalIgnoreCase))
            throw new IOException("MCC map filename and native map identity differ.");
        return table;
    }

    internal static MapStringIdTable Read(Stream file, string title, string mapPath = "")
    {
        int headerSize, maskOffset, nameOffset;
        switch (title)
        {
            case "haloreach": headerSize = 0xA000; maskOffset = 0x4CC; nameOffset = 0xC0; break;
            case "halo4": case "groundhog": headerSize = 0x1E000; maskOffset = 0x4C4; nameOffset = 0xB8; break;
            default: throw new IOException("Map incident string IDs require Reach, Halo 4 or H2A.");
        }
        if (!file.CanRead || !file.CanSeek || file.Length < headerSize || file.Length > uint.MaxValue)
            throw new IOException("MCC map extent is unsupported.");
        file.Position = 0; byte[] header = new byte[headerSize]; file.ReadExactly(header);
        uint U32(int p) => BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(p, 4));
        if (!header.AsSpan(0, 4).SequenceEqual("daeh"u8) || U32(4) != 13 ||
            !header.AsSpan(headerSize - 4, 4).SequenceEqual("toof"u8) || U32(8) != file.Length)
            throw new IOException("Unsupported MCC cache header/version/extent.");
        string HeaderText(int offset, int capacity)
        {
            var bytes = header.AsSpan(offset, capacity); int end = bytes.IndexOf((byte)0);
            if (end <= 0) throw new IOException("Missing bounded MCC map identity.");
            try { return Utf8.GetString(bytes[..end]); }
            catch (DecoderFallbackException e) { throw new IOException("Invalid MCC map identity encoding.", e); }
        }
        string name = HeaderText(nameOffset, 32), scenario = HeaderText(nameOffset + 32, 256);
        if (name.Length > 31 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new IOException("Invalid MCC map identity.");
        string leaf = scenario.Replace('\\', '/').Split('/').Last();
        if (!leaf.Equals(name, StringComparison.OrdinalIgnoreCase)) throw new IOException("MCC map and scenario identities differ.");
        uint count = U32(0x30), stringBytes = U32(0x38), namespaceCount = U32(0x40);
        if (count < 1 || count > MaximumStrings || stringBytes < 1 || stringBytes > MaximumBytes || namespaceCount < 2 || namespaceCount > 256)
            throw new IOException("MCC string metadata exceeds supported bounds.");
        uint mask = U32(maskOffset), debugBase = U32(maskOffset + 16), debugSize = U32(maskOffset + 20);
        long debugPhysical = unchecked(debugBase + mask);
        if (debugSize == 0 || debugPhysical < headerSize || debugPhysical > file.Length || debugSize > file.Length - debugPhysical)
            throw new IOException("MCC debug section lies outside the map.");
        (long Offset, int Size) Range(uint pointer, long size)
        {
            if (size < 1 || size > MaximumBytes || pointer < debugBase || (ulong)pointer + (ulong)size > (ulong)debugBase + debugSize)
                throw new IOException("MCC string pointer lies outside its debug section.");
            long physical = unchecked(pointer + mask);
            if (physical < debugPhysical || physical > file.Length || size > file.Length - physical ||
                physical - debugPhysical > debugSize || size > debugSize - (physical - debugPhysical))
                throw new IOException("MCC string physical extent is invalid.");
            return (physical, checked((int)size));
        }
        var indexRange = Range(U32(0x3C), checked((long)count * 4));
        var dataRange = Range(U32(0x34), stringBytes);
        var namespaceRange = Range(U32(0x44), checked((long)namespaceCount * 4));
        var ranges = new[] { indexRange, dataRange, namespaceRange }.OrderBy(r => r.Offset).ToArray();
        for (int i = 1; i < ranges.Length; i++)
            if (ranges[i - 1].Offset + ranges[i - 1].Size > ranges[i].Offset) throw new IOException("MCC string metadata regions overlap.");
        byte[] ReadRange((long Offset, int Size) range)
        { file.Position = range.Offset; byte[] data = new byte[range.Size]; file.ReadExactly(data); return data; }
        byte[] indexes = ReadRange(indexRange), strings = ReadRange(dataRange), namespaces = ReadRange(namespaceRange);
        var counts = new int[namespaceCount]; int prefix = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            counts[i] = checked((int)(BinaryPrimitives.ReadUInt32LittleEndian(namespaces.AsSpan(i * 4)) & IndexMask));
            prefix = checked(prefix + counts[i]);
            if (prefix > count) throw new IOException("MCC string namespaces exceed the string table.");
        }
        if ((long)count - prefix + counts[0] > IndexMask + 1L) throw new IOException("MCC map-local string namespace exceeds 19 bits.");
        var keys = new uint[count]; int global = 0;
        for (; global < counts[0]; global++) keys[global] = (uint)global;
        for (int ns = 1; ns < counts.Length; ns++)
            for (uint local = 0; local < counts[ns]; local++) keys[global++] = ((uint)ns << 19) | local;
        for (uint local = (uint)counts[0]; global < keys.Length; global++, local++) keys[global] = local;
        var values = new Dictionary<uint, string>(checked((int)count));
        for (int i = 0; i < keys.Length; i++)
        {
            uint position = BinaryPrimitives.ReadUInt32LittleEndian(indexes.AsSpan(i * 4));
            if (position == uint.MaxValue) continue; // Native missing-string sentinel.
            if (position >= strings.Length || position > 0 && strings[position - 1] != 0)
                throw new IOException("MCC string index is outside a name boundary.");
            int start = (int)position, remaining = strings.Length - start;
            int end = strings.AsSpan(start, Math.Min(remaining, MaximumNameBytes + 1)).IndexOf((byte)0);
            if (end < 0 || end > MaximumNameBytes) throw new IOException("MCC string name is unterminated or oversized.");
            try { values.Add(keys[i], Utf8.GetString(strings, start, end)); }
            catch (DecoderFallbackException e) { throw new IOException("Invalid MCC string-name encoding.", e); }
        }
        using var identity = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        identity.AppendData(Encoding.UTF8.GetBytes("mcc-map-string-ids/v1\0" + title + "\0"));
        identity.AppendData(header); identity.AppendData(indexes); identity.AppendData(strings); identity.AppendData(namespaces);
        return new(title, name, scenario, mapPath, Convert.ToHexString(identity.GetHashAndReset()), values);
    }
}

internal sealed class MapStringIdTable : IReadOnlyDictionary<uint, string>
{
    private readonly ReadOnlyDictionary<uint, string> values;
    internal MapStringIdTable(string title, string mapName, string scenario, string path, string identity, Dictionary<uint, string> names)
    { Title = title; MapName = mapName; Scenario = scenario; MapPath = path; Identity = identity; values = new(names); }
    public string Title { get; }
    public string MapName { get; }
    public string Scenario { get; }
    public string MapPath { get; }
    // Digest covers title, native header/map identity and all metadata consumed.
    public string Identity { get; }
    public int Count => values.Count;
    public IEnumerable<uint> Keys => values.Keys;
    public IEnumerable<string> Values => values.Values;
    public string this[uint key] => values[key & MapStringIds.NameMask];
    public bool ContainsKey(uint key) => values.ContainsKey(key & MapStringIds.NameMask);
    public bool TryGetValue(uint key, out string value) => values.TryGetValue(key & MapStringIds.NameMask, out value!);
    public string? Resolve(uint id) => TryGetValue(id, out string name) ? name : null;
    public IEnumerator<KeyValuePair<uint, string>> GetEnumerator() => values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
