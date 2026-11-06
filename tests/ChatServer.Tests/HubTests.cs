namespace ChatServer.Tests;

public sealed class FakeClient : IChatClient
{
    public List<string> Lines { get; } = new();
    public int Capacity { get; set; } = int.MaxValue;
    public bool Closed { get; private set; }

    public bool TrySend(string line)
    {
        if (Lines.Count >= Capacity) return false;
        Lines.Add(line);
        return true;
    }

    public void Close() => Closed = true;

    public void Clear() => Lines.Clear();
}

public class HubTests
{
    private readonly ChatHub _hub = new();

    private (ChatUser User, FakeClient Client) Join(string nick)
    {
        var client = new FakeClient();
        var user = _hub.Connect(client);
        _hub.HandleLine(user, "/nick " + nick);
        return (user, client);
    }

    [Fact]
    public void NewConnection_IsGreeted_AndMustPickNickFirst()
    {
        var client = new FakeClient();
        var user = _hub.Connect(client);
        Assert.StartsWith("* Welcome", client.Lines[0]);
        _hub.HandleLine(user, "hello?");
        _hub.HandleLine(user, "/join dev");
        Assert.All(client.Lines.Skip(1), l => Assert.StartsWith("! Choose a nickname first", l));
        Assert.Equal(0, _hub.UserCount);
    }

    [Fact]
    public void FirstNick_JoinsLobby_AndAnnouncesToOthers()
    {
        var (_, a) = Join("alice");
        Assert.Contains("* You are now known as alice", a.Lines);
        Assert.Contains(a.Lines, l => l.StartsWith("* You joined #lobby") && l.Contains("alice"));
        var (_, b) = Join("bob");
        Assert.Contains("* bob joined #lobby", a.Lines);
        Assert.Contains(b.Lines, l => l.Contains("Users here: alice, bob"));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("this-nickname-is-too-long")]
    [InlineData("bad nick")]
    [InlineData("emoji😀")]
    [InlineData("")]
    public void InvalidNicks_AreRejected(string nick)
    {
        var client = new FakeClient();
        var user = _hub.Connect(client);
        _hub.HandleLine(user, "/nick " + nick);
        Assert.StartsWith("! Invalid nickname", client.Lines[^1]);
        Assert.Null(user.Nick);
    }

    [Fact]
    public void Nicks_AreUniqueCaseInsensitively()
    {
        Join("Alice");
        var client = new FakeClient();
        var user = _hub.Connect(client);
        _hub.HandleLine(user, "/nick alice");
        Assert.Equal("! Nickname 'alice' is already taken", client.Lines[^1]);
        Assert.Null(user.Nick);
    }

    [Fact]
    public void Chat_ReachesOnlyTheSameRoom_IncludingSender()
    {
        var (alice, a) = Join("alice");
        var (_, b) = Join("bob");
        var (carol, c) = Join("carol");
        _hub.HandleLine(carol, "/join dev");
        a.Clear(); b.Clear(); c.Clear();

        _hub.HandleLine(alice, "hi there");
        Assert.Equal(new[] { "[#lobby] alice: hi there" }, a.Lines);
        Assert.Equal(new[] { "[#lobby] alice: hi there" }, b.Lines);
        Assert.Empty(c.Lines);
    }

    [Fact]
    public void Join_MovesBetweenRooms_AndNotifiesBoth()
    {
        var (alice, a) = Join("alice");
        var (bob, b) = Join("bob");
        a.Clear(); b.Clear();
        _hub.HandleLine(bob, "/join #games");
        Assert.Equal("games", bob.Room);
        Assert.Contains("* bob left #lobby", a.Lines);
        Assert.Contains(b.Lines, l => l.StartsWith("* You joined #games"));

        _hub.HandleLine(alice, "/join games");
        Assert.Contains("* alice joined #games", b.Lines);
    }

    [Fact]
    public void Join_SameRoom_IsAnError()
    {
        var (u, c) = Join("alice");
        _hub.HandleLine(u, "/join lobby");
        Assert.StartsWith("! You are already in #lobby", c.Lines[^1]);
    }

    [Fact]
    public void Join_InvalidRoomName()
    {
        var (u, c) = Join("alice");
        _hub.HandleLine(u, "/join bad/room");
        Assert.StartsWith("! Invalid room name", c.Lines[^1]);
    }

    [Fact]
    public void Leave_ReturnsToLobby_AndEmptyRoomsDisappear()
    {
        var (u, c) = Join("alice");
        _hub.HandleLine(u, "/join solo");
        Assert.Contains("solo", _hub.RoomSizes().Keys);
        _hub.HandleLine(u, "/leave");
        Assert.Equal("lobby", u.Room);
        Assert.DoesNotContain("solo", _hub.RoomSizes().Keys);
        _hub.HandleLine(u, "/leave");
        Assert.StartsWith("! You are in #lobby", c.Lines[^1]);
    }

    [Fact]
    public void Rooms_AreCaseInsensitive_AndKeepFirstSpelling()
    {
        var (a, _) = Join("alice");
        var (b, _) = Join("bob");
        _hub.HandleLine(a, "/join Dev");
        _hub.HandleLine(b, "/join DEV");
        Assert.Equal("Dev", b.Room);
        Assert.Equal(2, _hub.RoomSizes()["dev"]);
    }

