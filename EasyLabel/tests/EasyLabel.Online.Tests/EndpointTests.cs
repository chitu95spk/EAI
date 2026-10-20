using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using EasyLabel.Online;
using Xunit;

namespace EasyLabel.Online.Tests
{
    public sealed class EndpointTests
    {
        private sealed class Host : IInferenceHost
        {
            public ManualResetEventSlim Gate;
            public IReadOnlyList<Detection> Infer(OnlineFrame frame, IDictionary<string, double> stageMs)
            {
                if (Gate != null) Gate.Wait();
                return frame.FrameId.StartsWith("ng")
                    ? new List<Detection> { new Detection("scratch", 0.9, new BoundingBox(10, 10, 20, 20), 400, new Centroid(20, 20)) }
                    : new List<Detection>();
            }
        }

        private static readonly byte[] Token = Fill(7);
        private static byte[] Fill(byte v) { var b = new byte[WireCodec.TokenLength]; for (int i = 0; i < b.Length; i++) b[i] = v; return b; }
        private static OnlineFrame Frame(string id) { return new OnlineFrame(id, 4, 4, new byte[16]); }

        private static OnlineEndpoint Start(Host host, int capacity, IEnumerable<IPAddress> allow, out OnlineRunner runner)
        {
            runner = new OnlineRunner(host, new RecipeEvaluator(Fixtures.Recipe()), "r1", capacity, OverloadPolicy.DropNewest);
            runner.Start();
            var ep = new OnlineEndpoint(runner, Token, allow, 1 << 20, 5000);
            ep.Start(IPAddress.Loopback, 0);
            return ep;
        }

        private static TcpClient Connect(OnlineEndpoint ep)
        {
            var c = new TcpClient();
            c.Connect(IPAddress.Loopback, ep.Port);
            c.ReceiveTimeout = 5000;
            return c;
        }

        [Fact]
        public void CodecRoundTrip()
        {
            var ms = new MemoryStream();
            WireCodec.WriteRequest(ms, Token, new OnlineFrame("abc", 2, 3, new byte[] { 1, 2, 3, 4, 5, 6 }));
            ms.Position = 0;
            byte[] token;
            var f = WireCodec.ReadRequest(ms, 1024, out token);
            Assert.Equal("abc", f.FrameId);
            Assert.Equal(2, f.Width);
            Assert.Equal(3, f.Height);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, f.Pixels);
            Assert.Equal(Token, token);
        }

        [Fact]
        public void CodecRejectsOversizeAndMismatch()
        {
            var ms = new MemoryStream();
            WireCodec.WriteRequest(ms, Token, Frame("a"));
            ms.Position = 0;
            byte[] t;
            Assert.Throws<WireProtocolException>(() => WireCodec.ReadRequest(ms, 10, out t));
            var bad = new MemoryStream(new byte[] { 1, 2, 3, 4, 0, 0, 0, 0 });
            Assert.Throws<WireProtocolException>(() => WireCodec.ReadRequest(bad, 1024, out t));
        }

        [Fact]
        public void ServesOkAndNgOverLoopback()
        {
            OnlineRunner runner;
            using (var ep = Start(new Host(), 4, new[] { IPAddress.Loopback }, out runner))
            using (runner)
            using (var c = Connect(ep))
            {
                var s = c.GetStream();
                WireCodec.WriteRequest(s, Token, Frame("ok-1"));
                var r1 = WireCodec.ReadResponse(s, 1 << 20);
                WireCodec.WriteRequest(s, Token, Frame("ng-1"));
                var r2 = WireCodec.ReadResponse(s, 1 << 20);
                Assert.Equal(WireStatus.Ok, r1.Status);
                Assert.Contains("\"OK\"", System.Text.Encoding.UTF8.GetString(r1.Body));
                Assert.Equal(WireStatus.Ok, r2.Status);
                Assert.Contains("\"NG\"", System.Text.Encoding.UTF8.GetString(r2.Body));
            }
        }

        [Fact]
        public void WrongTokenIsUnauthorized()
        {
            OnlineRunner runner;
            using (var ep = Start(new Host(), 4, new[] { IPAddress.Loopback }, out runner))
            using (runner)
            using (var c = Connect(ep))
            {
                var s = c.GetStream();
                WireCodec.WriteRequest(s, Fill(9), Frame("x"));
                Assert.Equal(WireStatus.Unauthorized, WireCodec.ReadResponse(s, 1024).Status);
                Assert.Equal(1, ep.Rejected);
            }
        }

        [Fact]
        public void ClientOutsideAllowListIsDropped()
        {
            OnlineRunner runner;
            using (var ep = Start(new Host(), 4, new[] { IPAddress.Parse("10.255.255.1") }, out runner))
            using (runner)
            using (var c = Connect(ep))
            {
                var s = c.GetStream();
                try { WireCodec.WriteRequest(s, Token, Frame("x")); } catch (IOException) { }
                Assert.ThrowsAny<Exception>(() => WireCodec.ReadResponse(s, 1024));
                Assert.Equal(1, ep.Rejected);
            }
        }

        [Fact]
        public void FullQueueAnswersBusy()
        {
            var host = new Host { Gate = new ManualResetEventSlim(false) };
            OnlineRunner runner;
            using (var ep = Start(host, 1, new[] { IPAddress.Loopback }, out runner))
            using (runner)
            {
                var clients = new List<TcpClient>();
                try
                {
                    // 1st goes to the worker (blocked), 2nd fills the queue, 3rd must be Busy.
                    for (int i = 0; i < 2; i++)
                    {
                        var c = Connect(ep); clients.Add(c);
                        WireCodec.WriteRequest(c.GetStream(), Token, Frame("f" + i));
                        Thread.Sleep(150);
                    }
                    var c3 = Connect(ep); clients.Add(c3);
                    WireCodec.WriteRequest(c3.GetStream(), Token, Frame("f2"));
                    Assert.Equal(WireStatus.Busy, WireCodec.ReadResponse(c3.GetStream(), 1024).Status);
                }
                finally
                {
                    host.Gate.Set();
                    foreach (var c in clients) c.Close();
                }
            }
        }
    }
}
