using System.Text;

namespace ChatServer.Tests;

public class LineReaderTests
{
    private static LineReader Reader(string text, int max = 100) => new(new MemoryStream(Encoding.UTF8.GetBytes(text)), max);

    [Fact]
    public async Task ReadsLines_WithLfOrCrLf_AndFinalUnterminatedLine()
    {
        var r = Reader("one\r\ntwo\nthree");
        Assert.Equal("one", await r.ReadLineAsync(default));
        Assert.Equal("two", await r.ReadLineAsync(default));
        Assert.Equal("three", await r.ReadLineAsync(default));
        Assert.Null(await r.ReadLineAsync(default));
    }

    [Fact]
    public async Task EmptyLines_AreReturned()
    {
        var r = Reader("\n\r\nx\n");
        Assert.Equal("", await r.ReadLineAsync(default));
        Assert.Equal("", await r.ReadLineAsync(default));
        Assert.Equal("x", await r.ReadLineAsync(default));
    }

    [Fact]
    public async Task Utf8_IsDecoded() => Assert.Equal("olá 日本", await Reader("olá 日本\n").ReadLineAsync(default));

    [Fact]
    public async Task LineLongerThanLimit_Throws()
    {
        var r = Reader(new string('a', 50) + "\n", max: 49);
        await Assert.ThrowsAsync<LineTooLongException>(() => r.ReadLineAsync(default));
    }

    [Fact]
    public async Task LineExactlyAtLimit_IsAccepted() =>
        Assert.Equal(new string('a', 49), await Reader(new string('a', 49) + "\n", max: 49).ReadLineAsync(default));

    [Fact]
    public async Task NoNewlineEver_IsBoundedByLimit()
    {
        var r = Reader(new string('z', 10_000), max: 100);
        await Assert.ThrowsAsync<LineTooLongException>(() => r.ReadLineAsync(default));
    }

    [Fact]
    public async Task LinesSpanningManyReads_AreReassembled()
    {
        var text = string.Join("\n", Enumerable.Range(0, 2000).Select(i => $"line-{i}-" + new string('x', i % 30))) + "\n";
        var r = Reader(text, max: 200);
        for (var i = 0; i < 2000; i++) Assert.Equal($"line-{i}-" + new string('x', i % 30), await r.ReadLineAsync(default));
        Assert.Null(await r.ReadLineAsync(default));
    }
}
