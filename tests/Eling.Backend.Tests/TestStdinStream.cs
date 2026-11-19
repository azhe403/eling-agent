using System.Collections.Concurrent;

namespace Eling.Backend.Tests;

/// <summary>
/// A minimal blocking stdin pipe feeding CliWrap's <c>PipeSource.FromStream</c>.
/// <c>Read</c> returns buffered bytes and reports EOF as a zero-length read once
/// <see cref="CompleteWriting"/> is called and the buffer is drained, so the
/// pump closes the child's stdin exactly when the test is done writing.
/// </summary>
public sealed class TestStdinStream : Stream
{
    private readonly BlockingCollection<byte[]> _chunks = new();
    private byte[]? _current;
    private int _offset;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Writes one line (UTF-8, no BOM) into the child's stdin.</summary>
    public void WriteLine(string line) =>
        Write(System.Text.Encoding.UTF8.GetBytes(line + "\n"));

    /// <summary>Signals EOF: the child sees end-of-input once the buffer drains.</summary>
    public void CompleteWriting() => _chunks.CompleteAdding();

    public override void Write(byte[] buffer, int offset, int count)
    {
        var copy = new byte[count];
        Buffer.BlockCopy(buffer, offset, copy, 0, count);
        _chunks.Add(copy);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return;
        _chunks.Add(buffer.ToArray());
        await Task.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_current is null || _offset >= _current.Length)
        {
            _current = null;
            _offset = 0;
            while (true)
            {
                if (_chunks.TryTake(out var chunk, Timeout.Infinite)) { _current = chunk; break; }
                if (_chunks.IsCompleted) return 0; // EOF
            }
        }

        var n = Math.Min(count, _current.Length - _offset);
        Buffer.BlockCopy(_current, _offset, buffer, offset, n);
        _offset += n;
        return n;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_current is null || _offset >= _current.Length)
        {
            _current = null;
            _offset = 0;
            while (true)
            {
                if (_chunks.TryTake(out var chunk)) { _current = chunk; break; }
                if (_chunks.IsCompleted) return 0; // EOF: pump closes child stdin
                if (cancellationToken.IsCancellationRequested) return 0;
                await Task.Delay(5, CancellationToken.None);
            }
        }

        var n = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, n).CopyTo(buffer);
        _offset += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(new Memory<byte>(buffer, offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
