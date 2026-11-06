using System.Net;
using ChatServer;

// Usage: chatserver [--port 5000] [--bind 127.0.0.1]
// Connect with: nc 127.0.0.1 5000   (or telnet)
string Arg(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

if (!int.TryParse(Arg("--port", "5000"), out var port) || !IPAddress.TryParse(Arg("--bind", "127.0.0.1"), out var address))
{
    Console.Error.WriteLine("usage: chatserver [--port 5000] [--bind 127.0.0.1]");
    return 2;
}

await using var server = new ChatTcpServer(new IPEndPoint(address, port));
server.Start();
Console.WriteLine($"ChatServer listening on {address}:{server.Port} (Ctrl+C to stop)");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
await stop.Task;

Console.WriteLine("shutting down...");
await server.StopAsync();
return 0;
