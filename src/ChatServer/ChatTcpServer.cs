using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace ChatServer;

public sealed record ChatServerOptions
{
    public int MaxClients { get; init; } = 1000;
    /// <summary>Maximum size in bytes of one incoming line.</summary>
    public int MaxLineLength { get; init; } = 1024;
    /// <summary>Outgoing lines buffered per client before it is considered too slow and disconnected.</summary>
    public int OutboundQueueSize { get; init; } = 256;
    /// <summary>How long shutdown waits for clients to flush pending output before forcing sockets closed.</summary>
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(2);
    public ChatOptions Chat { get; init; } = new();
}

/// <summary>
/// Asynchronous TCP front-end for <see cref="ChatHub"/>. Each connection has a reader loop (parses lines and feeds the
/// hub) and a writer task fed by a bounded <see cref="Channel{T}"/>, so broadcasting never waits on a slow socket.
/// </summary>
public sealed class ChatTcpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly ChatServerOptions _options;
    private readonly ChatHub _hub;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Session, Task> _sessions = new();
    private Task _acceptLoop = Task.CompletedTask;
    private bool _started;
    private Task? _stopTask;
    private readonly object _stopGate = new();

    public ChatTcpServer(IPEndPoint endpoint, ChatServerOptions? options = null)
    {
        _listener = new TcpListener(endpoint);
        _options = options ?? new ChatServerOptions();
        _hub = new ChatHub(_options.Chat);
    }

    public ChatHub Hub => _hub;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public int ConnectionCount => _sessions.Count;

    public void Start()
    {
        if (_started) throw new InvalidOperationException("Already started.");
        _started = true;
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Announces shutdown, lets each client flush its pending output, then closes everything.</summary>
    public Task StopAsync()
    {
        lock (_stopGate) return _stopTask ??= StopCoreAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private async Task StopCoreAsync()
    {
        if (!_started) return;
        _cts.Cancel();
        _listener.Stop(); // stop accepting first
        _hub.Announce("Server is shutting down. Goodbye!");
        foreach (var s in _sessions.Keys) s.Close(); // flush queued lines, then close
        try { await Task.WhenAll(_sessions.Values).WaitAsync(_options.ShutdownGrace).ConfigureAwait(false); }
        catch (TimeoutException) { /* sessions dispose their own sockets once their grace period ends */ }
        await _acceptLoop.ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (_cts.IsCancellationRequested) break; continue; }
            catch (InvalidOperationException) { break; } // listener stopped

            tcp.NoDelay = true;
            if (_sessions.Count >= _options.MaxClients)
            {
                _ = RejectAsync(tcp);
                continue;
            }
            var session = new Session(tcp, _hub, _options);
            var task = Task.Run(session.RunAsync);
            _sessions[session] = task;
            _ = task.ContinueWith(_ => _sessions.TryRemove(session, out Task? _), TaskScheduler.Default);
        }
    }

    private static async Task RejectAsync(TcpClient tcp)
    {
        try
        {
            using (tcp)
            {
                await tcp.GetStream().WriteAsync(Encoding.UTF8.GetBytes("! Server is full. Try again later.\r\n")).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
    }

    private sealed class Session : IChatClient
    {
        private readonly TcpClient _tcp;
        private readonly ChatHub _hub;
        private readonly ChatServerOptions _options;
        private readonly Channel<string> _outbound;
        private readonly CancellationTokenSource _closed = new();

        public Session(TcpClient tcp, ChatHub hub, ChatServerOptions options)
        {
            _tcp = tcp;
            _hub = hub;
            _options = options;
            _outbound = Channel.CreateBounded<string>(new BoundedChannelOptions(options.OutboundQueueSize)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait // TryWrite then reports false when full
            });
        }

        public bool TrySend(string line) => _outbound.Writer.TryWrite(line);

        /// <summary>Stops reading and finishes after the queued output has been written.</summary>
        public void Close()
        {
            _outbound.Writer.TryComplete();
            try { _closed.Cancel(); } catch (ObjectDisposedException) { }
        }

        public async Task RunAsync()
        {
            var user = _hub.Connect(this);
            var writer = Task.Run(WriteLoopAsync);
            try
            {
                var reader = new LineReader(_tcp.GetStream(), _options.MaxLineLength);
                while (!_closed.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_closed.Token).ConfigureAwait(false);
                    if (line is null) break; // client closed its side
                    if (!_hub.HandleLine(user, line)) break;
                }
            }
            catch (LineTooLongException ex)
            {
                TrySend($"! {ex.Message}; disconnecting");
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // connection dropped or server stopping
            }
            finally
            {
                _hub.Disconnect(user);
                _outbound.Writer.TryComplete();
                // Give the writer a moment to flush goodbye messages, then tear the socket down.
                await Task.WhenAny(writer, Task.Delay(_options.ShutdownGrace)).ConfigureAwait(false);
                _tcp.Dispose();
                _closed.Dispose();
            }
        }

        private async Task WriteLoopAsync()
        {
            try
            {
                var stream = _tcp.GetStream();
                await foreach (var line in _outbound.Reader.ReadAllAsync().ConfigureAwait(false))
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n")).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                // peer went away; reader loop will notice too
            }
        }
    }
}
