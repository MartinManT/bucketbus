namespace BucketBus.S3;

// A seekable bounded view of an already pinned file; SDK signing can rewind this part only.
// Disposing the view never releases the source lease. No payload buffering or temporary copy.
internal sealed class PartStream(Stream source, long offset, long length) : Stream
{
    private long _position;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public override long Seek(long offsetValue, SeekOrigin origin)
    {
        var value = checked(origin switch { SeekOrigin.Begin => offsetValue, SeekOrigin.Current => _position + offsetValue, SeekOrigin.End => length + offsetValue, _ => throw new ArgumentException() });
        if (value < 0 || value > length) throw new IOException();
        _position = value; return value;
    }
    public override int Read(byte[] buffer, int start, int count)
    {
        source.Position = checked(offset + _position);
        var n = source.Read(buffer, start, (int)Math.Min(count, length - _position)); _position += n; return n;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        source.Position = checked(offset + _position);
        var n = await source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, length - _position)], token).ConfigureAwait(false); _position += n; return n;
    }
    public override Task<int> ReadAsync(byte[] buffer, int start, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(start, count), token).AsTask();
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int start, int count) => throw new NotSupportedException();
}
