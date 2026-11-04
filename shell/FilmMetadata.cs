using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MCCTheaterCapture;

internal sealed class FilmGameMetadata(FilmTitle title, string map, int totalTicks, string? gameType, string? gameVariantName)
{
    public FilmTitle Title { get; } = title;
    public string Map { get; } = map;
    public int TotalTicks { get; } = totalTicks;
    public string? GameType { get; } = gameType;
    public string? GameVariantName { get; } = gameVariantName;
}

internal static class FilmMetadata
{
    private static readonly Encoding VariantEncoding = new UnicodeEncoding(false, false, true);
    public static FilmGameMetadata? Read(string file)
    {
        try { using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return Read(input); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static FilmGameMetadata? Read(Stream stream)
    {
        try
        {
            var header = FilmTitle.ReadHeader(stream); var title = header.Title; byte[] h = header.Bytes;
            int ticks = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(title.TickOffset));
            if (ticks is < 1 or > 2592000) return null;
            string resource = Encoding.ASCII.GetString(h, title.MapOffset, 0x54).Split('\0')[0];
            string[] parts = resource.Replace('\\', '/').Split('/');
            if (parts.Length != 3 || !parts[0].Equals(title.EngineFolder, StringComparison.OrdinalIgnoreCase) || !parts[1].Equals("maps", StringComparison.OrdinalIgnoreCase)) return null;
            string map = parts[2];
            if (map.Length == 0 || map.Any(c => !(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'))) return null;
            // Megalo-era title variants use a different structure and mode enum.
            // Preserve their native names; do not label a CTF film as Slayer by
            // interpreting its first variant word with Halo 3's mode table.
            string? mode = title == FilmTitle.Halo3 ? GameTypeName(BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(0x1718))) : null;
            string? name = VariantName(h.AsSpan(title.VariantNameOffset, title.VariantNameBytes));
            return new(title, map, ticks, mode, name);
        }
        catch (Exception error) when (error is IOException or ArgumentException) { return null; }
    }

    private static string? GameTypeName(uint type) => type switch
    {
        1 => "Capture the Flag", 2 => "Slayer", 3 => "Oddball", 4 => "King of the Hill", 5 => "Forge", 6 => "VIP",
        7 => "Juggernaut", 8 => "Territories", 9 => "Assault", 10 => "Infection", 12 => "Gun Game", _ => null
    };
    private static string? VariantName(ReadOnlySpan<byte> bytes)
    {
        int count = 0;
        while (count + 1 < bytes.Length && (bytes[count] != 0 || bytes[count + 1] != 0)) count += 2;
        try
        {
            string value = VariantEncoding.GetString(bytes[..count]);
            return string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) ? null : value;
        }
        catch (DecoderFallbackException) { return null; }
    }
}
