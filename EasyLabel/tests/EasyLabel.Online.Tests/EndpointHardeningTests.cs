using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using EasyLabel.Online;
using Xunit;

namespace EasyLabel.Online.Tests
{
    public sealed class EndpointHardeningTests
    {
        private sealed class ControlledHost : IInferenceHost
        {
            public readonly ManualResetEventSlim Entered = new ManualResetEventSlim(false);
            public ManualResetEventSlim Gate;

            public IReadOnlyList<Detection> Infer(OnlineFrame frame, IDictionary<string, double> stageMs)
            {
                Entered.Set();
                if (Gate != null) Gate.Wait();
                stageMs["infer"] = 0;
                return new List<Detection>();
            }
        }

        private static readonly byte[] Token = Fill(11);

        private static byte[] Fill(byte value)
        {
            var bytes = new byte[WireCodec.TokenLength];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = value;
            return bytes;
        }

        private static OnlineFrame Frame(string id)
        {
            return new OnlineFrame(id, 4, 4, new byte[16]);
        }

        private static OnlineEndpoint Start(ControlledHost host, int capacity, int timeoutMs, out OnlineRunner runner, int maxConnections = 8)
        {
            runner = new OnlineRunner(host, new RecipeEvaluator(Fixtures.Recipe()), "hardening", capacity, OverloadPolicy.DropNewest);
            runner.Start();
            var endpoint = new OnlineEndpoint(runner, Token, new[] { IPAddress.Loopback }, 1024, timeoutMs, maxConnections);
            endpoint.Start(IPAddress.Loopback, 0);
            return endpoint;
        }

        private static TcpClient Connect(OnlineEndpoint endpoint)
        {
            var client = new TcpClient();
            client.Connect(IPAddress.Loopback, endpoint.Port);
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            return client;
        }

        private static TcpClient Connect(OnlineEndpoint endpoint, IPAddress address)
        {
            var client = new TcpClient(AddressFamily.InterNetwork);
            client.Connect(address, endpoint.Port);
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            return client;
        }

        private static WireResponse Send(OnlineEndpoint endpoint, string frameId)
        {
            using (var client = Connect(endpoint))
            {
                Stream stream = client.GetStream();
                WireCodec.WriteRequest(stream, Token, Frame(frameId));
                return WireCodec.ReadResponse(stream, 1024);
            }
        }

        [Fact]
        public void BoundedParallelClientsReceiveResponsesAndDuplicateIsRejected()
        {
            var host = new ControlledHost { Gate = new ManualResetEventSlim(false) };
            OnlineRunner runner;
            OnlineEndpoint endpoint = Start(host, 16, 5000, out runner, 10);
            try
            {
                using (var first = Connect(endpoint))
                using (var duplicate = Connect(endpoint))
                {
                    Stream firstStream = first.GetStream();
                    WireCodec.WriteRequest(firstStream, Token, Frame("duplicate"));
                    Assert.True(host.Entered.Wait(5000));

                    Stream duplicateStream = duplicate.GetStream();
                    WireCodec.WriteRequest(duplicateStream, Token, Frame("duplicate"));
                    Assert.Equal(WireStatus.BadRequest, WireCodec.ReadResponse(duplicateStream, 1024).Status);

                    const int clients = 8;
                    var start = new ManualResetEventSlim(false);
                    var work = new Task<WireResponse>[clients];
                    for (int i = 0; i < clients; i++)
                    {
                        int index = i;
                        work[index] = Task.Run(() =>
                        {
                            start.Wait();
                            return Send(endpoint, "parallel-" + index);
                        });
                    }

                    start.Set();
                    host.Gate.Set();
                    Assert.Equal(WireStatus.Ok, WireCodec.ReadResponse(firstStream, 1024).Status);
                    Assert.True(Task.WaitAll(work, 10000));
                    for (int i = 0; i < work.Length; i++) Assert.Equal(WireStatus.Ok, work[i].Result.Status);
                    start.Dispose();
                }
            }
            finally
            {
                host.Gate.Set();
                endpoint.Dispose();
                runner.Dispose();
                host.Gate.Dispose();
                host.Entered.Dispose();
            }
        }

