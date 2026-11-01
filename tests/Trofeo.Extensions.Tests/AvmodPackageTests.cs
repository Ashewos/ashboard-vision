using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Trofeo.Extensions;

namespace Trofeo.Extensions.Tests;

public sealed class AvmodPackageTests : IDisposable
{
    private const string ManifestJson = """
        {
          "id": "ashewos.demo",
          "name": "Démo",
          "description": "Un module de démonstration.",
          "author": "Ashewos",
          "version": "1.0.0",
          "sdk": "1.0",
          "entry": "Demo.dll",
          "permissions": ["internet"],
          "minSize": { "columns": 2, "rows": 1 }
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "avmod-tests-" + Guid.NewGuid().ToString("N"));

    public AvmodPackageTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string InRoot(string name) => Path.Combine(_root, name);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>Écrit une archive ZIP avec les entrées données, noms écrits tels quels (même absolus ou avec « .. »).</summary>
    private string MakeZip(string fileName, params (string Name, byte[] Content)[] entries)
    {
        var path = InRoot(fileName);
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            using var entryStream = entry.Open();
            entryStream.Write(content);
        }

        return path;
    }

    private string MakeValidZip(string fileName = "valid.avmod") =>
        MakeZip(fileName, ("manifest.json", Bytes(ManifestJson)), ("Demo.dll", Bytes("dll")), ("lib/Other.dll", Bytes("other")));

    private static void WriteEntry(ZipArchive zip, string name, byte[] content)
    {
        using var entryStream = zip.CreateEntry(name).Open();
        entryStream.Write(content);
    }

    // ---- Inspect ----

    [Fact]
    public void Inspect_ValidPackage_ReturnsManifest()
    {
        var result = AvmodPackage.Inspect(MakeValidZip());

        Assert.Empty(result.Errors);
        var manifest = Assert.IsType<ModuleManifest>(result.Manifest);
        Assert.Equal("ashewos.demo", manifest.Id);
        Assert.Equal("Demo.dll", manifest.Entry);
        Assert.Equal(Bytes(ManifestJson).Length + 3 + 5, result.UncompressedBytes);
    }

    [Fact]
    public void Inspect_ManifestWithByteOrderMark_IsRead()
    {
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes(ManifestJson)).ToArray();
        var path = MakeZip("bom.avmod", ("manifest.json", withBom), ("Demo.dll", Bytes("dll")));

        var result = AvmodPackage.Inspect(path);

        Assert.Empty(result.Errors);
        Assert.NotNull(result.Manifest);
    }

    [Fact]
    public void Inspect_NoManifestAtRoot_IsRefused()
    {
        var path = MakeZip("nomanifest.avmod", ("sub/manifest.json", Bytes(ManifestJson)), ("Demo.dll", Bytes("dll")));

        var result = AvmodPackage.Inspect(path);

        Assert.Null(result.Manifest);
        Assert.Contains(result.Errors, e => e.Contains("manifest.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_InvalidManifest_ReturnsItsErrors()
    {
        var path = MakeZip("badmanifest.avmod", ("manifest.json", Bytes("{ \"id\": \"clock\" }")), ("Demo.dll", Bytes("dll")));

        var result = AvmodPackage.Inspect(path);

        Assert.Null(result.Manifest);
        Assert.Contains(result.Errors, e => e.Contains("auteur.module", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_ManifestNotUtf8_IsRefusedWithoutException()
    {
        var path = MakeZip("latin1.avmod", ("manifest.json", new byte[] { 0x7B, 0xE9, 0x7D }), ("Demo.dll", Bytes("dll")));

        var result = AvmodPackage.Inspect(path);

        Assert.Null(result.Manifest);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Inspect_EntryDllMissing_IsRefused()
    {
        var path = MakeZip("nodll.avmod", ("manifest.json", Bytes(ManifestJson)), ("lib/Demo.dll", Bytes("dll")));

        var result = AvmodPackage.Inspect(path);

        Assert.Null(result.Manifest);
        Assert.Contains(result.Errors, e => e.Contains("Demo.dll", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_NotAZipFile_IsRefusedWithoutException()
    {
        var path = InRoot("garbage.avmod");
        File.WriteAllText(path, "ceci n'est pas une archive");

        var result = AvmodPackage.Inspect(path);

        Assert.Null(result.Manifest);
        Assert.Single(result.Errors);
    }

    [Theory]
    [InlineData("..\\evil.dll")]
    [InlineData("../evil.dll")]
    [InlineData("a/../../evil.dll")]
    [InlineData("C:\\evil.dll")]
    [InlineData("C:evil.dll")]
    [InlineData("/evil.dll")]
    [InlineData("\\evil.dll")]
    [InlineData("\\\\server\\share\\evil.dll")]
    [InlineData("Demo.dll:stream")]
    [InlineData("a//b.dll")]
    [InlineData("CON.dll")]
    [InlineData("lib/nul")]
    [InlineData("trailing.")]
    [InlineData("bad|name.dll")]
    public void Inspect_ZipSlipEntry_IsRefused(string entryName)
    {
        var path = MakeZip("evil.avmod", ("manifest.json", Bytes(ManifestJson)), ("Demo.dll", Bytes("dll")), (entryName, Bytes("x")));

        var result = AvmodPackage.Inspect(path);

        Assert.Null(result.Manifest);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Inspect_DuplicateNamesDifferingByCase_IsRefused()
    {
        var path = MakeZip("dups.avmod", ("manifest.json", Bytes(ManifestJson)), ("Demo.dll", Bytes("dll")), ("lib/A.dll", Bytes("1")), ("lib/a.DLL", Bytes("2")));

        var result = AvmodPackage.Inspect(path);

        Assert.Null(result.Manifest);
        Assert.Contains(result.Errors, e => e.Contains("même nom", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_FileAndFolderWithSameName_IsRefused()
    {
        var path = MakeZip("clash.avmod", ("manifest.json", Bytes(ManifestJson)), ("Demo.dll", Bytes("dll")), ("lib", Bytes("1")), ("lib/x.dll", Bytes("2")));

        var result = AvmodPackage.Inspect(path);

        Assert.Null(result.Manifest);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Inspect_DirectoryEntries_AreAllowed()
    {
        var path = MakeZip("dirs.avmod", ("manifest.json", Bytes(ManifestJson)), ("Demo.dll", Bytes("dll")), ("lib/", []), ("lib\\", []), ("lib/x.txt", Bytes("x")));

        var result = AvmodPackage.Inspect(path);

        Assert.Empty(result.Errors);
        Assert.NotNull(result.Manifest);
    }

    [Fact]
    public void Inspect_TooManyEntries_IsRefused()
    {
        var path = InRoot("many.avmod");
        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "manifest.json", Bytes(ManifestJson));
            WriteEntry(zip, "Demo.dll", Bytes("dll"));
            for (var i = 0; i < AvmodPackage.MaxEntries - 1; i++)
            {
                zip.CreateEntry($"f/{i}.txt");
            }
        }

        var result = AvmodPackage.Inspect(path);

        Assert.Null(result.Manifest);
        Assert.Contains(result.Errors, e => e.Contains("2000 au plus", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_DirectoryEntriesCountTowardTheLimit()
    {
        var path = InRoot("manydirs.avmod");
        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "manifest.json", Bytes(ManifestJson));
            WriteEntry(zip, "Demo.dll", Bytes("dll"));
            for (var i = 0; i < AvmodPackage.MaxEntries - 1; i++)
            {
                zip.CreateEntry($"d{i}/");
            }
        }

        var result = AvmodPackage.Inspect(path);

        Assert.Null(result.Manifest);
        Assert.Contains(result.Errors, e => e.Contains("2000 au plus", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_ExactlyTheEntryLimit_IsAccepted()
    {
        var path = InRoot("exact.avmod");
        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteEntry(zip, "manifest.json", Bytes(ManifestJson));
            WriteEntry(zip, "Demo.dll", Bytes("dll"));
            for (var i = 0; i < AvmodPackage.MaxEntries - 2; i++)
            {
                zip.CreateEntry($"d{i}/");
            }
        }

        var result = AvmodPackage.Inspect(path);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Inspect_HugeEntryName_IsRefusedQuickly()
    {
        // 65 000 caractères en 32 500 niveaux : le traitement des dossiers parents ne doit pas devenir quadratique.
        var path = MakeZip("huge.avmod", ("manifest.json", Bytes(ManifestJson)), ("Demo.dll", Bytes("dll")), (string.Concat(Enumerable.Repeat("a/", 32_500)) + "x.dll", Bytes("x")));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = AvmodPackage.Inspect(path);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"trop lent : {watch.Elapsed}");
        Assert.Null(result.Manifest);
        Assert.Contains(result.Errors, e => e.Contains("200 caractères", StringComparison.Ordinal));
        Assert.All(result.Errors, e => Assert.True(e.Length < 400));
    }

    [Fact]
    public void Inspect_PathOfTheMaximumLength_IsAcceptedAndOneMoreIsRefused()
    {
        var atLimit = "lib/" + new string('a', AvmodPackage.MaxPathLength - 4);
        var accepted = MakeZip("len-ok.avmod", ("manifest.json", Bytes(ManifestJson)), ("Demo.dll", Bytes("dll")), (atLimit, Bytes("x")));
        var refused = MakeZip("len-ko.avmod", ("manifest.json", Bytes(ManifestJson)), ("Demo.dll", Bytes("dll")), (atLimit + "b", Bytes("x")));

        Assert.Equal(AvmodPackage.MaxPathLength, atLimit.Length);
        Assert.Empty(AvmodPackage.Inspect(accepted).Errors);
        Assert.Contains(AvmodPackage.Inspect(refused).Errors, e => e.Contains("200 caractères", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_PathOfTheMaximumDepth_IsAcceptedAndOneMoreIsRefused()
    {
        var atLimit = string.Join('/', Enumerable.Repeat("d", AvmodPackage.MaxPathDepth - 1)) + "/f.txt";
        var tooDeep = "d/" + atLimit;
        var accepted = MakeZip("deep-ok.avmod", ("manifest.json", Bytes(ManifestJson)), ("Demo.dll", Bytes("dll")), (atLimit, Bytes("x")));
        var refused = MakeZip("deep-ko.avmod", ("manifest.json", Bytes(ManifestJson)), ("Demo.dll", Bytes("dll")), (tooDeep, Bytes("x")));

        Assert.Equal(AvmodPackage.MaxPathDepth, atLimit.Split('/').Length);
        Assert.Empty(AvmodPackage.Inspect(accepted).Errors);
        Assert.Contains(AvmodPackage.Inspect(refused).Errors, e => e.Contains("16 niveaux", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_TooLarge_IsRefused()
    {
        var path = MakeBigZip("big.avmod", AvmodPackage.MaxUncompressedBytes + 1024 * 1024);

        var result = AvmodPackage.Inspect(path);

        Assert.Null(result.Manifest);
        Assert.True(result.UncompressedBytes > AvmodPackage.MaxUncompressedBytes);
        Assert.Contains(result.Errors, e => e.Contains("50 Mio", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_JustUnderTheLimit_IsAccepted()
    {
        var path = MakeBigZip("almost.avmod", AvmodPackage.MaxUncompressedBytes - 1024 * 1024);

        var result = AvmodPackage.Inspect(path);

        Assert.Empty(result.Errors);
        Assert.NotNull(result.Manifest);
    }

    /// <summary>Une archive qui contient en plus un fichier de <paramref name="bigLength"/> octets (des zéros : elle reste petite).</summary>
    private string MakeBigZip(string fileName, long bigLength)
    {
        var path = InRoot(fileName);
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        WriteEntry(zip, "manifest.json", Bytes(ManifestJson));
        WriteEntry(zip, "Demo.dll", Bytes("dll"));
        var big = zip.CreateEntry("big.bin", CompressionLevel.Optimal);
        using var bigStream = big.Open();
        var chunk = new byte[1024 * 1024];
        for (long written = 0; written < bigLength; written += chunk.Length)
        {
            bigStream.Write(chunk, 0, (int)Math.Min(chunk.Length, bigLength - written));
        }

        return path;
    }

    [Fact]
    public void CopyLimited_StreamLargerThanLimit_Throws()
    {
        using var source = new MemoryStream(new byte[1000]);
        using var destination = new MemoryStream();
        long total = 0;

        Assert.Throws<InvalidDataException>(() => AvmodPackage.CopyLimited(source, destination, ref total, 999, "trop gros"));
    }

    [Fact]
    public void CopyLimited_CountsAcrossCalls()
    {
        long total = 0;
        AvmodPackage.CopyLimited(new MemoryStream(new byte[600]), new MemoryStream(), ref total, 1000, "trop gros");

        Assert.Equal(600, total);
        Assert.Throws<InvalidDataException>(() =>
        {
            var again = total;
            AvmodPackage.CopyLimited(new MemoryStream(new byte[600]), new MemoryStream(), ref again, 1000, "trop gros");
        });
    }

    [Fact]
    public void ExtractTo_HeadersUnderstatingTheSize_NeverWritesMoreThanTheLimit()
    {
        // Une archive dont l'en-tête dit 100 octets alors que le contenu en fait 51 Mio.
        var path = MakeBigZip("liar.avmod", AvmodPackage.MaxUncompressedBytes + 1024 * 1024);
        var bytes = File.ReadAllBytes(path);
        PatchDeclaredSize(bytes, "big.bin", 100);
        File.WriteAllBytes(path, bytes);
        var target = InRoot("liar-target");

        try
        {
            AvmodPackage.ExtractTo(path, target);
        }
        catch (InvalidDataException)
        {
        }

        var written = Directory.Exists(target)
            ? Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            : 0;
        Assert.True(written <= AvmodPackage.MaxUncompressedBytes);
        Assert.Empty(Directory.GetDirectories(_root, "liar-target.tmp-*"));
    }

    /// <summary>Réécrit la taille décompressée annoncée par l'entrée <paramref name="name"/> (dans les deux en-têtes).</summary>
    private static void PatchDeclaredSize(byte[] zip, string name, uint size)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        for (var i = 0; i < zip.Length - 46; i++)
        {
            if (zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 0x01 && zip[i + 3] == 0x02
                && NameAt(zip, i + 46, BitConverter.ToUInt16(zip, i + 28), nameBytes))
            {
                BitConverter.GetBytes(size).CopyTo(zip, i + 24);
            }
            else if (zip[i] == 0x50 && zip[i + 1] == 0x4B && zip[i + 2] == 0x03 && zip[i + 3] == 0x04
                && NameAt(zip, i + 30, BitConverter.ToUInt16(zip, i + 26), nameBytes))
            {
                BitConverter.GetBytes(size).CopyTo(zip, i + 22);
            }
        }
    }

    private static bool NameAt(byte[] zip, int offset, int length, byte[] name) =>
        length == name.Length && zip.AsSpan(offset, length).SequenceEqual(name);

    // ---- ExtractTo ----

    [Fact]
    public void ExtractTo_ValidPackage_WritesAllFiles()
    {
        var target = InRoot("out/ashewos.demo/1.0.0");

        AvmodPackage.ExtractTo(MakeValidZip(), target);

        Assert.Equal("dll", File.ReadAllText(Path.Combine(target, "Demo.dll")));
        Assert.Equal("other", File.ReadAllText(Path.Combine(target, "lib", "Other.dll")));
        Assert.True(File.Exists(Path.Combine(target, "manifest.json")));
        Assert.Empty(Directory.GetDirectories(InRoot("out/ashewos.demo"), "*.tmp-*"));
    }

    [Fact]
    public void ExtractTo_RefusedPackage_WritesNothing()
    {
        var parent = InRoot("parent");
        Directory.CreateDirectory(parent);
        var path = MakeZip("evil.avmod", ("manifest.json", Bytes(ManifestJson)), ("Demo.dll", Bytes("dll")), ("..\\evil.dll", Bytes("x")));

        Assert.Throws<InvalidDataException>(() => AvmodPackage.ExtractTo(path, Path.Combine(parent, "target")));

        Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
        Assert.False(File.Exists(InRoot("evil.dll")));
    }

    [Fact]
    public void ExtractTo_RefusedPackage_DoesNotCreateMissingParents()
    {
        var path = MakeZip("evil.avmod", ("manifest.json", Bytes(ManifestJson)), ("/evil.dll", Bytes("x")));

        Assert.Throws<InvalidDataException>(() => AvmodPackage.ExtractTo(path, InRoot("a/b/target")));

        Assert.False(Directory.Exists(InRoot("a")));
    }

    [Fact]
    public void ExtractTo_ExistingTarget_Throws()
    {
        var target = InRoot("existing");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");

        Assert.Throws<IOException>(() => AvmodPackage.ExtractTo(MakeValidZip(), target));

        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
        Assert.Single(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public void ExtractTo_FailureAfterWriting_RemovesTheTemporaryDirectory()
    {
        // Le premier fichier est écrit, puis la cible du renommage final apparaît : l'échec survient après l'écriture.
        var path = MakeValidZip();
        var parent = InRoot("late");
        var target = Path.Combine(parent, "target");

        AvmodPackage.AfterExtractionForTests = () => Directory.CreateDirectory(target);
        try
        {
            Assert.Throws<IOException>(() => AvmodPackage.ExtractTo(path, target));
        }
        finally
        {
            AvmodPackage.AfterExtractionForTests = null;
        }

        Assert.Empty(Directory.GetDirectories(parent, "*.tmp-*"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    // ---- ContentHash ----

    private string MakeTree(string name, params (string Path, string Content)[] files)
    {
        var dir = InRoot(name);
        Directory.CreateDirectory(dir);
        foreach (var (relative, content) in files)
        {
            var full = Path.Combine(dir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        return dir;
    }

    [Fact]
    public void Compute_SameContentDifferentOrder_SameHash()
    {
        var a = MakeTree("a", ("one.txt", "1"), ("sub/two.txt", "2"), ("three.txt", "3"));
        var b = MakeTree("b", ("three.txt", "3"), ("sub/two.txt", "2"), ("one.txt", "1"));

        Assert.Equal(ContentHash.Compute(a), ContentHash.Compute(b));
    }

    [Fact]
    public void Compute_OneByteChanged_DifferentHash()
    {
        var a = MakeTree("a", ("one.txt", "hello"), ("two.txt", "2"));
        var b = MakeTree("b", ("one.txt", "hellp"), ("two.txt", "2"));

        Assert.NotEqual(ContentHash.Compute(a), ContentHash.Compute(b));
    }

    [Fact]
    public void Compute_RenamedFile_DifferentHash()
    {
        var a = MakeTree("a", ("one.txt", "1"));
        var b = MakeTree("b", ("uno.txt", "1"));

        Assert.NotEqual(ContentHash.Compute(a), ContentHash.Compute(b));
    }

    [Fact]
    public void Compute_ExtraFile_DifferentHash()
    {
        var a = MakeTree("a", ("one.txt", "1"));
        var b = MakeTree("b", ("one.txt", "1"), ("sub/extra.txt", ""));

        Assert.NotEqual(ContentHash.Compute(a), ContentHash.Compute(b));
    }

    [Fact]
    public void Compute_HiddenFile_IsIncluded()
    {
        var a = MakeTree("a", ("one.txt", "1"));
        var b = MakeTree("b", ("one.txt", "1"), ("secret.dll", "x"));
        File.SetAttributes(Path.Combine(b, "secret.dll"), FileAttributes.Hidden | FileAttributes.System);

        Assert.NotEqual(ContentHash.Compute(a), ContentHash.Compute(b));
    }

    [Fact]
    public void Compute_EmptyDirectory_IsHashOfNothing()
    {
        var empty = InRoot("empty");
        Directory.CreateDirectory(empty);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData([])), ContentHash.Compute(empty));
    }

    [Fact]
    public void Compute_DirectoryAndStreamList_AgreeAndFollowTheDocumentedFormat()
    {
        var dir = MakeTree("a", ("Lib/Two.TXT", "deux"), ("one.txt", "un"));
        var list = new List<(string, Stream)>
        {
            ("one.txt", new MemoryStream(Bytes("un"))),
            ("LIB\\two.txt", new MemoryStream(Bytes("deux"))),
        };

        var expectedLines = new[]
        {
            "lib/two.txt\0" + Convert.ToHexStringLower(SHA256.HashData(Bytes("deux"))),
            "one.txt\0" + Convert.ToHexStringLower(SHA256.HashData(Bytes("un"))),
        };
        var expected = Convert.ToHexStringLower(SHA256.HashData(Bytes(string.Concat(expectedLines.Select(l => l + "\n")))));

        Assert.Equal(expected, ContentHash.Compute(dir));
        Assert.Equal(expected, ContentHash.Compute(list));
    }

    [Fact]
    public void Compute_StreamListWithPathsDifferingByCase_Throws()
    {
        var list = new List<(string, Stream)> { ("A.txt", new MemoryStream()), ("a.txt", new MemoryStream()) };

        Assert.Throws<ArgumentException>(() => ContentHash.Compute(list));
    }

    // ---- AvmodWriter ----

    [Fact]
    public void Pack_ThenInspect_RoundTrips()
    {
        var module = MakeTree("module", ("manifest.json", ManifestJson), ("Demo.dll", "dll"), ("lib/Other.dll", "other"), ("icon.png", "png"));
        var package = InRoot("out/ashewos.demo-1.0.0.avmod");
        var before = ContentHash.Compute(module);

        AvmodWriter.Pack(module, package);
        var inspection = AvmodPackage.Inspect(package);
        var target = InRoot("installed");
        AvmodPackage.ExtractTo(package, target);

        Assert.Empty(inspection.Errors);
        Assert.Equal("ashewos.demo", inspection.Manifest!.Id);
        Assert.Equal(before, ContentHash.Compute(target));
        Assert.Empty(Directory.GetFiles(InRoot("out"), "*.tmp-*"));
    }

    [Fact]
    public void Pack_OutputInsideTheModuleFolder_IsNotPackedInItself()
    {
        var module = MakeTree("module", ("manifest.json", ManifestJson), ("Demo.dll", "dll"));
        var package = Path.Combine(module, "dist", "m.avmod");

        // Le premier paquet existe quand le second est fait : c'est lui que l'énumération du dossier rencontre.
        AvmodWriter.Pack(module, package);
        Assert.True(File.Exists(package));
        AvmodWriter.Pack(module, package);
        using var zip = ZipFile.OpenRead(package);

        Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith(".avmod", StringComparison.Ordinal));
        Assert.Equal(["Demo.dll", "manifest.json"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Pack_ExistingOutput_IsReplaced()
    {
        var module = MakeTree("module", ("manifest.json", ManifestJson), ("Demo.dll", "dll"));
        var package = InRoot("m.avmod");
        File.WriteAllText(package, "old");

        AvmodWriter.Pack(module, package);

        Assert.Empty(AvmodPackage.Inspect(package).Errors);
    }

    [Fact]
    public void Pack_ModuleWithoutManifest_ThrowsAndLeavesNoFile()
    {
        var module = MakeTree("module", ("Demo.dll", "dll"));
        var package = InRoot("m.avmod");

        Assert.Throws<InvalidDataException>(() => AvmodWriter.Pack(module, package));

        Assert.Empty(Directory.GetFiles(_root, "m.avmod*"));
    }

    [Fact]
    public void Pack_MissingFolder_Throws()
    {
        Assert.Throws<DirectoryNotFoundException>(() => AvmodWriter.Pack(InRoot("nope"), InRoot("m.avmod")));
    }
}
