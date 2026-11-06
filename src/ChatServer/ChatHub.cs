using System.Text.RegularExpressions;

namespace ChatServer;

/// <summary>A connection as seen by the chat logic. Implemented by the TCP session and by fakes in tests.</summary>
public interface IChatClient
{
    /// <summary>Queues a line for delivery without blocking. Returns false if the client cannot keep up.</summary>
    bool TrySend(string line);

    /// <summary>Asks the transport to close the connection after pending lines are flushed.</summary>
    void Close();
}

/// <summary>Server-side state of one connected client.</summary>
public sealed class ChatUser
{
    internal ChatUser(IChatClient client) => Client = client;

    internal IChatClient Client { get; }
    public string? Nick { get; internal set; }
    public string? Room { get; internal set; }
    internal bool Gone { get; set; }
}

public sealed record ChatOptions
{
    public string DefaultRoom { get; init; } = "lobby";
}

/// <summary>
/// Transport-independent chat rules: nicknames, rooms, broadcast and private messages.
/// All state changes happen under one lock; delivery only enqueues lines (never blocks), so holding the lock is safe
/// and a slow client can never stall the others: if its queue is full, <see cref="IChatClient.TrySend"/> fails and
/// the user is disconnected.
/// </summary>
public sealed class ChatHub
{
    private static readonly Regex NickPattern = new("^[A-Za-z0-9_-]{2,16}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RoomPattern = new("^[A-Za-z0-9_-]{1,24}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public const string HelpText =
        "Commands: /nick <name>, /join <room>, /leave, /rooms, /who [room], /msg <nick> <text>, /help, /quit. Anything else is sent to your room.";

    private readonly object _gate = new();
    private readonly string _defaultRoom;
    private readonly HashSet<ChatUser> _users = new();
    private readonly Dictionary<string, ChatUser> _byNick = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<ChatUser>> _rooms = new(StringComparer.OrdinalIgnoreCase);

    public ChatHub(ChatOptions? options = null) => _defaultRoom = (options ?? new ChatOptions()).DefaultRoom;

    public int UserCount { get { lock (_gate) return _byNick.Count; } }

    public IReadOnlyDictionary<string, int> RoomSizes()
    {
        lock (_gate) return _rooms.ToDictionary(r => r.Key, r => r.Value.Count, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Registers a new connection and greets it. The user has no nickname yet.</summary>
    public ChatUser Connect(IChatClient client)
    {
        var user = new ChatUser(client);
        lock (_gate)
        {
            _users.Add(user);
            Send(user, "* Welcome! Choose a nickname with /nick <name> (2-16 letters, digits, _ or -). /help lists commands.");
        }
        return user;
    }

    /// <summary>Processes one line from a user. Returns false when the user asked to quit.</summary>
    public bool HandleLine(ChatUser user, string line)
    {
        line = line.Trim();
        if (line.Length == 0) return true;
        lock (_gate)
        {
            if (user.Gone) return false;
            if (!line.StartsWith('/')) { Chat(user, line); return true; }

            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var command = parts[0].ToLowerInvariant();
            var argument = parts.Length > 1 ? parts[1].Trim() : "";

            if (user.Nick is null && command is not ("/nick" or "/help" or "/quit"))
            {
                Send(user, "! Choose a nickname first with /nick <name>");
                return true;
            }

            switch (command)
            {
                case "/nick": SetNick(user, argument); break;
                case "/join": Join(user, argument); break;
                case "/leave": Leave(user); break;
                case "/rooms": ListRooms(user); break;
                case "/who": Who(user, argument); break;
                case "/msg": PrivateMessage(user, argument); break;
                case "/help": Send(user, "* " + HelpText); break;
                case "/quit":
                    Send(user, "* Bye!");
                    return false;
                default: Send(user, $"! Unknown command {command}. Try /help"); break;
            }
            return true;
        }
    }

    /// <summary>Removes the user (idempotent) and tells their room.</summary>
    public void Disconnect(ChatUser user)
    {
        lock (_gate)
        {
            if (user.Gone) return;
            user.Gone = true;
            _users.Remove(user);
            if (user.Nick is not null)
            {
                var room = user.Room;
                _byNick.Remove(user.Nick);
                if (room is not null) LeaveRoom(user);
                if (room is not null) Broadcast(room, $"* {user.Nick} left #{room}");
            }
        }
    }

    /// <summary>Sends a notice to every connected client (used for shutdown announcements).</summary>
    public void Announce(string text)
    {
        lock (_gate)
            foreach (var u in _users.ToList()) Send(u, "* " + text);
    }

    // ---- commands (all called with _gate held) -------------------------------------------------------------------

    private void SetNick(ChatUser user, string nick)
    {
        if (!NickPattern.IsMatch(nick))
        {
            Send(user, "! Invalid nickname. Use 2-16 letters, digits, _ or -");
            return;
        }
        if (_byNick.TryGetValue(nick, out var owner) && owner != user)
        {
            Send(user, $"! Nickname '{nick}' is already taken");
            return;
        }

        var old = user.Nick;
        if (old is not null) _byNick.Remove(old);
        user.Nick = nick;
        _byNick[nick] = user;

        if (old is null)
        {
            Send(user, $"* You are now known as {nick}");
            EnterRoom(user, _defaultRoom);
        }
        else if (old == nick) Send(user, $"* You are already {nick}");
        else
        {
            Send(user, $"* You are now known as {nick}");
            if (user.Room is not null) Broadcast(user.Room, $"* {old} is now known as {nick}", except: user);
        }
    }

    private void Join(ChatUser user, string room)
    {
        if (!RoomPattern.IsMatch(room.TrimStart('#')))
        {
            Send(user, "! Invalid room name. Use 1-24 letters, digits, _ or -");
            return;
        }
        room = room.TrimStart('#');
        if (string.Equals(user.Room, room, StringComparison.OrdinalIgnoreCase))
        {
            Send(user, $"! You are already in #{user.Room}");
            return;
        }
        MoveToRoom(user, room);
    }

    private void Leave(ChatUser user)
    {
        if (string.Equals(user.Room, _defaultRoom, StringComparison.OrdinalIgnoreCase))
        {
            Send(user, $"! You are in #{_defaultRoom}; use /quit to disconnect or /join <room> to move");
            return;
        }
        MoveToRoom(user, _defaultRoom);
    }

    private void MoveToRoom(ChatUser user, string room)
    {
        var previous = user.Room;
        if (previous is not null)
        {
            LeaveRoom(user);
            Broadcast(previous, $"* {user.Nick} left #{previous}");
        }
        EnterRoom(user, room);
    }

    private void EnterRoom(ChatUser user, string room)
    {
        // Keep the spelling the room was first created with.
        if (_rooms.TryGetValue(room, out var existing)) room = _rooms.Keys.First(k => string.Equals(k, room, StringComparison.OrdinalIgnoreCase));
        else _rooms[room] = existing = new HashSet<ChatUser>();
        existing.Add(user);
        user.Room = room;
        Broadcast(room, $"* {user.Nick} joined #{room}", except: user);
        Send(user, $"* You joined #{room}. Users here: {string.Join(", ", existing.Select(u => u.Nick).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))}");
    }

    private void LeaveRoom(ChatUser user)
    {
        if (user.Room is null) return;
        if (_rooms.TryGetValue(user.Room, out var members))
        {
            members.Remove(user);
            if (members.Count == 0) _rooms.Remove(user.Room); // empty rooms disappear
        }
        user.Room = null;
    }

    private void ListRooms(ChatUser user)
    {
        var list = _rooms.OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase).Select(r => $"#{r.Key} ({r.Value.Count})");
        Send(user, "* Rooms: " + string.Join(", ", list));
    }

    private void Who(ChatUser user, string room)
    {
        room = room.TrimStart('#');
        if (room.Length == 0) room = user.Room!;
        if (!_rooms.TryGetValue(room, out var members))
        {
            Send(user, $"! No such room #{room}");
            return;
        }
        Send(user, $"* Users in #{room}: {string.Join(", ", members.Select(u => u.Nick).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))}");
    }

    private void PrivateMessage(ChatUser user, string argument)
    {
        var parts = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) { Send(user, "! Usage: /msg <nick> <text>"); return; }
        if (!_byNick.TryGetValue(parts[0], out var target)) { Send(user, $"! No such user '{parts[0]}'"); return; }
        Send(target, $"(private) from {user.Nick}: {parts[1]}");
        if (target != user) Send(user, $"(private) to {target.Nick}: {parts[1]}");
    }

    private void Chat(ChatUser user, string text)
    {
        if (user.Nick is null) { Send(user, "! Choose a nickname first with /nick <name>"); return; }
        Broadcast(user.Room!, $"[#{user.Room}] {user.Nick}: {text}");
    }

    private void Broadcast(string room, string line, ChatUser? except = null)
    {
        if (!_rooms.TryGetValue(room, out var members)) return;
        foreach (var member in members.ToList())
            if (member != except) Send(member, line);
    }

    private void Send(ChatUser user, string line)
    {
        if (user.Gone) return;
        if (user.Client.TrySend(line)) return;
        // Slow consumer: its outbound queue is full. Drop the connection instead of buffering without bound.
        DropSlow(user);
    }

    private void DropSlow(ChatUser user)
    {
        var nick = user.Nick;
        var room = user.Room;
        user.Gone = true;
        _users.Remove(user);
        if (nick is not null) _byNick.Remove(nick);
        if (room is not null) LeaveRoom(user);
        user.Client.Close();
        if (room is not null) Broadcast(room, $"* {nick} left #{room} (too slow)");
    }
}