        [Fact]
        public void TruncatedAndOversizeRequestsAreRejectedWithoutBreakingSubsequentClient()
        {
            OnlineRunner runner;
            using (var endpoint = Start(new ControlledHost(), 4, 1000, out runner))
            using (runner)
            {
                using (var truncated = Connect(endpoint))
                {
                    Stream stream = truncated.GetStream();
                    stream.Write(new byte[] { (byte)'E', (byte)'O', (byte)'L', (byte)'1', 43, 0, 0, 0, 1, 2, 3 }, 0, 11);
                    truncated.Client.Shutdown(SocketShutdown.Send);
                    Assert.Equal(-1, stream.ReadByte());
                }

                using (var slow = Connect(endpoint))
                {
                    Stream stream = slow.GetStream();
                    stream.Write(new byte[] { (byte)'E', (byte)'O', (byte)'L', (byte)'1', 43, 0, 0, 0, 1, 2, 3 }, 0, 11);
                    Assert.Equal(WireStatus.Ok, Send(endpoint, "after-slow-client").Status);
                    slow.Client.Shutdown(SocketShutdown.Send);
                    Assert.Equal(-1, stream.ReadByte());
                }

                using (var oversize = Connect(endpoint))
                {
                    Stream stream = oversize.GetStream();
                    stream.Write(new byte[] { (byte)'E', (byte)'O', (byte)'L', (byte)'1', 1, 4, 0, 0 }, 0, 8);
                    Assert.Equal(WireStatus.BadRequest, WireCodec.ReadResponse(stream, 1024).Status);
                }
                Assert.Equal(WireStatus.Ok, Send(endpoint, "after-invalid").Status);
            }
        }

        [Fact]
        public void ClientDisconnectWhileWaitingDoesNotBlockFollowingFrame()
        {
            var host = new ControlledHost { Gate = new ManualResetEventSlim(false) };
            OnlineRunner runner;
            OnlineEndpoint endpoint = Start(host, 4, 1000, out runner);
            TcpClient abandoned = null;
            try
            {
                abandoned = Connect(endpoint);
                WireCodec.WriteRequest(abandoned.GetStream(), Token, Frame("abandoned"));
                Assert.True(host.Entered.Wait(5000));
                abandoned.Close();
                abandoned = null;
                host.Gate.Set();

                Assert.Equal(WireStatus.Ok, Send(endpoint, "after-disconnect").Status);
            }
            finally
            {
                host.Gate.Set();
                if (abandoned != null) abandoned.Close();
                endpoint.Dispose();
                runner.Dispose();
                host.Gate.Dispose();
                host.Entered.Dispose();
            }
        }

        [Fact]
        public void DisposeClosesAcceptedClientConnection()
        {
            var host = new ControlledHost { Gate = new ManualResetEventSlim(false) };
            OnlineRunner runner;
            OnlineEndpoint endpoint = Start(host, 4, 100, out runner);
            TcpClient client = null;
            try
            {
                client = Connect(endpoint);
                WireCodec.WriteRequest(client.GetStream(), Token, Frame("dispose-serving"));
                Assert.True(host.Entered.Wait(5000));
                endpoint.Dispose();
                client.ReceiveTimeout = 250;
                Assert.Equal(-1, client.GetStream().ReadByte());
            }
            finally
            {
                host.Gate.Set();
                if (client != null) client.Close();
                endpoint.Dispose();
                runner.Dispose();
                host.Gate.Dispose();
                host.Entered.Dispose();
            }
        }

