using System.Text;

namespace ChatServer;

public sealed class LineTooLongException : Exception
{
    public LineTooLongException(int limit) : base($"Line exceeds {limit} bytes") { }
}

/// <summary>
/// Reads '\n'-terminated UTF-8 lines from a stream with a hard length limit, so a client that never sends a newline
/// cannot make the server buffer unbounded data (unlike <see cref="StreamReader.ReadLineAsync()"/>).
/// </summary>
public sealed class LineReader
{
    private readonly Stream _stream;
    private readonly int _maxLength;
    private readonly byte[] _buffer = new byte[2048];
    private int _start, _end;

    public LineReader(Stream stream, int maxLength)
    {
        _stream = stream;
        _maxLength = maxLength;
    }

    /// <summary>Returns the next line without its terminator, or null at end of stream.</summary>
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var line = new List<byte>();
        while (true)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = await _stream.ReadAsync(_buffer.AsMemory(), ct).ConfigureAwait(false);
                if (_end == 0)
                {
                    // EOF: deliver a final unterminated line if there is one.
                    return line.Count > 0 ? Decode(line) : null;
                }
            }

            var nl = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            var take = (nl < 0 ? _end : nl) - _start;
            if (line.Count + take > _maxLength) throw new LineTooLongException(_maxLength);
            line.AddRange(new ArraySegment<byte>(_buffer, _start, take));
            if (nl >= 0)
            {
                _start = nl + 1;
                return Decode(line);
            }
            _start = _end;
        }
    }

    private static string Decode(List<byte> bytes)
    {
        if (bytes.Count > 0 && bytes[^1] == '\r') bytes.RemoveAt(bytes.Count - 1);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
