namespace EasyLabel.Camera.Tests;

public sealed class CameraServiceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FastRetry = TimeSpan.FromMilliseconds(20);

    private static EasyLabel.Camera.Fake.FakeCameraSpec Spec(string id = "A", string name = "Cam A")
        => new EasyLabel.Camera.Fake.FakeCameraSpec { Id = id, DisplayName = name, Width = 64, Height = 48, FrameRate = 100 };

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new Xunit.Sdk.XunitException("Timed out waiting for " + what + ".");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>Fake provider that remembers every source it created so a test can make one fault.</summary>
    private sealed class RecordingProvider : ICameraProvider
    {
        private readonly EasyLabel.Camera.Fake.FakeCameraProvider _inner;
        private readonly List<EasyLabel.Camera.Fake.FakeCameraSource> _created = new List<EasyLabel.Camera.Fake.FakeCameraSource>();

        public RecordingProvider(params EasyLabel.Camera.Fake.FakeCameraSpec[] specs)
        {
            _inner = new EasyLabel.Camera.Fake.FakeCameraProvider(specs);
        }

        public int CreatedCount
        {
            get { lock (_created) { return _created.Count; } }
        }

        public EasyLabel.Camera.Fake.FakeCameraSource Created(int index)
        {
            lock (_created) { return _created[index]; }
        }

        public Task<IReadOnlyList<CameraInfo>> EnumerateAsync(CancellationToken cancellationToken)
            => _inner.EnumerateAsync(cancellationToken);

        public ICameraSource Create(CameraInfo info)
        {
            var source = (EasyLabel.Camera.Fake.FakeCameraSource)_inner.Create(info);
            lock (_created) { _created.Add(source); }
            return source;
        }
    }

    private sealed class ThrowingProvider : ICameraProvider
    {
        public Task<IReadOnlyList<CameraInfo>> EnumerateAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("enumeration exploded");

        public ICameraSource Create(CameraInfo info) => throw new InvalidOperationException("no create");
    }

    /// <summary>A source whose open blocks until the test releases it or the open is cancelled.</summary>
    private sealed class GatedSource : CameraSourceBase
    {
        private readonly ManualResetEventSlim _release = new ManualResetEventSlim(false);

        public GatedSource(CameraInfo info)
            : base(info)
        {
        }

        public ManualResetEventSlim Entered { get; } = new ManualResetEventSlim(false);

        protected override void OnOpen(CancellationToken cancellationToken)
        {
            Entered.Set();
            _release.Wait(cancellationToken);
        }

        protected override void OnClose()
        {
        }

        protected override void OnStartGrabbing()
        {
        }

        protected override void OnStopGrabbing()
        {
        }

        protected override PollStatus Poll(out CameraFrame? frame, out string? error)
        {
            frame = null;
            error = null;
            Thread.Sleep(5);
            return PollStatus.NoData;
        }
    }

    private sealed class GatedProvider : ICameraProvider
    {
        public GatedSource? Last { get; private set; }

        public Task<IReadOnlyList<CameraInfo>> EnumerateAsync(CancellationToken cancellationToken)
        {
            IReadOnlyList<CameraInfo> list = new[] { new CameraInfo("G", "Gated", "M", "1", "V") };
            return Task.FromResult(list);
        }

        public ICameraSource Create(CameraInfo info)
        {
            Last = new GatedSource(info);
            return Last;
        }
    }

    [Fact]
    public async Task Enumerate_ListsDevices_SetsHasEnumerated_AndRaisesStateChanged()
    {
        await using var service = new CameraService(new RecordingProvider(Spec("A", "Cam A"), Spec("B", "Cam B")), FastRetry);
        int raised = 0;
        service.StateChanged += (s, e) => Interlocked.Increment(ref raised);
        Assert.False(service.HasEnumerated);

        IReadOnlyList<CameraInfo> list = await service.EnumerateAsync();

        Assert.Equal(new[] { "A", "B" }, list.Select(i => i.Id).ToArray());
        Assert.Equal(2, service.Devices.Count);
        Assert.True(service.HasEnumerated);
        Assert.Equal(string.Empty, service.EnumerationMessage);
        Assert.True(Volatile.Read(ref raised) >= 1);
    }

    [Fact]
    public async Task Enumerate_ZeroDevices_ReturnsEmptyListWithoutError()
    {
        await using var service = new CameraService(new RecordingProvider(), FastRetry);

        IReadOnlyList<CameraInfo> list = await service.EnumerateAsync();

        Assert.Empty(list);
        Assert.True(service.HasEnumerated);
        Assert.Equal(string.Empty, service.ErrorText);
        Assert.Equal(CameraServiceState.Disconnected, service.State);
    }

    [Fact]
    public async Task Enumerate_MvsRuntimeMissing_GivesEmptyListAndTheLoaderMessage()
    {
        var provider = new EasyLabel.Camera.Mvs.MvsCameraProvider(() => new EasyLabel.Camera.Mvs.MvsSdkLoadResult(
            EasyLabel.Camera.Mvs.MvsSdkStatus.NotFound, 126, "C:\\none", null, 0, "MVS runtime not found."));
        await using var service = new CameraService(provider, FastRetry);

        IReadOnlyList<CameraInfo> list = await service.EnumerateAsync();

        Assert.Empty(list);
        Assert.Equal("MVS runtime not found.", service.EnumerationMessage);
    }

    [Fact]
    public async Task Enumerate_ProviderThrows_NeverThrowsAndKeepsTheMessage()
    {
        await using var service = new CameraService(new ThrowingProvider(), FastRetry);

        IReadOnlyList<CameraInfo> list = await service.EnumerateAsync();

        Assert.Empty(list);
        Assert.Equal("enumeration exploded", service.EnumerationMessage);
    }

    [Fact]
    public async Task Connect_OpensTheCamera_ExposesParameters_AndDisconnectClosesIt()
    {
        var provider = new RecordingProvider(Spec());
        await using var service = new CameraService(provider, FastRetry);
        await service.EnumerateAsync();

        bool ok = await service.ConnectAsync("A");

        Assert.True(ok);
        Assert.Equal(CameraServiceState.Connected, service.State);
        Assert.Equal("CameraStatusConnected", service.StatusKey);
        Assert.Equal("Cam A", service.DeviceName);
        Assert.NotNull(service.Parameters);
        Assert.Equal(CameraState.Open, provider.Created(0).State);

        await service.DisconnectAsync();

        Assert.Equal(CameraServiceState.Disconnected, service.State);
        Assert.Null(service.Parameters);
        Assert.Equal(CameraState.Closed, provider.Created(0).State);
    }

    [Fact]
    public async Task Connect_UnknownDevice_FailsWithAMessage()
    {
        await using var service = new CameraService(new RecordingProvider(Spec()), FastRetry);

        bool ok = await service.ConnectAsync("missing");

        Assert.False(ok);
        Assert.Equal(CameraServiceState.Faulted, service.State);
        Assert.Contains("missing", service.ErrorText);
    }

    [Fact]
    public async Task Connect_OpenFailure_ReportsTheFailureMessageAndStaysFaulted()
    {
        var spec = Spec();
        spec.FailOnOpen = true;
        spec.OpenFailureMessage = "boom";
        await using var service = new CameraService(new RecordingProvider(spec), FastRetry);
        await service.EnumerateAsync();

        bool ok = await service.ConnectAsync("A");

        Assert.False(ok);
        Assert.Equal(CameraServiceState.Faulted, service.State);
        Assert.Equal("boom", service.ErrorText);
        Assert.Equal("CameraStatusFaulted", service.StatusKey);
        Assert.Null(service.Parameters);

        await service.DisconnectAsync();
        Assert.Equal(CameraServiceState.Disconnected, service.State);
        Assert.Equal(string.Empty, service.ErrorText);
    }

    [Fact]
    public async Task Fault_ReconnectsAutomaticallyAndRecovers()
    {
        var provider = new RecordingProvider(Spec());
        await using var service = new CameraService(provider, FastRetry);
        await service.EnumerateAsync();
        Assert.True(await service.ConnectAsync("A"));
        var states = new List<CameraServiceState>();
        service.StateChanged += (s, e) =>
        {
            lock (states) { states.Add(service.State); }
        };

        provider.Created(0).SimulateFault("cable pulled");

        await WaitForAsync(() => provider.CreatedCount == 2 && service.State == CameraServiceState.Connected, "reconnect");
        Assert.Equal(string.Empty, service.ErrorText);
        Assert.Equal(0, service.ReconnectAttempt);
        Assert.Equal(CameraState.Closed, provider.Created(0).State);
        lock (states)
        {
            Assert.Contains(CameraServiceState.Faulted, states);
        }

        // The recovered connection grabs frames.
        CameraBgrImage image = await service.GrabOneAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(64, image.Width);
    }

    [Fact]
    public async Task Fault_WhenEveryReconnectFails_StaysFaultedAfterThreeTries()
    {
        var spec = Spec();
        var provider = new RecordingProvider(spec);
        await using var service = new CameraService(provider, FastRetry);
        await service.EnumerateAsync();
        Assert.True(await service.ConnectAsync("A"));
        spec.FailOnOpen = true;
        spec.OpenFailureMessage = "still down";

        provider.Created(0).SimulateFault("lost");

        await WaitForAsync(() => provider.CreatedCount == 1 + CameraService.MaxReconnectAttempts, "three reconnect tries");
        await WaitForAsync(() => service.ReconnectAttempt == 0, "reconnect to end");
        await Task.Delay(150);
        Assert.Equal(1 + CameraService.MaxReconnectAttempts, provider.CreatedCount);
        Assert.Equal(CameraServiceState.Faulted, service.State);
        Assert.Equal("still down", service.ErrorText);
        Assert.Equal("CameraStatusFaulted", service.StatusKey);
    }

    [Fact]
    public async Task Disconnect_WhileConnecting_CancelsTheOpen()
    {
        var provider = new GatedProvider();
        await using var service = new CameraService(provider, FastRetry);
        await service.EnumerateAsync();

        Task<bool> connecting = service.ConnectAsync("G");
        await WaitForAsync(() => provider.Last != null && provider.Last.Entered.IsSet, "the open to start");
        Assert.Equal(CameraServiceState.Connecting, service.State);
        Assert.Equal("CameraStatusConnecting", service.StatusKey);

        await service.DisconnectAsync();
        bool ok = await connecting.WaitAsync(Patience);

        Assert.False(ok);
        Assert.Equal(CameraServiceState.Disconnected, service.State);
        Assert.Equal(CameraState.Closed, provider.Last!.State);
    }

    [Fact]
    public async Task Connect_WhileConnected_SwitchesDevice()
    {
        var provider = new RecordingProvider(Spec("A", "Cam A"), Spec("B", "Cam B"));
        await using var service = new CameraService(provider, FastRetry);
        await service.EnumerateAsync();
        Assert.True(await service.ConnectAsync("A"));

        Assert.True(await service.ConnectAsync("B"));

        Assert.Equal(CameraServiceState.Connected, service.State);
        Assert.Equal("B", service.DeviceId);
        Assert.Equal("Cam B", service.DeviceName);
        Assert.Equal(2, provider.CreatedCount);
        Assert.Equal(CameraState.Closed, provider.Created(0).State);
        Assert.Equal(CameraState.Open, provider.Created(1).State);
    }

    [Fact]
    public async Task GrabOne_ReturnsAnIndependentBgrCopyEachTime()
    {
        await using var service = new CameraService(new RecordingProvider(Spec()), FastRetry);
        await service.EnumerateAsync();
        Assert.True(await service.ConnectAsync("A"));

        CameraBgrImage first = await service.GrabOneAsync(TimeSpan.FromSeconds(5));
        CameraBgrImage second = await service.GrabOneAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(64, first.Width);
        Assert.Equal(48, first.Height);
        Assert.Equal(64 * 48 * 3, first.Bgr.Length);
        Assert.NotSame(first.Bgr, second.Bgr);
        Assert.True(second.FrameNumber > first.FrameNumber);

        // Changing the copy never touches later frames.
        Array.Clear(first.Bgr, 0, first.Bgr.Length);
        Assert.Equal(64 * 48 * 3, second.Bgr.Length);
    }

    [Fact]
    public async Task GrabOne_WhenNotConnected_Throws()
    {
        await using var service = new CameraService(new RecordingProvider(Spec()), FastRetry);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GrabOneAsync(TimeSpan.FromSeconds(1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GrabOneAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task FrameReceived_DeliversLiveFrames_AndGrabOneStillWorksWhileLive()
    {
        await using var service = new CameraService(new RecordingProvider(Spec()), FastRetry);
        await service.EnumerateAsync();
        Assert.True(await service.ConnectAsync("A"));
        int count = 0;
        long lastNumber = 0;
        int threadId = -1;
        EventHandler<CameraFrameEventArgs> handler = (s, e) =>
        {
            Assert.Equal(64, e.Frame.Width);
            Volatile.Write(ref lastNumber, e.Frame.FrameNumber);
            Volatile.Write(ref threadId, Environment.CurrentManagedThreadId);
            Interlocked.Increment(ref count);
        };
        service.FrameReceived -= handler;
        service.FrameReceived += handler;

        await WaitForAsync(() => Volatile.Read(ref count) >= 3, "three live frames");
        CameraBgrImage image = await service.GrabOneAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(64 * 48 * 3, image.Bgr.Length);
        Assert.NotEqual(-1, Volatile.Read(ref threadId)); // raised from the pump, not necessarily a different pool thread than the test continuation

        service.FrameReceived -= handler;
        await Task.Delay(100);
        int after = Volatile.Read(ref count);
        await Task.Delay(150);
        Assert.Equal(after, Volatile.Read(ref count));
    }

    [Fact]
    public async Task Dispose_ClosesTheCamera()
    {
        var provider = new RecordingProvider(Spec());
        var service = new CameraService(provider, FastRetry);
        await service.EnumerateAsync();
        Assert.True(await service.ConnectAsync("A"));

        await service.DisposeAsync();

        Assert.Equal(CameraState.Closed, provider.Created(0).State);
        Assert.Equal(CameraServiceState.Disconnected, service.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.ConnectAsync("A"));
    }
}
