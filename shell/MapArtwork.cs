using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MCCTheaterCapture;

// Read MCC's original BC1 map thumbnails. No editing-kit dependency or game writes.
internal static class MapArtwork
{
    internal const string Host = "mcc-artwork.local";

    internal static Dictionary<string, string> Load(string maps, string cache)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFileName(maps.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Equals("maps", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(maps)?.Parent?.FullName : Path.GetFullPath(maps);
        if (root == null) return result;
        if (Directory.Exists(Path.Combine(root, "Content", "data"))) root = Path.Combine(root, "Content");
        var pack = Path.Combine(root, "data", "ui", "texturepacks", "levelstexturepack");
        if (!File.Exists(pack + ".perm.bin") || !File.Exists(pack + ".temp.bin")) return result;
        if (new FileInfo(pack + ".perm.bin").Length > 4 * 1024 * 1024) throw new IOException("Unsupported MCC artwork metadata size.");
        var metadata = File.ReadAllBytes(pack + ".perm.bin");
        using var pixels = File.OpenRead(pack + ".temp.bin");
        var identity = Convert.ToHexString(SHA256.HashData(metadata))[..16].ToLowerInvariant();
        var folder = Path.Combine(cache, identity);
        Directory.CreateDirectory(folder);
        // Fixed-size texture records in the installed MCC level pack.
        for (int record = 0; record + 480 <= metadata.Length; record += 480)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(metadata.AsSpan(record)) != 0xcdbfa090 ||
                BinaryPrimitives.ReadUInt32LittleEndian(metadata.AsSpan(record + 4)) != 464) continue;
            int nameOffset = record + 68;
            string name = Encoding.ASCII.GetString(metadata, nameOffset, 48).Split('\0')[0];
            string? prefix = new[] { "H3MP_", "HREACHMP_", "REACHFIREFIGHT_", "H4MP_", "H2MPA_" }.FirstOrDefault(p => name.StartsWith(p, StringComparison.Ordinal));
            if (prefix == null) continue;
            // Reach omits the underscore before LARGE, including Firefight.
            int suffix = name.EndsWith("_LARGE", StringComparison.Ordinal) ? 6
                : (prefix is "HREACHMP_" or "REACHFIREFIGHT_") && name.EndsWith("LARGE", StringComparison.Ordinal) ? 5 : 0;
            if (suffix == 0) continue;
            string key = name[prefix.Length..^suffix];
            if (key.Length == 0 || key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_'))) continue;
            string title = prefix switch { "HREACHMP_" or "REACHFIREFIGHT_" => "haloreach", "H4MP_" => "halo4", "H2MPA_" => "groundhog", _ => "halo3" };
            // Halo 3 retains its existing cache names and lookup keys. New
            // titles use a namespace so shared display names cannot collide.
            string cacheName = title == "halo3" ? key : title + "-" + key;
            int width = BinaryPrimitives.ReadUInt16LittleEndian(metadata.AsSpan(nameOffset + 48));
            int height = BinaryPrimitives.ReadUInt16LittleEndian(metadata.AsSpan(nameOffset + 50));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(metadata.AsSpan(nameOffset + 68));
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(metadata.AsSpan(nameOffset + 76));
            if (width < 4 || height < 4 || width > 2048 || height > 2048 ||
                size != ((width + 3) / 4) * ((height + 3) / 4) * 8 || (long)offset + size > pixels.Length) continue;
            string output = Path.Combine(folder, cacheName + ".png");
            if (!File.Exists(output))
            {
                pixels.Position = offset;
                var data = new byte[(int)size];
                pixels.ReadExactly(data);
                var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
                    DecodeBc1(data, width, height), width * 4);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                // Publish only complete cache entries; interrupted writes are retried.
                var temporary = output + ".tmp";
                try { using (var stream = File.Create(temporary)) encoder.Save(stream); File.Move(temporary, output, true); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            result[title == "halo3" ? key : title + ":" + key] = $"https://{Host}/{identity}/{cacheName}.png";
        }
        return result;
    }

    internal static byte[] DecodeBc1(ReadOnlySpan<byte> source, int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 2048 || height > 2048 ||
            source.Length != ((width + 3) / 4) * ((height + 3) / 4) * 8)
            throw new InvalidDataException("Invalid BC1 map thumbnail.");
        var output = new byte[width * height * 4];
        Span<byte> palette = stackalloc byte[16];
        int offset = 0;
        for (int y = 0; y < height; y += 4)
        for (int x = 0; x < width; x += 4)
        {
            ushort a = BinaryPrimitives.ReadUInt16LittleEndian(source[offset..]);
            ushort b = BinaryPrimitives.ReadUInt16LittleEndian(source[(offset + 2)..]);
            // Palette uses BGRA to match WPF's pixel format.
            for (int color = 0; color < 2; color++)
            {
                ushort value = color == 0 ? a : b;
                int blue = value & 31, green = (value >> 5) & 63, red = (value >> 11) & 31;
                palette[color * 4] = (byte)((blue << 3) | (blue >> 2));
                palette[color * 4 + 1] = (byte)((green << 2) | (green >> 4));
                palette[color * 4 + 2] = (byte)((red << 3) | (red >> 2));
                palette[color * 4 + 3] = 255;
            }
            for (int c = 0; c < 3; c++)
            {
                palette[8 + c] = (byte)(a > b ? (2 * palette[c] + palette[4 + c]) / 3 : (palette[c] + palette[4 + c]) / 2);
                palette[12 + c] = (byte)(a > b ? (palette[c] + 2 * palette[4 + c]) / 3 : 0);
            }
            palette[11] = 255; palette[15] = (byte)(a > b ? 255 : 0);
            uint indices = BinaryPrimitives.ReadUInt32LittleEndian(source[(offset + 4)..]);
            for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
            {
                int color = (int)((indices >> (2 * (row * 4 + column))) & 3);
                if (y + row < height && x + column < width)
                    palette.Slice(color * 4, 4).CopyTo(output.AsSpan(((y + row) * width + x + column) * 4, 4));
            }
            offset += 8;
        }
        return output;
    }
}
