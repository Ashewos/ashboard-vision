using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AshboardVision.Sdk;
using SkiaSharp;
using Trofeo.Extensions.Protocol;
using Trofeo.Extensions.Sandbox;
using Trofeo.ModuleKit;

namespace Trofeo.Extensions.Tests;

/// <summary>Le protocole entre le service et l'hôte : trames JSON sur deux flux, images dans une section partagée.</summary>
public sealed class ProtocolTests
{
    private static readonly Guid Instance = Guid.Parse("3f2b8e0a-5c41-4f7e-9a63-1d0c7b2e4a55");

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    /// <summary>Un exemplaire de chaque message, avec le <c>type</c> attendu sur le fil.</summary>
    public static TheoryData<string, HostMessage> EveryMessage() => new()
    {
        { "hello", new Hello(HostMessage.ProtocolVersion, @"C:\mods\a.b\1.0.0", "A.B.dll", "a.b",
            [ModulePermission.Internet, ModulePermission.ThisComputer], @"C:\data\a.b", Json("""{"theme":"dark"}"""), 30) },
        { "open", new Open(Instance, Json("""{"port":25565}"""), 480, 231, 1.25f, 4, 2, 1234, 0) },
        { "close", new Close(Instance) },
        { "appearanceChanged", new AppearanceChanged(Json("""{"theme":"light"}""")) },
        { "limits", new Limits(15) },
        { "frameDone", new FrameDone(Instance, 1) },
        { "visibility", new Visibility(Instance, false) },
        { "shortcut", new Shortcut(Instance, "toggle") },
        { "lookup", new Lookup(7, "entity", "salon") },
        { "runAction", new RunAction(8, Instance, "test", Json("""{"host":"x"}""")) },
        { "secretValue", new SecretValue(9, "s3cret") },
        { "dataValue", new DataValue(10, "ok", Json("""{"cpu":42.5}""")) },
        { "shutdown", new Shutdown() },
        { "ready", new Ready("1.0") },
        { "frame", new Frame(Instance, 0, true) },
        { "status", new Status(Instance, ModuleHealth.NotConfigured, "À régler") },
        { "log", new Log("warn", "attention") },
        { "secretGet", new SecretGet(11, "token") },
        { "secretSet", new SecretSet(12, "token", null) },
        { "dataGet", new DataGet(13, "nowPlaying") },
        { "localConnect", new LocalConnect(14, Instance, 25565) },
        { "lookupReply", new LookupReply(15, [new LookupResult("Salon", Json("""{"id":"light.salon"}"""))]) },
        { "actionReply", new ActionReply(16, new ActionResult(true, "Fait.")) },
        { "failure", new Failure("Deux classes de module.") },
        { "localConnected", new LocalConnected(17, 4000, 4004) },
        { "localRefused", new LocalRefused(18, "Port non autorisé") },
    };

    /// <summary>Un canal dont on lit ce qu'il écrit (pour observer le fil).</summary>
    private static async Task<byte[]> WireOf(params HostMessage[] messages)
    {
        var wire = new MemoryStream();
        await using var channel = new MessageChannel(new MemoryStream(), new NonClosingStream(wire));
        foreach (var message in messages)
        {
            await channel.SendAsync(message, CancellationToken.None);
        }

        return wire.ToArray();
    }

    private static async Task<List<HostMessage>> ReadAll(byte[] wire)
    {
        await using var channel = new MessageChannel(new MemoryStream(wire), new MemoryStream());
        var read = new List<HostMessage>();
        await foreach (var message in channel.ReadAllAsync(CancellationToken.None))
        {
            read.Add(message);
        }

        return read;
    }

