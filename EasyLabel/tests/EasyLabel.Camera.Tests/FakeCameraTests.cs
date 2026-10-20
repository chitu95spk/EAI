namespace EasyLabel.Camera.Tests;

public sealed class FakeCameraTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static EasyLabel.Camera.Fake.FakeCameraSpec SmallSpec()
        => new EasyLabel.Camera.Fake.FakeCameraSpec { Width = 64, Height = 48, FrameRate = 100 };

    [Fact]
    public async Task Provider_ListsConfiguredDevices()
    {
        var provider = new EasyLabel.Camera.Fake.FakeCameraProvider(new[]
        {
            new EasyLabel.Camera.Fake.FakeCameraSpec { Id = "A", DisplayName = "Cam A", SerialNumber = "1" },
            new EasyLabel.Camera.Fake.FakeCameraSpec { Id = "B", DisplayName = "Cam B", SerialNumber = "2" },
        });

        IReadOnlyList<CameraInfo> list = await provider.EnumerateAsync(CancellationToken.None);

        Assert.Equal(new[] { "A", "B" }, list.Select(i => i.Id).ToArray());
        Assert.Equal("Cam B", list[1].DisplayName);
    }

    [Fact]
    public async Task Provider_ZeroDevices_ReturnsEmptyList()
    {
        var provider = new EasyLabel.Camera.Fake.FakeCameraProvider(Array.Empty<EasyLabel.Camera.Fake.FakeCameraSpec>());

        IReadOnlyList<CameraInfo> list = await provider.EnumerateAsync(CancellationToken.None);

        Assert.Empty(list);
    }

    [Fact]
    public void Provider_Create_UnknownId_Throws()
    {
        var provider = new EasyLabel.Camera.Fake.FakeCameraProvider();

        Assert.Throws<ArgumentException>(() => provider.Create(new CameraInfo("nope", "x", "x", "x", "x")));
    }

    [Fact]
    public async Task Stream_DeliversFramesWithRoiSizeAndIncreasingNumbers()
    {
        await using ICameraSource source = new EasyLabel.Camera.Fake.FakeCameraSource(SmallSpec());
        await source.OpenAsync(CancellationToken.None);
        Assert.Equal(CameraState.Open, source.State);

        using var cts = new CancellationTokenSource(Patience);
        var numbers = new List<long>();
        await foreach (CameraFrame frame in source.StreamAsync(cts.Token))
        {
            using (frame)
            {
                Assert.Equal(64, frame.Width);
                Assert.Equal(48, frame.Height);
                Assert.Equal(CameraPixelFormat.Bgr8, frame.PixelFormat);
                Assert.Equal(64 * 3, frame.Stride);
                Assert.Equal(64 * 48 * 3, frame.Data.Length);
                numbers.Add(frame.FrameNumber);
            }

            if (numbers.Count == 3)
            {
                break;
            }
        }

        Assert.Equal(3, numbers.Count);
        Assert.True(numbers[0] < numbers[1] && numbers[1] < numbers[2]);
    }

    [Fact]
    public async Task Stream_SlowConsumer_SkipsFrames_LatestWins()
    {
        await using ICameraSource source = new EasyLabel.Camera.Fake.FakeCameraSource(SmallSpec());
        await source.OpenAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(Patience);
        await using IAsyncEnumerator<CameraFrame> frames = source.StreamAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await frames.MoveNextAsync());
        long first = frames.Current.FrameNumber;
        frames.Current.Dispose();

        await Task.Delay(400);

        Assert.True(await frames.MoveNextAsync());
        long second = frames.Current.FrameNumber;
        frames.Current.Dispose();

        Assert.True(second - first > 3, "expected the consumer to skip frames, got " + first + " then " + second);
    }

    [Fact]
    public async Task GrabOne_ReturnsFrame_AndOwnershipPassesToCaller()
    {
        await using ICameraSource source = new EasyLabel.Camera.Fake.FakeCameraSource(SmallSpec());
        await source.OpenAsync(CancellationToken.None);

        CameraFrame frame = await source.GrabOneAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(64, frame.Width);
        frame.Dispose();
        frame.Dispose();
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = frame.Data;
        });
    }

    [Fact]
    public async Task GrabOne_WaitingForHardwareTrigger_TimesOut()
    {
        var fake = new EasyLabel.Camera.Fake.FakeCameraSource(SmallSpec());
        await using ICameraSource source = fake;
        await source.OpenAsync(CancellationToken.None);
        await fake.SetAsync(CameraParameterId.TriggerSource, 0, CancellationToken.None); // Line0
        await fake.SetAsync(CameraParameterId.TriggerMode, 1, CancellationToken.None);   // On

        await Assert.ThrowsAsync<TimeoutException>(
            () => source.GrabOneAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None));
    }

    [Fact]
    public async Task GrabOne_SoftwareTrigger_FiresAndReturnsFrame()
    {
        var fake = new EasyLabel.Camera.Fake.FakeCameraSource(SmallSpec());
        await using ICameraSource source = fake;
        await source.OpenAsync(CancellationToken.None);
        await fake.SetAsync(CameraParameterId.TriggerSource, 7, CancellationToken.None); // Software
        await fake.SetAsync(CameraParameterId.TriggerMode, 1, CancellationToken.None);   // On

        using CameraFrame frame = await source.GrabOneAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(64, frame.Width);
    }

    [Fact]
    public async Task GrabOne_Cancelled_ThrowsOperationCanceled()
    {
        var fake = new EasyLabel.Camera.Fake.FakeCameraSource(SmallSpec());
        await using ICameraSource source = fake;
        await source.OpenAsync(CancellationToken.None);
        await fake.SetAsync(CameraParameterId.TriggerSource, 0, CancellationToken.None);
        await fake.SetAsync(CameraParameterId.TriggerMode, 1, CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.GrabOneAsync(TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task GrabOne_RejectsNonPositiveTimeout()
    {
        await using ICameraSource source = new EasyLabel.Camera.Fake.FakeCameraSource(SmallSpec());
        await source.OpenAsync(CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => source.GrabOneAsync(TimeSpan.Zero, CancellationToken.None));
    }

    [Fact]
    public async Task Stream_CancelledToken_ThrowsOperationCanceled()
    {
        await using ICameraSource source = new EasyLabel.Camera.Fake.FakeCameraSource(SmallSpec());
        await source.OpenAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (CameraFrame frame in source.StreamAsync(cts.Token))
            {
                frame.Dispose();
            }
        });
    }

    [Fact]
    public async Task GrabOne_BeforeOpen_Throws()
    {
        await using ICameraSource source = new EasyLabel.Camera.Fake.FakeCameraSource(SmallSpec());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.GrabOneAsync(TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task OpenFailure_ThrowsAndLeavesSourceClosed()
    {
        EasyLabel.Camera.Fake.FakeCameraSpec spec = SmallSpec();
        spec.FailOnOpen = true;
        spec.OpenFailureMessage = "device busy";
        await using ICameraSource source = new EasyLabel.Camera.Fake.FakeCameraSource(spec);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.OpenAsync(CancellationToken.None));

        Assert.Contains("device busy", ex.Message);
        Assert.Equal(CameraState.Closed, source.State);
    }

    [Fact]
    public async Task SimulatedFault_RaisesEvent_FailsStream_AndAllowsReopen()
    {
        var fake = new EasyLabel.Camera.Fake.FakeCameraSource(SmallSpec());
        await using ICameraSource source = fake;
        var raised = new TaskCompletionSource<CameraFaultEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Faulted -= OnFaulted;
        source.Faulted += OnFaulted;
        void OnFaulted(object? sender, CameraFaultEventArgs e) => raised.TrySetResult(e);

        await source.OpenAsync(CancellationToken.None);
        Task<Exception?> consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (CameraFrame frame in source.StreamAsync(CancellationToken.None))
                {
                    frame.Dispose();
                }

                return (Exception?)null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        });
        await Task.Delay(100);

        fake.SimulateFault("cable pulled");

        CameraFaultEventArgs args = await raised.Task.WaitAsync(Patience);
        Assert.Equal("cable pulled", args.Message);
        Assert.Equal(CameraState.Faulted, source.State);
        Exception? streamError = await consumer.WaitAsync(Patience);
        Assert.IsType<InvalidOperationException>(streamError);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.GrabOneAsync(TimeSpan.FromSeconds(1), CancellationToken.None));

        await source.OpenAsync(CancellationToken.None);
        Assert.Equal(CameraState.Open, source.State);
        using CameraFrame frameAfter = await source.GrabOneAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(64, frameAfter.Width);
    }

    [Fact]
    public async Task RepeatedGrabErrors_RaiseFaulted()
    {
        var fake = new EasyLabel.Camera.Fake.FakeCameraSource(SmallSpec());
        await using ICameraSource source = fake;
        var raised = new TaskCompletionSource<CameraFaultEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Faulted -= OnFaulted;
        source.Faulted += OnFaulted;
        void OnFaulted(object? sender, CameraFaultEventArgs e) => raised.TrySetResult(e);

        await source.OpenAsync(CancellationToken.None);
        fake.SimulatePollError("boom");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.GrabOneAsync(TimeSpan.FromSeconds(10), CancellationToken.None));

        CameraFaultEventArgs args = await raised.Task.WaitAsync(Patience);
        Assert.Contains("boom", args.Message);
        Assert.Equal(CameraState.Faulted, source.State);
    }

    [Fact]
    public async Task NoDataStreak_WithDeadDevice_RaisesFaulted()
    {
        EasyLabel.Camera.Fake.FakeCameraSpec spec = SmallSpec();
        spec.LivenessWindow = TimeSpan.FromMilliseconds(200);
        var fake = new EasyLabel.Camera.Fake.FakeCameraSource(spec);
        await using ICameraSource source = fake;
        var raised = new TaskCompletionSource<CameraFaultEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Faulted -= OnFaulted;
        source.Faulted += OnFaulted;
        void OnFaulted(object? sender, CameraFaultEventArgs e) => raised.TrySetResult(e);
        await source.OpenAsync(CancellationToken.None);
        using (await source.GrabOneAsync(TimeSpan.FromSeconds(5), CancellationToken.None))
        {
        }

        fake.SimulateDisconnect();
        Task<CameraFrame> pending = source.GrabOneAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        CameraFaultEventArgs args = await raised.Task.WaitAsync(Patience);
        Assert.Contains("disconnected", args.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CameraState.Faulted, source.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
    }

    [Fact]
    public async Task NoDataStreak_WithLiveDevice_DoesNotFault()
    {
        EasyLabel.Camera.Fake.FakeCameraSpec spec = SmallSpec();
        spec.LivenessWindow = TimeSpan.FromMilliseconds(100);
        var fake = new EasyLabel.Camera.Fake.FakeCameraSource(spec);
        await using ICameraSource source = fake;
        await source.OpenAsync(CancellationToken.None);
        await fake.SetAsync(CameraParameterId.TriggerSource, 0, CancellationToken.None);
        await fake.SetAsync(CameraParameterId.TriggerMode, 1, CancellationToken.None);

        await Assert.ThrowsAsync<TimeoutException>(
            () => source.GrabOneAsync(TimeSpan.FromMilliseconds(600), CancellationToken.None));

        Assert.NotEqual(CameraState.Faulted, source.State);
    }

    [Fact]
    public async Task StuckWorker_CloseKeepsThreadRegistered_AndReopenIsRefusedUntilItEnds()
    {
        EasyLabel.Camera.Fake.FakeCameraSpec spec = SmallSpec();
        spec.WorkerJoinTimeoutMilliseconds = 200;
        var fake = new EasyLabel.Camera.Fake.FakeCameraSource(spec);
        await using ICameraSource source = fake;
        await source.OpenAsync(CancellationToken.None);
        fake.SimulateStuckWorker();
        Task<CameraFrame> pending = source.GrabOneAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        await Task.Delay(150); // the worker is now blocked inside Poll

        await source.CloseAsync();

        Assert.Equal(CameraState.Closed, source.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.OpenAsync(CancellationToken.None));

        fake.ReleaseStuckWorker();
        await source.CloseAsync();
        await source.OpenAsync(CancellationToken.None);
        Assert.Equal(CameraState.Open, source.State);
        using CameraFrame frame = await source.GrabOneAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(64, frame.Width);
        await Task.WhenAny(pending, Task.Delay(100)); // the first grab ended with the close; its outcome is irrelevant
    }

    [Fact]
    public async Task Close_EndsStreamGracefully_AndIsIdempotent()
    {
        await using ICameraSource source = new EasyLabel.Camera.Fake.FakeCameraSource(SmallSpec());
        await source.OpenAsync(CancellationToken.None);
        int received = 0;
        Task consumer = Task.Run(async () =>
        {
            await foreach (CameraFrame frame in source.StreamAsync(CancellationToken.None))
            {
                frame.Dispose();
                Interlocked.Increment(ref received);
            }
        });
        await Task.Delay(150);

        await source.CloseAsync();
        await source.CloseAsync();

        await consumer.WaitAsync(Patience);
        Assert.Equal(CameraState.Closed, source.State);
        Assert.True(received > 0);
    }

    [Fact]
    public async Task Dispose_IsIdempotent_AndBlocksReopen()
    {
        ICameraSource source = new EasyLabel.Camera.Fake.FakeCameraSource(SmallSpec());
        await source.OpenAsync(CancellationToken.None);

        await source.DisposeAsync();
        await source.DisposeAsync();
        await source.CloseAsync();

        Assert.Equal(CameraState.Closed, source.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.OpenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Dispose_WithoutOpen_DoesNotThrow()
    {
        ICameraSource source = new EasyLabel.Camera.Fake.FakeCameraSource(SmallSpec());

        await source.DisposeAsync();

        Assert.Equal(CameraState.Closed, source.State);
    }

    [Fact]
    public async Task Open_Twice_IsHarmless()
    {
        await using ICameraSource source = new EasyLabel.Camera.Fake.FakeCameraSource(SmallSpec());

        await source.OpenAsync(CancellationToken.None);
        await source.OpenAsync(CancellationToken.None);

        Assert.Equal(CameraState.Open, source.State);
    }

    [Fact]
    public async Task IdleCamera_StopsGrabbing_AfterLastConsumer()
    {
        var fake = new EasyLabel.Camera.Fake.FakeCameraSource(SmallSpec());
        await using ICameraSource source = fake;
        await source.OpenAsync(CancellationToken.None);
        using (await source.GrabOneAsync(TimeSpan.FromSeconds(5), CancellationToken.None))
        {
        }

        await Task.Delay(700);
        long produced = fake.FramesProduced;
        await Task.Delay(300);

        Assert.Equal(produced, fake.FramesProduced);
        Assert.Equal(CameraState.Open, source.State);
    }
}
