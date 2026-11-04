using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MCCTheaterCapture;

// Film identity is read from the native BLF header, never inferred from a
// filename or the folder chosen in the library. The engine repeats this check.
internal sealed record FilmTitle(string Id, string DisplayName, int HeaderBytes, ushort Major, ushort Minor,
    int MapOffset, int TickOffset, int VariantNameOffset, int VariantNameBytes, string SteamHash, string StoreHash)
{
    public string EngineFolder => Id;
    public string MovieFolderName => Id switch { "haloreach" => "HaloReach", "groundhog" => "Halo2A", "halo4" => "Halo4", _ => "Halo3" };
    public static readonly FilmTitle Halo3 = new("halo3", "Halo 3", 0xfc80, 10, 1, 0x164, 0x98, 0x172c, 64,
        "b209d8454b12dc77e54ccd2c9924ec8d44b8619d21cf98e36ffaf601e67efb63", "fce2d92f06fccef3c4a66933ab280e90ec1ad705cfe71e68107816324d76ff6b");
    public static readonly FilmTitle Reach = new("haloreach", "Halo: Reach", 0x1f520, 0x360e, 2, 0x5ec, 0x11c, 0x1b0c, 256,
        "738dd2d24ea3aea12e1ee9aa4a61094bf116027d42004c35a19e5048608b0894", "f9f39cf058ff28c298cc05964bc40898a57c30d33848fa54d950d8d6c2697e20");
    public static readonly FilmTitle Halo4 = new("halo4", "Halo 4", 0x2d4e0, 0x4c8f, 1, 0x1d204, 0x11c, 0x65c, 256,
        "7c53e7d5bc9848545a1b70e2768242479336fba1b7630d7ab955f7fd0c34fa84", "5767cd564c1e8e8d012d002a8de8e92960a3de46442399ed054e3c4ef44aa496");
    public static readonly FilmTitle Halo2A = new("groundhog", "Halo 2 Anniversary", 0x2d4e0, 0x5530, 1, 0x1d204, 0x11c, 0x65c, 256,
        "e51dda124799246a856af47d2888e057394424a0c9c8e92ae9099896164e6190", "905b7ae23a2e12f99ba68f5c0b18ee50eee537cb90f002652f1757828bccff2c");
    public static IReadOnlyList<FilmTitle> All { get; } = new[] { Halo3, Reach, Halo4, Halo2A };

    public static FilmTitle FromId(string id) => All.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new IOException("Unsupported Theater title. Choose Halo 3, Reach, Halo 4 or H2A multiplayer.");

    public static FilmTitle Detect(string film)
    {
        using var input = File.OpenRead(film);
        return ReadHeader(input).Title;
    }

    internal sealed record Header(FilmTitle Title, long Offset, byte[] Bytes);
    internal static Header ReadHeader(Stream input)
    {
        if (!input.CanRead || !input.CanSeek) throw new IOException("A readable, seekable Theater film is required.");
        long offset = 0;
        byte[] chunk = new byte[12];
        for (int count = 0; count < 64 && offset < 1048576; count++)
        {
            if (offset > input.Length - chunk.Length) break;
            input.Position = offset; input.ReadExactly(chunk);
            string tag = Encoding.ASCII.GetString(chunk, 0, 4);
            if (offset == 0 && tag != "_blf") throw new IOException("Not a native BLF Theater film.");
            uint size = tag == "flmd" ? BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4)) : BinaryPrimitives.ReadUInt32BigEndian(chunk.AsSpan(4));
            if (size < 12 || size > input.Length - offset) throw new IOException("Invalid Theater film chunk extent.");
            if (tag == "flmh")
            {
                ushort major = BinaryPrimitives.ReadUInt16BigEndian(chunk.AsSpan(8));
                ushort minor = BinaryPrimitives.ReadUInt16BigEndian(chunk.AsSpan(10));
                var title = All.FirstOrDefault(t => t.HeaderBytes == size && t.Major == major && t.Minor == minor)
                    ?? throw new IOException("Unsupported Theater film header or version.");
                byte[] bytes = new byte[title.HeaderBytes]; chunk.CopyTo(bytes, 0); input.ReadExactly(bytes.AsSpan(12));
                return new(title, offset, bytes);
            }
            if (tag is "flmd" or "_eof") break;
            offset += size;
        }
        throw new IOException("A supported native Theater film header was not found.");
    }

    // Preferences historically store a title's maps folder. Also accept the
    // combined installation root and Xbox's outer folder containing Content.
    public static string InstallationRoot(string selection)
    {
        string path = Path.GetFullPath(selection).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Path.GetFileName(path).Equals("maps", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(path);
            if (parent != null && All.Any(t => parent.Name.Equals(t.Id, StringComparison.OrdinalIgnoreCase)) && parent.Parent != null)
                return parent.Parent.FullName;
        }
        if (All.Any(t => File.Exists(Path.Combine(path, t.Id, t.Id + ".dll")))) return path;
        string content = Path.Combine(path, "Content");
        if (All.Any(t => File.Exists(Path.Combine(content, t.Id, t.Id + ".dll")))) return content;
        return path;
    }

    public string MapsFolder(string selection) => Path.Combine(InstallationRoot(selection), Id, "maps");

    public bool HasSupportedEngine(string selection)
    {
        string root = InstallationRoot(selection), engine = Path.Combine(root, Id, Id + ".dll");
        try
        {
            using var input = File.OpenRead(engine);
            string hash = Convert.ToHexString(SHA256.HashData(input));
            return hash.Equals(SteamHash, StringComparison.OrdinalIgnoreCase) || hash.Equals(StoreHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    public string? InstallationIssue(string selection)
    {
        string root = InstallationRoot(selection);
        if (!Directory.Exists(MapsFolder(root))) return $"The {DisplayName} maps are not installed under the selected MCC folder.";
        if (!HasSupportedEngine(root)) return $"The installed {DisplayName} engine build is unsupported or unavailable.";
        foreach (string dependency in new[] { "bink2w64.dll", "XAudio2_9Redist.dll" })
            if (!File.Exists(Path.Combine(root, "mcc", "binaries", "win64", dependency))) return "The selected folder is missing MCC's shared native dependencies.";
        if (!File.Exists(Path.Combine(root, "mcc", "content", "paks", "MCC-WindowsNoEditor.pak"))) return "The selected folder is missing MCC's shared font assets.";
        return null;
    }
}