    private static byte[] FrameOf(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + payload.Length];
        BitConverter.GetBytes((uint)payload.Length).CopyTo(frame, 0);
        payload.CopyTo(frame, 4);
        return frame;
    }

    [Fact]
    public void ProtocolVersion_Is1Point1() => Assert.Equal("1.1", HostMessage.ProtocolVersion);

    [Fact]
    public void EveryMessage_CoversTheWholeProtocol()
    {
        var types = typeof(HostMessage).Assembly.GetTypes().Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(HostMessage)));
        Assert.Equal(types.Count(), EveryMessage().Count);
    }

    [Theory]
    [MemberData(nameof(EveryMessage))]
    public async Task RoundTrip_EachMessageType(string type, HostMessage message)
    {
        var wire = await WireOf(message);

        // Sur le fil : un en-tête de 4 octets, puis du JSON dont le discriminant est « type ».
        Assert.Equal((uint)(wire.Length - 4), BitConverter.ToUInt32(wire, 0));
        using var document = JsonDocument.Parse(wire.AsMemory(4));
        Assert.Equal(type, document.RootElement.GetProperty("type").GetString());

        var read = Assert.Single(await ReadAll(wire));
        Assert.Equal(message.GetType(), read.GetType());

        // Les records comparent leurs listes et leurs JsonElement par référence : on compare le fil.
        Assert.Equal(await WireOf(message), await WireOf(read));
    }

    [Fact]
    public async Task RoundTrip_KeepsValues()
    {
        var hello = new Hello("1.0", @"C:\mods", "Mod.dll", "a.b",
            [ModulePermission.LocalNetwork, ModulePermission.NowPlaying], @"C:\data", Json("""{"k":[1,2]}"""), 24);
        var open = new Open(Instance, Json("""{"port":8123}"""), 1920, 462, 1.5f, 16, 4, 77, 0);
        var status = new Status(Instance, ModuleHealth.Offline, null);

        var read = await ReadAll(await WireOf(hello, open, status));

        var roundHello = Assert.IsType<Hello>(read[0]);
        Assert.Equal([ModulePermission.LocalNetwork, ModulePermission.NowPlaying], roundHello.Granted);
        Assert.Equal(24, roundHello.MaxFps);
        Assert.Equal(2, roundHello.Appearance.GetProperty("k").GetArrayLength());
        var roundOpen = Assert.IsType<Open>(read[1]);
        Assert.Equal(Instance, roundOpen.InstanceId);
        Assert.Equal(1.5f, roundOpen.TextScale);
        Assert.Equal(77, roundOpen.Buffer0);
        Assert.Equal(0, roundOpen.Buffer1); // réservé dans le protocole 1.0
        Assert.Equal(8123, roundOpen.Settings.GetProperty("port").GetInt32());
        var roundStatus = Assert.IsType<Status>(read[2]);
        Assert.Equal(ModuleHealth.Offline, roundStatus.Health);
        Assert.Null(roundStatus.Message);
    }

    [Fact]
    public async Task Wire_UsesCamelCaseNamesAndManifestPermissions()
    {
        var wire = await WireOf(
            new Hello("1.0", "d", "e", "a.b", [ModulePermission.LocalNetwork, ModulePermission.ThisComputer], "x", Json("{}"), 30),
            new Status(Instance, ModuleHealth.NotConfigured, "m"));

        var text = Encoding.UTF8.GetString(wire);
        Assert.Contains("\"type\":\"hello\"", text);
        Assert.Contains("\"granted\":[\"reseau-local\",\"ce-pc\"]", text);
        Assert.Contains("\"moduleDirectory\":\"d\"", text);
        Assert.Contains("\"maxFps\":30", text);
        Assert.Contains("\"health\":\"notConfigured\"", text);
        Assert.Contains("\"instanceId\"", text);
    }

    [Fact]
    public async Task Read_TypeNotFirst_StillUnderstood()
    {
        var read = await ReadAll(FrameOf("""{"id":3,"key":"token","type":"secretGet"}"""));

        var message = Assert.IsType<SecretGet>(Assert.Single(read));
        Assert.Equal(3, message.Id);
        Assert.Equal("token", message.Key);
    }

    [Fact]
    public async Task Read_HealthInAnyCase_Understood()
    {
        var read = await ReadAll(FrameOf($$"""{"type":"status","instanceId":"{{Instance}}","health":"Online"}"""));

        Assert.Equal(ModuleHealth.Online, Assert.IsType<Status>(Assert.Single(read)).Health);
    }

    [Fact]
    public async Task Read_UnknownMessageType_ThrowsInvalidData()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAll(FrameOf("""{"type":"explode"}""")));
    }

    [Fact]
    public async Task Read_UnknownPermission_ThrowsInvalidData()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAll(FrameOf(
            """{"type":"hello","protocol":"1.0","moduleDirectory":"d","entry":"e","typeId":"a.b","granted":["root"],"dataDirectory":"x","appearance":{},"maxFps":30}""")));
    }

    [Fact]
    public async Task Read_NotJson_ThrowsInvalidData()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAll(FrameOf("pas du json")));
    }

    /// <summary>Des messages venant d'un hôte qu'on ne croit pas : champ absent, nul, du mauvais genre ou hors bornes.</summary>
    [Theory]
    [InlineData("""{"type":"log"}""")]
    [InlineData("""{"type":"log","level":"info"}""")]
    [InlineData("""{"type":"log","message":"m"}""")]
    [InlineData("""{"type":"log","level":null,"message":"m"}""")]
    [InlineData("""{"type":"log","level":"info","message":null}""")]
    [InlineData("""{"type":"secretGet","id":1}""")]
    [InlineData("""{"type":"secretGet","key":"k"}""")]
    [InlineData("""{"type":"secretGet","id":1,"key":null}""")]
    [InlineData("""{"type":"ready"}""")]
    [InlineData("""{"type":"failure"}""")]
    [InlineData("""{"type":"lookupReply","id":1}""")]
    [InlineData("""{"type":"lookupReply","id":1,"results":null}""")]
    [InlineData("""{"type":"lookupReply","id":1,"results":[null]}""")]
    [InlineData("""{"type":"lookupReply","id":1,"results":[{"label":"x"}]}""")]
    [InlineData("""{"type":"lookupReply","id":1,"results":[{"value":{}}]}""")]
    [InlineData("""{"type":"actionReply","id":1}""")]
    [InlineData("""{"type":"actionReply","id":1,"result":null}""")]
    [InlineData("""{"type":"actionReply","id":1,"result":{"ok":true}}""")]
    [InlineData("""{"type":"dataGet","id":1}""")]
    [InlineData("""{"type":"localConnect","id":1,"port":80}""")]
    [InlineData("""{"type":"status","instanceId":"3f2b8e0a-5c41-4f7e-9a63-1d0c7b2e4a55"}""")]
    [InlineData("""{"type":"status","instanceId":"3f2b8e0a-5c41-4f7e-9a63-1d0c7b2e4a55","health":99}""")]
    [InlineData("""{"type":"status","instanceId":"3f2b8e0a-5c41-4f7e-9a63-1d0c7b2e4a55","health":1}""")]
    [InlineData("""{"type":"status","instanceId":"3f2b8e0a-5c41-4f7e-9a63-1d0c7b2e4a55","health":"1"}""")]
    [InlineData("""{"type":"status","instanceId":"3f2b8e0a-5c41-4f7e-9a63-1d0c7b2e4a55","health":"bogus"}""")]
    [InlineData("""{"type":"status","instanceId":"3f2b8e0a-5c41-4f7e-9a63-1d0c7b2e4a55","health":null}""")]
    [InlineData("""{"type":"frame","instanceId":"3f2b8e0a-5c41-4f7e-9a63-1d0c7b2e4a55","buffer":0}""")]
    [InlineData("""{"type":"hello","protocol":"1.0","moduleDirectory":"d","entry":"e","typeId":"a.b","granted":[],"dataDirectory":"x","maxFps":30}""")]
    [InlineData("""{"type":"hello","protocol":"1.0","moduleDirectory":"d","entry":"e","typeId":"a.b","dataDirectory":"x","appearance":{},"maxFps":30}""")]
    [InlineData("""{"type":"hello","protocol":"1.0","moduleDirectory":"d","entry":"e","typeId":"a.b","granted":[null],"dataDirectory":"x","appearance":{},"maxFps":30}""")]
    [InlineData("""{"type":"hello","protocol":"1.0","moduleDirectory":"d","entry":"e","typeId":"a.b","granted":[3],"dataDirectory":"x","appearance":{},"maxFps":30}""")]
    [InlineData("""{"type":"limits"}""")]
    [InlineData("""{"type":"appearanceChanged"}""")]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    [InlineData("""123""")]
    [InlineData("""{"type":3}""")]
    public async Task Read_MissingNullOrOutOfRangeField_ThrowsInvalidData(string json)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAll(FrameOf(json)));
    }

    [Fact]
    public async Task Read_OptionalFieldsAbsentOrNull_AreAccepted()
    {
        var read = await ReadAll(FrameOf(
            $$"""
            {"type":"status","instanceId":"{{Instance}}","health":"online"}
            """).Concat(FrameOf("""{"type":"secretSet","id":2,"key":"k","value":null}""")).Concat(FrameOf("""{"type":"secretValue","id":3}""")).Concat(FrameOf("""{"type":"dataValue","id":4,"status":"denied"}""")).ToArray());

        Assert.Null(Assert.IsType<Status>(read[0]).Message);
        Assert.Null(Assert.IsType<SecretSet>(read[1]).Value);
        Assert.Null(Assert.IsType<SecretValue>(read[2]).Value);
        Assert.Null(Assert.IsType<DataValue>(read[3]).Value);
    }

    [Fact]
    public void IsFromHost_SeparatesTheTwoDirections()
    {
        var fromHost = new HashSet<string>
        {
            "ready", "frame", "status", "log", "secretGet", "secretSet", "dataGet", "localConnect", "lookupReply", "actionReply", "failure",
        };

        foreach (var data in EveryMessage())
        {
            var type = (string)data[0];
            Assert.Equal(fromHost.Contains(type), HostMessage.IsFromHost((HostMessage)data[1]));
        }
    }

    [Fact]
    public async Task Read_OversizedFrame_Throws()
    {
        var wire = new byte[4 + 16];
        BitConverter.GetBytes((uint)(MessageChannel.MaxFrameBytes + 1)).CopyTo(wire, 0);

        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAll(wire));
    }

    [Fact]
    public async Task Read_HugeLengthHeader_ThrowsWithoutAllocating()
    {
        var wire = new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };

        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAll(wire));
    }

    [Fact]
    public async Task Read_FrameAtTheLimit_IsAccepted()
    {
        // Un message de 1 Mio pile : le JSON {"type":"log","level":"info","message":"…"}.
        const string Prefix = "{\"type\":\"log\",\"level\":\"info\",\"message\":\"";
        var padding = new string('x', MessageChannel.MaxFrameBytes - Prefix.Length - 2);
        var json = Prefix + padding + "\"}";
        Assert.Equal(MessageChannel.MaxFrameBytes, Encoding.UTF8.GetByteCount(json));

        var log = Assert.IsType<Log>(Assert.Single(await ReadAll(FrameOf(json))));
        Assert.Equal(padding.Length, log.Message.Length);
    }

    [Fact]
    public async Task Send_OversizedMessage_Throws()
    {
        await using var channel = new MessageChannel(new MemoryStream(), new MemoryStream());
        var message = new Log("info", new string('x', MessageChannel.MaxFrameBytes));

        await Assert.ThrowsAsync<ArgumentException>(() => channel.SendAsync(message, CancellationToken.None));
    }

    [Fact]
    public async Task Read_TruncatedStream_EndsCleanly()
    {
        var wire = await WireOf(new Ready("1.0"), new Limits(10));
        var firstFrame = 4 + (int)BitConverter.ToUInt32(wire, 0);

        // Coupé au milieu de la deuxième charge utile, puis au milieu de son en-tête, puis juste après la première trame.
        foreach (var cut in new[] { wire.Length - 3, firstFrame + 2, firstFrame })
        {
            var read = await ReadAll(wire[..cut]);
            Assert.IsType<Ready>(Assert.Single(read));
        }

        Assert.Empty(await ReadAll([]));
        Assert.Empty(await ReadAll([1, 0]));
    }

    [Fact]
    public async Task Read_BrokenPipe_EndsCleanly()
    {
        await using var channel = new MessageChannel(new ThrowingStream(new IOException("tube coupé")), new MemoryStream());

        var read = new List<HostMessage>();
        await foreach (var message in channel.ReadAllAsync(CancellationToken.None))
        {
            read.Add(message);
        }

        Assert.Empty(read);
    }

    [Fact]
    public async Task Read_Cancelled_Throws()
    {
        await using var channel = new MessageChannel(new BlockingStream(), new MemoryStream());
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in channel.ReadAllAsync(cancel.Token))
            {
            }
        });
    }

    [Fact]
    public async Task Send_ConcurrentCallers_NeverInterleave()
    {
        var wire = new MemoryStream();
        await using var channel = new MessageChannel(new MemoryStream(), new NonClosingStream(wire));

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(
            () => channel.SendAsync(new Log("info", new string((char)('a' + i % 26), 500 + i)), CancellationToken.None))));

        var read = await ReadAll(wire.ToArray());
        Assert.Equal(200, read.Count);
        Assert.All(read, message =>
        {
            var log = Assert.IsType<Log>(message);
            Assert.Single(log.Message.Distinct());
        });
        Assert.Equal(Enumerable.Range(0, 200).Select(i => 500 + i).Order(), read.Cast<Log>().Select(l => l.Message.Length).Order());
    }

    [Fact]
    public async Task Channel_OverAnonymousPipes_RoundTrips()
    {
        // Le montage de production : un tube par sens.
        using var toHost = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var hostIn = new AnonymousPipeClientStream(PipeDirection.In, toHost.ClientSafePipeHandle);
        using var fromHost = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        using var hostOut = new AnonymousPipeClientStream(PipeDirection.Out, fromHost.ClientSafePipeHandle);
        await using var service = new MessageChannel(fromHost, toHost);
        await using var host = new MessageChannel(hostIn, hostOut);

        await service.SendAsync(new Limits(12), CancellationToken.None);
        await host.SendAsync(new Ready("1.0"), CancellationToken.None);

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var message in host.ReadAllAsync(cancel.Token))
        {
            Assert.Equal(12, Assert.IsType<Limits>(message).MaxFps);
            break;
        }

        await foreach (var message in service.ReadAllAsync(cancel.Token))
        {
            Assert.Equal("1.0", Assert.IsType<Ready>(message).RequiredSdk);
            break;
        }
    }

    [Fact]
    public async Task Read_PendingOnAnonymousPipe_EndsCleanlyWhenTheWriterCloses()
    {
        // Une lecture en attente sur un tube anonyme ne se termine pas par DisposeAsync ni par l'annulation : elle se termine
        // quand l'autre extrémité se ferme (et que le service a fermé sa copie de la poignée client).
        using var fromHost = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        using var self = Process.GetCurrentProcess();
        Assert.True(Native.DuplicateHandle(Native.GetCurrentProcess(), fromHost.ClientSafePipeHandle, self.SafeHandle, out var duplicate,
            0, false, Native.DuplicateSameAccess)); // la copie de l'hôte ; le service garde la sienne, comme en production
        var hostOut = new AnonymousPipeClientStream(PipeDirection.Out, new Microsoft.Win32.SafeHandles.SafePipeHandle(duplicate, true));
        await using var service = new MessageChannel(fromHost, new MemoryStream());

        var reading = Task.Run(async () =>
        {
            var received = new List<HostMessage>();
            await foreach (var message in service.ReadAllAsync(CancellationToken.None))
            {
                received.Add(message);
            }

            return received;
        });

        await using (var host = new MessageChannel(new MemoryStream(), hostOut))
        {
            await host.SendAsync(new Ready("1.0"), CancellationToken.None);
        }

        // Tant que l'écrivain est ouvert (ou que le service garde la poignée client), la lecture reste en attente.
        Assert.NotSame(reading, await Task.WhenAny(reading, Task.Delay(TimeSpan.FromMilliseconds(300))));

        fromHost.DisposeLocalCopyOfClientHandle();

        var done = await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(reading, done);
        Assert.IsType<Ready>(Assert.Single(await reading));
    }

    [Fact]
    public async Task Send_AfterDispose_Throws()
    {
        var channel = new MessageChannel(new MemoryStream(), new MemoryStream());
        await channel.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => channel.SendAsync(new Shutdown(), CancellationToken.None));
    }

    // --- Images partagées ------------------------------------------------------------------------------------------

    private static IntPtr DuplicateInSameProcess(FrameBuffers buffers)
    {
        using var self = Process.GetCurrentProcess();
        Assert.True(Native.DuplicateHandle(Native.GetCurrentProcess(), buffers.Section, self.SafeHandle, out var duplicate,
            0, false, Native.DuplicateSameAccess));
        return duplicate;
    }

    private static SKBitmap NewBitmap(int width, int height) => new(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));

    private static void WritePixel(IntPtr view, int offsetBytes, uint bgra) => System.Runtime.InteropServices.Marshal.WriteInt32(view, offsetBytes, unchecked((int)bgra));

    [Fact]
    public void FrameBuffers_WriteThroughMappedView_ReadsBack()
    {
        const int W = 48, H = 20;
        using var buffers = new FrameBuffers(W, H);
        var duplicate = DuplicateInSameProcess(buffers);
        var view = FrameView.Map(duplicate, W, H);
        try
        {
            var bufferBytes = W * H * 4;
            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    WritePixel(view, (y * W + x) * 4, 0xFF000000u | (uint)(x << 8) | (uint)y);                       // tampon 0
                    WritePixel(view, bufferBytes + (y * W + x) * 4, 0xFF000000u | (uint)(y << 16) | (uint)(x << 8)); // tampon 1
                }
            }

            using var zero = NewBitmap(W, H);
            using var one = NewBitmap(W, H);
            buffers.CopyTo(0, zero);
            buffers.CopyTo(1, one);

            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    Assert.Equal(new SKColor((byte)0, (byte)x, (byte)y, (byte)0xFF), zero.GetPixel(x, y));
                    Assert.Equal(new SKColor((byte)y, (byte)x, (byte)0, (byte)0xFF), one.GetPixel(x, y));
                }
            }
        }
        finally
        {
            FrameView.Unmap(view);
            Native.CloseHandle(duplicate);
        }
    }

    [Fact]
    public void FrameBuffers_Section_HasExactSize()
    {
        using var buffers = new FrameBuffers(1920, 462);
        var duplicate = DuplicateInSameProcess(buffers);
        try
        {
            // Le dernier octet du tampon 1 est accessible : la section fait bien 2 × w × h × 4.
            var view = FrameView.Map(duplicate, 1920, 462);
            try
            {
                var last = 2L * 1920 * 462 * 4 - 1;
                System.Runtime.InteropServices.Marshal.WriteByte(view, (int)last, 0x7F);
                Assert.Equal(0x7F, System.Runtime.InteropServices.Marshal.ReadByte(view, (int)last));
            }
            finally
            {
                FrameView.Unmap(view);
            }

            // Une vue plus grande que la section est refusée.
            Assert.Throws<System.ComponentModel.Win32Exception>(() => FrameView.Map(duplicate, 1920, 463));
        }
        finally
        {
            Native.CloseHandle(duplicate);
        }
    }

    [Fact]
    public void CopyTo_DestinationWithPadding_HonoursItsRowBytes()
    {
        const int W = 10, H = 4, Stride = 64;
        using var buffers = new FrameBuffers(W, H);
        var duplicate = DuplicateInSameProcess(buffers);
        var view = FrameView.Map(duplicate, W, H);
        var memory = System.Runtime.InteropServices.Marshal.AllocHGlobal(Stride * H);
        try
        {
            for (var i = 0; i < Stride * H; i++)
            {
                System.Runtime.InteropServices.Marshal.WriteByte(memory, i, 0xEE); // le remplissage ne doit pas bouger
            }

            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    WritePixel(view, (y * W + x) * 4, 0xFF000000u | (uint)(y * 16 + x));
                }
            }

            using var destination = new SKBitmap();
            Assert.True(destination.InstallPixels(new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul), memory, Stride));
            buffers.CopyTo(0, destination);

            for (var y = 0; y < H; y++)
            {
                for (var x = 0; x < W; x++)
                {
                    Assert.Equal(unchecked((int)(0xFF000000u | (uint)(y * 16 + x))), System.Runtime.InteropServices.Marshal.ReadInt32(memory, y * Stride + x * 4));
                }

                Assert.Equal(0xEE, System.Runtime.InteropServices.Marshal.ReadByte(memory, y * Stride + W * 4)); // octets de remplissage intacts
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(memory);
            FrameView.Unmap(view);
            Native.CloseHandle(duplicate);
        }
    }

    [Fact]
    public void CopyTo_WrongSize_Throws()
    {
        using var buffers = new FrameBuffers(32, 16);
        using var wide = NewBitmap(33, 16);
        using var tall = NewBitmap(32, 17);

        Assert.Throws<ArgumentException>(() => buffers.CopyTo(0, wide));
        Assert.Throws<ArgumentException>(() => buffers.CopyTo(0, tall));
    }

    [Fact]
    public void CopyTo_WrongFormat_Throws()
    {
        using var buffers = new FrameBuffers(32, 16);
        using var rgba = new SKBitmap(new SKImageInfo(32, 16, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var unpremultiplied = new SKBitmap(new SKImageInfo(32, 16, SKColorType.Bgra8888, SKAlphaType.Unpremul));

        Assert.Throws<ArgumentException>(() => buffers.CopyTo(0, rgba));
        Assert.Throws<ArgumentException>(() => buffers.CopyTo(0, unpremultiplied));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void CopyTo_BufferOutOfRange_Throws(int buffer)
    {
        using var buffers = new FrameBuffers(32, 16);
        using var bitmap = NewBitmap(32, 16);

        Assert.ThrowsAny<ArgumentException>(() => buffers.CopyTo(buffer, bitmap));
    }

    [Fact]
    public void CopyTo_AfterDispose_Throws()
    {
        var buffers = new FrameBuffers(32, 16);
        using var bitmap = NewBitmap(32, 16);
        buffers.Dispose();

        Assert.Throws<ObjectDisposedException>(() => buffers.CopyTo(0, bitmap));
        buffers.Dispose(); // idempotent
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, 10)]
    [InlineData(100000, 100000)]
    public void FrameBuffers_AbsurdSize_Throws(int width, int height)
    {
        Assert.ThrowsAny<ArgumentException>(() => new FrameBuffers(width, height));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-5, 10)]
    [InlineData(100000, 100000)]
    public void FrameView_Map_InvalidSize_ThrowsBeforeTouchingTheHandle(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameView.Map(0x7FFF0, width, height));
    }

    [Fact]
    public void FrameView_BufferBytes_IsWidthTimesHeightTimesFour()
    {
        Assert.Equal(1920 * 462 * 4, FrameView.BufferBytes(1920, 462));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameView.BufferBytes(0, 1));
    }

    [Fact]
    public void FrameView_Map_InvalidHandle_Throws()
    {
        Assert.Throws<System.ComponentModel.Win32Exception>(() => FrameView.Map(0x7FFF0, 8, 8));
    }

    // --- Doublures de flux -----------------------------------------------------------------------------------------

    /// <summary>Un flux dont <c>Dispose</c> ne ferme pas le flux derrière (pour relire ce qu'on a écrit).</summary>
    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
    }

    /// <summary>Un flux de lecture qui n'a jamais rien à lire, et que seule l'annulation interrompt.</summary>
    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Un flux de lecture dont chaque lecture lève l'exception donnée.</summary>
    private sealed class ThrowingStream(Exception exception) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw exception;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw exception;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
