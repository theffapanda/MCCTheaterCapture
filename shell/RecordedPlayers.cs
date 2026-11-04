using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MCCTheaterCapture;

internal sealed record RecordedPlayer(int Index, string Identifier, string Gamertag, long FileOffset);

internal static class RecordedPlayers
{
    // Audited flmh v10.1 roster layout. The eight-byte identifier is a
    // recorded network-player identifier; it is not labelled as an Xbox XUID.
    public static IReadOnlyList<RecordedPlayer> Read(string file)
    {
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var header = new byte[12];
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        for (int chunk = 0; chunk < 64 && input.Position <= 1048576; chunk++)
        {
            long offset = input.Position;
            if (input.Length - offset < 12) throw new IOException("The recording has a truncated chunk header.");
            input.ReadExactly(header);
            string tag = Encoding.ASCII.GetString(header, 0, 4);
            if (offset == 0 && tag != "_blf") throw new IOException("The recording is not a BLF film.");
            uint size = tag == "flmd" ? BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) : BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
            if (size < 12 || size > input.Length - offset) throw new IOException("The recording has an invalid chunk extent.");
            if (tag == "flmh")
            {
                if (size != 0xfc80 || BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8)) != 10 || BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(10)) != 1)
                    throw new IOException("This recording's initial player roster layout is not supported.");
                var entries = new byte[16 * 0xb8];
                input.Position = offset + 0x310;
                input.ReadExactly(entries);
                var result = new List<RecordedPlayer>();
                for (int index = 0; index < 16; index++)
                {
                    int start = index * 0xb8;
                    if (entries[start] == 0) continue;
                    string identifier = Convert.ToHexString(entries.AsSpan(start + 0x10, 8)).ToLowerInvariant();
                    if (identifier == "0000000000000000" || !identifiers.Add(identifier)) throw new IOException("The recording's initial roster has an invalid player identifier.");
                    string name = Encoding.Unicode.GetString(entries, start + 0x18, 32).Split('\0')[0];
                    result.Add(new RecordedPlayer(index, identifier, name, offset + 0x310 + start));
                }
                return result;
            }
            if (tag == "_eof") break;
            input.Position = offset + size;
        }
        throw new IOException("The recording has no supported initial player roster.");
    }
}
