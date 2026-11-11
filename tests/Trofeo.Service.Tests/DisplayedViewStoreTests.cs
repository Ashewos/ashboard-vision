using Microsoft.Extensions.Time.Testing;

namespace Trofeo.Display.Tests;

public sealed class DisplayedViewStoreTests : IDisposable
{
    private static readonly Guid A = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid B = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid C = new("cccccccc-0000-0000-0000-000000000003");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ashboard-view-store-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero));

    private string FilePath => Path.Combine(_directory, "vue-affichee.json");

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // un fichier encore ouvert ne fait pas échouer le test
        }
    }

    private DisplayedViewStore Store() => new(FilePath, _time);

    [Fact]
    public void Save_TwiceInOneSecond_WritesOnceThenTheLastValue()
    {
        var store = Store();

        store.Save(A);
        Assert.Equal(A, Store().Load()); // la première écriture est immédiate

        _time.Advance(TimeSpan.FromMilliseconds(100));
        store.Save(B);
        _time.Advance(TimeSpan.FromMilliseconds(100));
        store.Save(C);
        Assert.Equal(A, Store().Load()); // les suivantes attendent la fin de la seconde

        _time.Advance(TimeSpan.FromMilliseconds(800));
        Assert.Equal(C, Store().Load()); // la dernière valeur gagne, B n'a jamais été écrite
    }

    [Fact]
    public void Save_AfterASecond_WritesAtOnce()
    {
        var store = Store();
        store.Save(A);

        _time.Advance(TimeSpan.FromSeconds(2));
        store.Save(B);

        Assert.Equal(B, Store().Load());
    }

    [Fact]
    public void Load_MissingOrUnreadableFile_IsNull()
    {
        Assert.Null(Store().Load()); // le fichier manque

        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, "pas du json");
        Assert.Null(Store().Load());

        File.WriteAllText(FilePath, "");
        Assert.Null(Store().Load());

        File.WriteAllText(FilePath, """{"ViewId":"pas-un-guid"}""");
        Assert.Null(Store().Load());

        File.WriteAllText(FilePath, """{"ViewId":"00000000-0000-0000-0000-000000000000"}""");
        Assert.Null(Store().Load());

        File.WriteAllText(FilePath, """{"Autre":1}""");
        Assert.Null(Store().Load());
    }

    [Fact]
    public void Flush_WritesThePendingValue()
    {
        var store = Store();
        store.Save(A);
        store.Save(B);
        Assert.Equal(A, Store().Load());

        store.Flush();

        Assert.Equal(B, Store().Load());
        Assert.Contains("\"ViewId\"", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Flush_NothingPending_DoesNotWrite()
    {
        Store().Flush();

        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Save_UnwritablePath_DoesNotThrow()
    {
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(FilePath); // un dossier à la place du fichier : l'écriture échoue
        var store = Store();

        store.Save(A);
        store.Flush();

        Assert.Null(store.Load());
    }
}
