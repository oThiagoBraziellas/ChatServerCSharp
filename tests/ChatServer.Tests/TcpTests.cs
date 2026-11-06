using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ChatServer.Tests;

public sealed class ChatClient : IDisposable
{
    private readonly TcpClient _tcp = new();
    private StreamReader _reader = null!;
    private StreamWriter _writer = null!;

    public static async Task<ChatClient> ConnectAsync(int port)
    {
        var c = new ChatClient();
        await c._tcp.ConnectAsync(IPAddress.Loopback, port);
        var stream = c._tcp.GetStream();
        c._reader = new StreamReader(stream, Encoding.UTF8);
        c._writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
        return c;
    }

    public static async Task<ChatClient> ConnectAsync(int port, string nick)
    {
        var c = await ConnectAsync(port);
        await c.SendAsync("/nick " + nick);
        await c.ExpectAsync(l => l.StartsWith("* You joined #lobby"));
        return c;
    }

    public Task SendAsync(string line) => _writer.WriteLineAsync(line);

    public async Task<string?> ReadAsync(int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try { return await _reader.ReadLineAsync(cts.Token); }
        catch (OperationCanceledException) { throw new TimeoutException("no line received"); }
        catch (IOException) { return null; }
    }

    /// <summary>Reads lines until one matches; returns it.</summary>
    public async Task<string> ExpectAsync(Func<string, bool> predicate)
    {
        var seen = new List<string>();
        while (true)
        {
            var line = await ReadAsync() ?? throw new EndOfStreamException("closed; saw: " + string.Join(" | ", seen));
            if (predicate(line)) return line;
            seen.Add(line);
        }
    }

    public Task<string> ExpectAsync(string exact) => ExpectAsync(l => l == exact);

    /// <summary>Drains until the server closes the connection; returns every line received.</summary>
    public async Task<List<string>> ReadUntilClosedAsync()
    {
        var all = new List<string>();
        while (await ReadAsync() is { } line) all.Add(line);
        return all;
    }

    public void Dispose() => _tcp.Dispose();
}

public class TcpTests : IAsyncLifetime
{
    private ChatTcpServer _server = null!;

