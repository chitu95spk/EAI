namespace EasyLabel.Camera;

/// <summary>
/// Single-slot, latest-frame-wins hand-off between the grab thread and one consumer. Posting replaces (and
/// disposes) a frame the consumer has not taken. A frame returned from <see cref="TakeAsync"/> belongs to the caller.
/// </summary>
internal sealed class FrameMailbox
{
    private readonly object _lock = new();
    private readonly SemaphoreSlim _signal = new(0);
    private CameraFrame? _latest;
    private bool _signaled;
    private bool _completed;
    private string? _faultMessage;
    private Exception? _faultInner;

    /// <summary>Stores the frame. Ownership moves to the mailbox; a closed mailbox disposes it.</summary>
    internal void Post(CameraFrame frame)
    {
        lock (_lock)
        {
            if (_completed || _faultMessage != null)
            {
                frame.Dispose();
                return;
            }

            _latest?.Dispose();
            _latest = frame;
            Wake();
        }
    }

    /// <summary>Drops a pending frame so the next frame taken is fresh.</summary>
    internal void Clear()
    {
        lock (_lock)
        {
            _latest?.Dispose();
            _latest = null;
        }
    }

    /// <summary>Ends the stream: waiting and later takers get null.</summary>
    internal void Complete()
    {
        lock (_lock)
        {
            _completed = true;
            _latest?.Dispose();
            _latest = null;
            Wake();
        }
    }

    /// <summary>Ends the stream with an error: waiting and later takers get an InvalidOperationException.</summary>
    internal void Fail(string message, Exception? inner)
    {
        lock (_lock)
        {
            _faultMessage = message;
            _faultInner = inner;
            _latest?.Dispose();
            _latest = null;
            Wake();
        }
    }

    /// <summary>Re-arms the mailbox for a new Open.</summary>
    internal void Reset()
    {
        lock (_lock)
        {
            _latest?.Dispose();
            _latest = null;
            _completed = false;
            _faultMessage = null;
            _faultInner = null;
            _signaled = false;
            while (_signal.CurrentCount > 0)
            {
                _signal.Wait(0);
            }
        }
    }

    /// <summary>Waits for the next frame. Returns null when completed; throws on fault or cancellation.</summary>
    internal async Task<CameraFrame?> TakeAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                _signaled = false;
                if (_faultMessage != null)
                {
                    Wake(); // let other waiters see the fault too
                    throw new InvalidOperationException(_faultMessage, _faultInner);
                }

                CameraFrame? frame = _latest;
                _latest = null;
                if (frame != null)
                {
                    return frame;
                }

                if (_completed)
                {
                    Wake();
                    return null;
                }
            }
        }
    }

    // Must be called with _lock held. Keeps at most one pending release on the semaphore.
    private void Wake()
    {
        if (!_signaled)
        {
            _signaled = true;
            _signal.Release();
        }
    }
}