    [Fact]
    public void Rooms_AndWho()
    {
        var (a, ca) = Join("alice");
        var (b, _) = Join("bob");
        _hub.HandleLine(b, "/join dev");
        _hub.HandleLine(a, "/rooms");
        Assert.Equal("* Rooms: #dev (1), #lobby (1)", ca.Lines[^1]);
        _hub.HandleLine(a, "/who");
        Assert.Equal("* Users in #lobby: alice", ca.Lines[^1]);
        _hub.HandleLine(a, "/who dev");
        Assert.Equal("* Users in #dev: bob", ca.Lines[^1]);
        _hub.HandleLine(a, "/who nowhere");
        Assert.Equal("! No such room #nowhere", ca.Lines[^1]);
    }

    [Fact]
    public void PrivateMessages_GoOnlyToTarget_AndEchoToSender()
    {
        var (a, ca) = Join("alice");
        var (_, cb) = Join("bob");
        var (_, cc) = Join("carol");
        ca.Clear(); cb.Clear(); cc.Clear();
        _hub.HandleLine(a, "/msg BOB psst, secret");
        Assert.Equal(new[] { "(private) from alice: psst, secret" }, cb.Lines);
        Assert.Equal(new[] { "(private) to bob: psst, secret" }, ca.Lines);
        Assert.Empty(cc.Lines);
        _hub.HandleLine(a, "/msg ghost hi");
        Assert.Equal("! No such user 'ghost'", ca.Lines[^1]);
        _hub.HandleLine(a, "/msg bob");
        Assert.StartsWith("! Usage", ca.Lines[^1]);
    }

    [Fact]
    public void Rename_NotifiesRoom_AndFreesOldNick()
    {
        var (a, ca) = Join("alice");
        var (_, cb) = Join("bob");
        cb.Clear();
        _hub.HandleLine(a, "/nick alicia");
        Assert.Equal(new[] { "* alice is now known as alicia" }, cb.Lines);
        Join("alice"); // old nick is available again
        Assert.Equal(3, _hub.UserCount);
        _hub.HandleLine(a, "/nick alicia");
        Assert.Equal("* You are already alicia", ca.Lines[^1]);
    }

    [Fact]
    public void Disconnect_RemovesUser_NotifiesRoom_AndIsIdempotent()
    {
        var (a, _) = Join("alice");
        var (b, cb) = Join("bob");
        cb.Clear();
        _hub.Disconnect(a);
        _hub.Disconnect(a);
        Assert.Equal(new[] { "* alice left #lobby" }, cb.Lines);
        Assert.Equal(1, _hub.UserCount);
        Join("alice");
    }

    [Fact]
    public void Quit_SaysBye_AndReturnsFalse()
    {
        var (a, ca) = Join("alice");
        Assert.False(_hub.HandleLine(a, "/quit"));
        Assert.Equal("* Bye!", ca.Lines[^1]);
    }

    [Fact]
    public void Unknown_Empty_AndHelp()
    {
        var (a, ca) = Join("alice");
        var before = ca.Lines.Count;
        Assert.True(_hub.HandleLine(a, "   "));
        Assert.Equal(before, ca.Lines.Count);
        _hub.HandleLine(a, "/dance");
        Assert.Equal("! Unknown command /dance. Try /help", ca.Lines[^1]);
        _hub.HandleLine(a, "/HELP");
        Assert.StartsWith("* Commands:", ca.Lines[^1]);
    }

    [Fact]
    public void SlowClient_IsDisconnected_WithoutAffectingOthers()
    {
        var (a, ca) = Join("alice");
        var slow = new FakeClient();
        var slowUser = _hub.Connect(slow);
        _hub.HandleLine(slowUser, "/nick slowpoke");
        slow.Capacity = slow.Lines.Count; // from now on its queue is "full"
        ca.Clear();

        _hub.HandleLine(a, "anyone there?");
        Assert.True(slow.Closed);
        Assert.Equal(1, _hub.UserCount);
        Assert.Contains("[#lobby] alice: anyone there?", ca.Lines);
        Assert.Contains("* slowpoke left #lobby (too slow)", ca.Lines);
        Assert.False(_hub.HandleLine(slowUser, "still here"));
    }

    [Fact]
    public void Announce_ReachesEveryoneEvenWithoutNick()
    {
        var (_, ca) = Join("alice");
        var anon = new FakeClient();
        _hub.Connect(anon);
        _hub.Announce("maintenance");
        Assert.Equal("* maintenance", ca.Lines[^1]);
        Assert.Equal("* maintenance", anon.Lines[^1]);
    }

    [Fact]
    public async Task ConcurrentActivity_KeepsStateConsistent()
    {
        var clients = Enumerable.Range(0, 20).Select(i => (Client: new FakeClient(), Index: i)).ToList();
        await Task.WhenAll(clients.Select(x => Task.Run(() =>
        {
            var user = _hub.Connect(x.Client);
            _hub.HandleLine(user, $"/nick user{x.Index}");
            for (var r = 0; r < 50; r++)
            {
                _hub.HandleLine(user, $"/join room{(x.Index + r) % 4}");
                _hub.HandleLine(user, "hello");
            }
            _hub.Disconnect(user);
        })));
        Assert.Equal(0, _hub.UserCount);
        Assert.Empty(_hub.RoomSizes());
    }
}