    public Task InitializeAsync()
    {
        _server = new ChatTcpServer(new IPEndPoint(IPAddress.Loopback, 0), new ChatServerOptions { MaxLineLength = 200 });
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task TwoClients_Chat_InTheLobby()
    {
        using var alice = await ChatClient.ConnectAsync(_server.Port, "alice");
        using var bob = await ChatClient.ConnectAsync(_server.Port, "bob");
        await alice.ExpectAsync("* bob joined #lobby");
        await alice.SendAsync("hello bob");
        Assert.Equal("[#lobby] alice: hello bob", await bob.ExpectAsync(l => l.StartsWith("[#lobby]")));
        Assert.Equal("[#lobby] alice: hello bob", await alice.ExpectAsync(l => l.StartsWith("[#lobby]")));
    }

    [Fact]
    public async Task Rooms_AreIsolated_OverTcp()
    {
        using var alice = await ChatClient.ConnectAsync(_server.Port, "alice");
        using var bob = await ChatClient.ConnectAsync(_server.Port, "bob");
        using var carol = await ChatClient.ConnectAsync(_server.Port, "carol");
        await bob.SendAsync("/join dev");
        await carol.SendAsync("/join dev");
        await bob.ExpectAsync(l => l.StartsWith("* You joined #dev"));
        await carol.ExpectAsync(l => l.StartsWith("* You joined #dev"));

        await carol.SendAsync("only for dev");
        await bob.ExpectAsync("[#dev] carol: only for dev");
        await alice.SendAsync("lobby chatter");
        await alice.ExpectAsync("[#lobby] alice: lobby chatter");
        // bob must not have received the lobby message: the next thing he sees is his own later message
        await bob.SendAsync("marker");
        Assert.Equal("[#dev] bob: marker", await bob.ExpectAsync(l => l.StartsWith("[#")));
    }

    [Fact]
    public async Task PrivateMessage_OverTcp()
    {
        using var alice = await ChatClient.ConnectAsync(_server.Port, "alice");
        using var bob = await ChatClient.ConnectAsync(_server.Port, "bob");
        await alice.SendAsync("/msg bob psst");
        Assert.Equal("(private) from alice: psst", await bob.ExpectAsync(l => l.StartsWith("(private)")));
        Assert.Equal("(private) to bob: psst", await alice.ExpectAsync(l => l.StartsWith("(private)")));
    }

    [Fact]
    public async Task DuplicateNick_IsRejected_ThenAnotherCanBeUsed()
    {
        using var alice = await ChatClient.ConnectAsync(_server.Port, "alice");
        using var other = await ChatClient.ConnectAsync(_server.Port);
        await other.SendAsync("/nick ALICE");
        await other.ExpectAsync("! Nickname 'ALICE' is already taken");
        await other.SendAsync("/nick alice2");
        await other.ExpectAsync(l => l.StartsWith("* You joined #lobby"));
    }

    [Fact]
    public async Task ClientDisconnect_IsAnnounced_AndFreesNick()
    {
        using var alice = await ChatClient.ConnectAsync(_server.Port, "alice");
        var bob = await ChatClient.ConnectAsync(_server.Port, "bob");
        await alice.ExpectAsync("* bob joined #lobby");
        bob.Dispose();
        await alice.ExpectAsync("* bob left #lobby");
        using var again = await ChatClient.ConnectAsync(_server.Port, "bob");
    }

    [Fact]
    public async Task QuitCommand_SaysBye_AndClosesConnection()
    {
        using var alice = await ChatClient.ConnectAsync(_server.Port, "alice");
        await alice.SendAsync("/quit");
        var rest = await alice.ReadUntilClosedAsync();
        Assert.Contains("* Bye!", rest);
        await WaitUntil(() => _server.ConnectionCount == 0);
        Assert.Equal(0, _server.Hub.UserCount);
    }

    [Fact]
    public async Task OverlongLine_ClosesConnection_WithMessage()
    {
        using var alice = await ChatClient.ConnectAsync(_server.Port, "alice");
        await alice.SendAsync(new string('x', 300)); // fits in one TCP read, so nothing is left unread when the server closes
        var rest = await alice.ReadUntilClosedAsync();
        Assert.Contains(rest, l => l.StartsWith("! Line exceeds 200 bytes"));
        await WaitUntil(() => _server.Hub.UserCount == 0);
    }

    [Fact]
    public async Task ManyClients_AllReceiveEveryBroadcast()
    {
        const int n = 30;
        var clients = new List<ChatClient>();
        for (var i = 0; i < n; i++) clients.Add(await ChatClient.ConnectAsync(_server.Port, $"user{i}"));
        try
        {
            await clients[0].SendAsync("ping everyone");
            await Task.WhenAll(clients.Select(c => c.ExpectAsync("[#lobby] user0: ping everyone")));
            Assert.Equal(n, _server.Hub.UserCount);
        }
        finally { clients.ForEach(c => c.Dispose()); }
    }

    [Fact]
    public async Task UnicodeMessages_RoundTrip()
    {
        using var alice = await ChatClient.ConnectAsync(_server.Port, "alice");
        await alice.SendAsync("olá, 世界 🌍");
        await alice.ExpectAsync("[#lobby] alice: olá, 世界 🌍");
    }

    [Fact]
    public async Task ServerFull_RejectsNewConnections()
    {
        await using var small = new ChatTcpServer(new IPEndPoint(IPAddress.Loopback, 0), new ChatServerOptions { MaxClients = 1 });
        small.Start();
        using var first = await ChatClient.ConnectAsync(small.Port, "first");
        using var second = await ChatClient.ConnectAsync(small.Port);
        var lines = await second.ReadUntilClosedAsync();
        Assert.Contains(lines, l => l.StartsWith("! Server is full"));
    }

    [Fact]
    public async Task GracefulShutdown_NotifiesClients_ClosesConnections_StopsListening()
    {
        var server = new ChatTcpServer(new IPEndPoint(IPAddress.Loopback, 0));
        server.Start();
        var port = server.Port;
        using var alice = await ChatClient.ConnectAsync(port, "alice");
        using var bob = await ChatClient.ConnectAsync(port, "bob");

        await server.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains(await alice.ReadUntilClosedAsync(), l => l.Contains("Server is shutting down"));
        Assert.Contains(await bob.ReadUntilClosedAsync(), l => l.Contains("Server is shutting down"));
        Assert.Equal(0, server.ConnectionCount);
        using var tcp = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(async () => await tcp.ConnectAsync(IPAddress.Loopback, port));
    }

    [Fact]
    public async Task Stop_IsIdempotent()
    {
        var server = new ChatTcpServer(new IPEndPoint(IPAddress.Loopback, 0));
        server.Start();
        await server.StopAsync();
        await server.StopAsync();
        await server.DisposeAsync();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }
}