        [Fact]
        public void ConnectionCapRejectsBeforeStartingAnotherClientAndReleasesItsSlot()
        {
            OnlineRunner runner;
            var host = new ControlledHost();
            runner = new OnlineRunner(host, new RecipeEvaluator(Fixtures.Recipe()), "connection-cap", 4, OverloadPolicy.DropNewest);
            runner.Start();
            var endpoint = new OnlineEndpoint(runner, Token, new[] { IPAddress.Loopback }, 1024, 1000, 1);
            endpoint.Start(IPAddress.Loopback, 0);
            try
            {
                using (var first = Connect(endpoint))
                using (var rejected = Connect(endpoint))
                {
                    Stream firstStream = first.GetStream();
                    Stream rejectedStream = rejected.GetStream();
                    rejected.ReceiveTimeout = 5000;
                    Assert.Equal(-1, rejectedStream.ReadByte());
                    Assert.Equal(1, endpoint.Rejected);

                    first.Client.Shutdown(SocketShutdown.Send);
                    Assert.Equal(-1, firstStream.ReadByte());
                }

                WireResponse response = null;
                DateTime deadline = DateTime.UtcNow.AddSeconds(5);
                while (response == null && DateTime.UtcNow < deadline)
                {
                    try { response = Send(endpoint, "after-connection-cap"); }
                    catch (Exception) { Thread.Sleep(20); }
                }
                Assert.NotNull(response);
                Assert.Equal(WireStatus.Ok, response.Status);
            }
            finally
            {
                endpoint.Dispose();
                runner.Dispose();
                host.Entered.Dispose();
            }
        }

        [Fact]
        public void InvalidFirstAndLastTokenBytesAreUnauthorized()
        {
            OnlineRunner runner;
            using (var endpoint = Start(new ControlledHost(), 4, 1000, out runner))
            using (runner)
            {
                byte[] firstWrong = (byte[])Token.Clone();
                byte[] lastWrong = (byte[])Token.Clone();
                firstWrong[0]++;
                lastWrong[lastWrong.Length - 1]++;

                using (var first = Connect(endpoint))
                {
                    WireCodec.WriteRequest(first.GetStream(), firstWrong, Frame("bad-first"));
                    Assert.Equal(WireStatus.Unauthorized, WireCodec.ReadResponse(first.GetStream(), 1024).Status);
                }
                using (var last = Connect(endpoint))
                {
                    WireCodec.WriteRequest(last.GetStream(), lastWrong, Frame("bad-last"));
                    Assert.Equal(WireStatus.Unauthorized, WireCodec.ReadResponse(last.GetStream(), 1024).Status);
                }
                Assert.Equal(2, endpoint.Rejected);
            }
        }

        [Fact]
        public void UnauthenticatedRequestReadsOnlyFixedPrefixBeforeRejectingBody()
        {
            const int bodyLength = 512;
            byte[] bytes = new byte[8 + bodyLength];
            bytes[0] = (byte)'E'; bytes[1] = (byte)'O'; bytes[2] = (byte)'L'; bytes[3] = (byte)'1';
            bytes[4] = (byte)(bodyLength & 255);
            bytes[5] = (byte)(bodyLength >> 8);
            bytes[6] = 0;
            bytes[7] = 0;
            for (int i = 0; i < WireCodec.TokenLength; i++) bytes[8 + i] = 99;
            using (var stream = new RecordingReadStream(bytes))
            {
                bool authenticated;
                OnlineFrame frame = WireCodec.ReadRequest(stream, 1024, Token, out authenticated);
                Assert.False(authenticated);
                Assert.Null(frame);
                Assert.Equal(8 + WireCodec.TokenLength, stream.BytesRead);
            }
        }

