namespace EasyLabel.Camera.Tests;

public sealed class FrameMailboxTests
{
    private static CameraFrame Frame(long number)
        => CameraFrame.CopyFrom(new byte[] { 1 }, 1, 1, 1, CameraPixelFormat.Mono8, number, DateTime.UtcNow);

    [Fact]
    public async Task Post_Twice_DisposesTheStaleFrame_AndKeepsTheNewest()
    {
        var mailbox = new FrameMailbox();
        CameraFrame stale = Frame(1);
        CameraFrame fresh = Frame(2);

        mailbox.Post(stale);
        mailbox.Post(fresh);
        CameraFrame? taken = await mailbox.TakeAsync(CancellationToken.None);

        Assert.Same(fresh, taken);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = stale.Data;
        });
        taken!.Dispose();
    }

    [Fact]
    public async Task Take_WaitsUntilAFrameIsPosted()
    {
        var mailbox = new FrameMailbox();
        Task<CameraFrame?> pending = mailbox.TakeAsync(CancellationToken.None);
        Assert.False(pending.IsCompleted);

        mailbox.Post(Frame(5));

        CameraFrame? taken = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(5, taken!.FrameNumber);
        taken.Dispose();
    }

    [Fact]
    public async Task Complete_ReleasesWaiterWithNull_AndDropsPendingFrame()
    {
        var mailbox = new FrameMailbox();
        Task<CameraFrame?> pending = mailbox.TakeAsync(CancellationToken.None);

        mailbox.Complete();

        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        CameraFrame late = Frame(9);
        mailbox.Post(late); // a closed mailbox disposes what it is given
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = late.Data;
        });
    }

    [Fact]
    public async Task Fail_ThrowsInvalidOperation_ForWaiter()
    {
        var mailbox = new FrameMailbox();
        Task<CameraFrame?> pending = mailbox.TakeAsync(CancellationToken.None);

        mailbox.Fail("lost", new IOException("inner"));

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("lost", ex.Message);
        Assert.IsType<IOException>(ex.InnerException);
    }

    [Fact]
    public async Task Reset_AllowsReuseAfterComplete()
    {
        var mailbox = new FrameMailbox();
        mailbox.Complete();
        Assert.Null(await mailbox.TakeAsync(CancellationToken.None));

        mailbox.Reset();
        mailbox.Post(Frame(3));

        CameraFrame? taken = await mailbox.TakeAsync(CancellationToken.None);
        Assert.Equal(3, taken!.FrameNumber);
        taken.Dispose();
    }

    [Fact]
    public async Task Clear_DropsPendingFrame()
    {
        var mailbox = new FrameMailbox();
        CameraFrame frame = Frame(1);
        mailbox.Post(frame);

        mailbox.Clear();

        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = frame.Data;
        });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mailbox.TakeAsync(cts.Token));
    }
}
