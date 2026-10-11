namespace TerraScale.Reviewer.Features.Context;

// Limit decompressed input even when ignored files or archive metadata are oversized.
internal sealed class BoundedArchiveStream(Stream inner) : Stream
{
    private const long MaximumBytes = 512L * 1024 * 1024;
    private long _read;
    private int Count(int count)
    {
        if ((_read += count) > MaximumBytes) throw new InvalidOperationException("repository archive exceeds the fixed decompression limit");
        return count;
    }
    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken));
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