        [Fact]
        public void DeclaredBodyAboveCapReadsOnlyHeaderBeforeRejecting()
        {
            byte[] bytes = new byte[72];
            bytes[0] = (byte)'E'; bytes[1] = (byte)'O'; bytes[2] = (byte)'L'; bytes[3] = (byte)'1';
            bytes[4] = 1;
            bytes[5] = 4;
            using (var stream = new RecordingReadStream(bytes))
            {
                Assert.Throws<WireProtocolException>(() =>
                {
                    bool authenticated;
                    WireCodec.ReadRequest(stream, 1024, Token, out authenticated);
                });
                Assert.Equal(8, stream.BytesRead);
            }
        }

        [Fact]
        public void AuthenticatedCodecOverloadAndMappedAllowListPreserveCompatibility()
        {
            var memory = new MemoryStream();
            WireCodec.WriteRequest(memory, Token, Frame("compat"));
            byte[] wire = memory.ToArray();
            Assert.Equal((byte)'E', wire[0]);
            Assert.Equal((byte)'O', wire[1]);
            Assert.Equal((byte)'L', wire[2]);
            Assert.Equal((byte)'1', wire[3]);
            memory.Position = 0;
            byte[] legacyToken;
            OnlineFrame legacyFrame = WireCodec.ReadRequest(memory, 1024, out legacyToken);
            Assert.Equal(Token, legacyToken);
            Assert.Equal("compat", legacyFrame.FrameId);
            memory.Position = 0;
            bool authenticated;
            OnlineFrame frame = WireCodec.ReadRequest(memory, 1024, Token, out authenticated);
            Assert.True(authenticated);
            Assert.Equal("compat", frame.FrameId);

            OnlineRunner runner;
            var host = new ControlledHost();
            runner = new OnlineRunner(host, new RecipeEvaluator(Fixtures.Recipe()), "mapped-config", 4, OverloadPolicy.DropNewest);
            runner.Start();
            using (var endpoint = new OnlineEndpoint(runner, Token, new[] { IPAddress.Parse("::ffff:127.0.0.1") }, 1024, 1000))
            {
                endpoint.Start(IPAddress.Loopback, 0);
                Assert.Equal(WireStatus.Ok, Send(endpoint, "mapped-config").Status);
            }
            runner.Dispose();
            host.Entered.Dispose();
        }

        [Fact]
        public void MappedRemoteAddressIsAcceptedWhenDualModeEndpointIsAvailable()
        {
            if (!Socket.OSSupportsIPv6) return;

            OnlineRunner runner;
            var host = new ControlledHost();
            runner = new OnlineRunner(host, new RecipeEvaluator(Fixtures.Recipe()), "mapped-remote", 4, OverloadPolicy.DropNewest);
            runner.Start();
            try
            {
                using (var endpoint = new OnlineEndpoint(runner, Token, new[] { IPAddress.Loopback }, 1024, 1000))
                {
                    TcpClient client;
                    try
                    {
                        endpoint.Start(IPAddress.IPv6Any, 0);
                        client = Connect(endpoint, IPAddress.Loopback);
                    }
                    catch (SocketException)
                    {
                        return;
                    }
                    using (client)
                    {
                        WireCodec.WriteRequest(client.GetStream(), Token, Frame("mapped-remote"));
                        Assert.Equal(WireStatus.Ok, WireCodec.ReadResponse(client.GetStream(), 1024).Status);
                    }
                }
            }
            finally
            {
                runner.Dispose();
                host.Entered.Dispose();
            }
        }

        private sealed class RecordingReadStream : Stream
        {
            private readonly MemoryStream inner;

            public RecordingReadStream(byte[] bytes) { inner = new MemoryStream(bytes); }
            public int BytesRead { get; private set; }
            public override bool CanRead { get { return inner.CanRead; } }
            public override bool CanSeek { get { return inner.CanSeek; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return inner.Length; } }
            public override long Position { get { return inner.Position; } set { inner.Position = value; } }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                int read = inner.Read(buffer, offset, count);
                BytesRead += read;
                return read;
            }
            public override long Seek(long offset, SeekOrigin origin) { return inner.Seek(offset, origin); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}
