using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GitSweep.Infrastructure.Services;
using Xunit;

namespace GitSweep.Infrastructure.Tests;

public sealed class BoundedFileReaderTests : IDisposable
{
    private const int Cap = 1000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GitSweepBoundedReadTests", Guid.NewGuid().ToString("N"));

    public BoundedFileReaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception)
        {
            // ignore cleanup errors in tests
        }
    }

    private static MemoryStream Bytes(int count) => new(new byte[count]);

    [Theory]
    [InlineData(1)]
    [InlineData(Cap - 1)]
    [InlineData(Cap)]
    public async Task ReadFromStreamAsync_returns_content_up_to_the_cap(int size)
    {
        var result = await BoundedFileReader.ReadFromStreamAsync(Bytes(size), Cap, CancellationToken.None);

        Assert.Equal(BoundedReadStatus.Ok, result.Status);
        Assert.Equal(size, result.Content!.Length);
    }

    [Fact]
    public async Task ReadFromStreamAsync_reports_one_byte_over_the_cap_as_too_large()
    {
        var result = await BoundedFileReader.ReadFromStreamAsync(Bytes(Cap + 1), Cap, CancellationToken.None);

        Assert.Equal(BoundedReadStatus.TooLarge, result.Status);
        Assert.Null(result.Content);
    }

    [Fact]
    public async Task ReadFromStreamAsync_empty_stream_is_empty()
    {
        var result = await BoundedFileReader.ReadFromStreamAsync(Bytes(0), Cap, CancellationToken.None);

        Assert.Equal(BoundedReadStatus.Empty, result.Status);
        Assert.Null(result.Content);
    }

    [Fact]
    public async Task ReadFromStreamAsync_never_reads_more_than_cap_plus_one_bytes_from_an_endless_stream()
    {
        // A stream that never ends and lies about its size must still cost at
        // most cap + 1 bytes: the bound is the read itself, not a size check.
        using var endless = new EndlessStream();

        var result = await BoundedFileReader.ReadFromStreamAsync(endless, Cap, CancellationToken.None);

        Assert.Equal(BoundedReadStatus.TooLarge, result.Status);
        Assert.Equal(Cap + 1, endless.BytesServed);
    }

    [Fact]
    public async Task ReadAsync_reads_a_regular_file_unchanged()
    {
        var path = Path.Combine(_dir, "regular.json");
        byte[] bytes = [0xEF, 0xBB, 0xBF, (byte)'{', (byte)'}'];
        await File.WriteAllBytesAsync(path, bytes);

        var result = await BoundedFileReader.ReadAsync(path, Cap, CancellationToken.None);

        Assert.Equal(BoundedReadStatus.Ok, result.Status);
        Assert.Equal(bytes, result.Content!.ToArray());
    }

    [Fact]
    public async Task ReadAsync_reports_empty_and_oversized_files_without_content()
    {
        var empty = Path.Combine(_dir, "empty.json");
        await File.WriteAllBytesAsync(empty, []);
        var large = Path.Combine(_dir, "large.json");
        await File.WriteAllBytesAsync(large, new byte[Cap + 1]);

        var emptyResult = await BoundedFileReader.ReadAsync(empty, Cap, CancellationToken.None);
        var largeResult = await BoundedFileReader.ReadAsync(large, Cap, CancellationToken.None);

        Assert.Equal(BoundedReadStatus.Empty, emptyResult.Status);
        Assert.Null(emptyResult.Content);
        Assert.Equal(BoundedReadStatus.TooLarge, largeResult.Status);
        Assert.Null(largeResult.Content);
    }

    [FileSymlinkFact]
    public async Task ReadAsync_does_not_follow_a_symbolic_link()
    {
        var target = Path.Combine(_dir, "target.json");
        await File.WriteAllTextAsync(target, "{}");
        var link = Path.Combine(_dir, "link.json");
        TestLinks.CreateFileSymlink(link, target);

        var result = await BoundedFileReader.ReadAsync(link, Cap, CancellationToken.None);

        Assert.Equal(BoundedReadStatus.Link, result.Status);
        Assert.Null(result.Content);
    }

    [FifoFact]
    public async Task ReadAsync_reports_a_fifo_as_empty_without_opening_it()
    {
        var fifo = Path.Combine(_dir, "pipe.json");
        TestFifo.Create(fifo);

        var result = await TestFifo.CompleteWithinTimeoutAsync(
            fifo, () => BoundedFileReader.ReadAsync(fifo, Cap, CancellationToken.None));

        Assert.Equal(BoundedReadStatus.Empty, result.Status);
        Assert.Null(result.Content);
    }

    /// <summary>A readable stream that never reaches end of file.</summary>
    private sealed class EndlessStream : Stream
    {
        public long BytesServed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            BytesServed += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            BytesServed += buffer.Length;
            return ValueTask.FromResult(buffer.Length);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
