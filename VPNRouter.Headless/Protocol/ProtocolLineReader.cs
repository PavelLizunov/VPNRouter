namespace VPNRouter.Headless.Protocol;

public enum LineReadStatus
{
    Success,
    Oversized,
    Eof
}

public readonly struct LineReadResult
{
    public LineReadStatus Status { get; }
    public ReadOnlyMemory<byte> Memory { get; }
    public ReadOnlySpan<byte> Bytes => Memory.Span;

    public LineReadResult(LineReadStatus status, ReadOnlyMemory<byte> memory)
    {
        Status = status;
        Memory = memory;
    }

    public static LineReadResult Success(ReadOnlyMemory<byte> memory) => new(LineReadStatus.Success, memory);
    public static LineReadResult Oversized() => new(LineReadStatus.Oversized, ReadOnlyMemory<byte>.Empty);
    public static LineReadResult Eof() => new(LineReadStatus.Eof, ReadOnlyMemory<byte>.Empty);
}

public sealed class ProtocolLineReader
{
    private readonly Stream _input;
    private readonly byte[] _readChunk = new byte[8192];
    private readonly byte[] _lineBuffer = new byte[ProtocolConstants.MaxInputFrameBytes];
    private int _chunkOffset;
    private int _chunkAvailable;
    private int _lineLength;
    private bool _isOversized;
    private Task<int>? _pendingReadTask;
    private bool _isCancelled;

    public ProtocolLineReader(Stream input)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
    }

    /// <summary>
    /// Reads the next newline-delimited line. If an input line exceeds 256 KiB before encountering
    /// a newline, the reader stops accumulating, discards the excess bytes until the newline,
    /// and returns LineReadStatus.Oversized.
    /// </summary>
    public async ValueTask<LineReadResult> ReadLineAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_isCancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        _lineLength = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_chunkOffset >= _chunkAvailable)
            {
                _chunkOffset = 0;

                if (_pendingReadTask == null)
                {
                    // Start at most one pending read task on the underlying stream.
                    // Underlying Console read (fd 0) is noncancellable at the OS syscall level,
                    // so we read with CancellationToken.None and await it cancellably via WaitAsync(cancellationToken).
                    var vt = _input.ReadAsync(_readChunk.AsMemory(), CancellationToken.None);
                    if (vt.IsCompletedSuccessfully)
                    {
                        _chunkAvailable = vt.Result;
                    }
                    else
                    {
                        _pendingReadTask = vt.AsTask();
                    }
                }

                if (_pendingReadTask != null)
                {
                    try
                    {
                        _chunkAvailable = await _pendingReadTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                        _pendingReadTask = null;
                    }
                    catch (OperationCanceledException)
                    {
                        // Cancellation observed while readTask is pending in underlying blocking read.
                        // Underlying Console read cannot be interrupted at OS level;
                        // mark reader cancelled so _readChunk buffer is not reused and no subsequent
                        // reads are initiated. Process exit will release OS fd without mutating fd0.
                        _isCancelled = true;
                        throw;
                    }
                }

                if (_chunkAvailable == 0)
                {
                    // EOF reached
                    if (_isOversized)
                    {
                        _isOversized = false;
                        return LineReadResult.Oversized();
                    }

                    if (_lineLength > 0)
                    {
                        var resultSpan = TrimTrailingCr(_lineBuffer.AsSpan(0, _lineLength));
                        return LineReadResult.Success(resultSpan.ToArray());
                    }

                    return LineReadResult.Eof();
                }
            }

            // Search for newline in the current chunk
            int newlineIndex = -1;
            for (int i = _chunkOffset; i < _chunkAvailable; i++)
            {
                if (_readChunk[i] == (byte)'\n')
                {
                    newlineIndex = i;
                    break;
                }
            }

            if (newlineIndex >= 0)
            {
                int bytesToNewline = newlineIndex - _chunkOffset;

                if (_isOversized)
                {
                    // End of oversized line
                    _chunkOffset = newlineIndex + 1;
                    _isOversized = false;
                    _lineLength = 0;
                    return LineReadResult.Oversized();
                }

                if (_lineLength + bytesToNewline > ProtocolConstants.MaxInputFrameBytes)
                {
                    _chunkOffset = newlineIndex + 1;
                    _lineLength = 0;
                    return LineReadResult.Oversized();
                }

                _readChunk.AsSpan(_chunkOffset, bytesToNewline).CopyTo(_lineBuffer.AsSpan(_lineLength));
                _lineLength += bytesToNewline;
                _chunkOffset = newlineIndex + 1;

                var finalSpan = TrimTrailingCr(_lineBuffer.AsSpan(0, _lineLength));
                return LineReadResult.Success(finalSpan.ToArray());
            }
            else
            {
                // Newline not in this chunk
                int remainingInChunk = _chunkAvailable - _chunkOffset;

                if (_isOversized)
                {
                    // Discard without accumulating
                    _chunkOffset = _chunkAvailable;
                    continue;
                }

                if (_lineLength + remainingInChunk > ProtocolConstants.MaxInputFrameBytes)
                {
                    _isOversized = true;
                    _lineLength = 0;
                    _chunkOffset = _chunkAvailable;
                    continue;
                }

                _readChunk.AsSpan(_chunkOffset, remainingInChunk).CopyTo(_lineBuffer.AsSpan(_lineLength));
                _lineLength += remainingInChunk;
                _chunkOffset = _chunkAvailable;
            }
        }
    }

    private static ReadOnlySpan<byte> TrimTrailingCr(ReadOnlySpan<byte> span)
    {
        if (span.Length > 0 && span[^1] == (byte)'\r')
        {
            return span[..^1];
        }
        return span;
    }
}
