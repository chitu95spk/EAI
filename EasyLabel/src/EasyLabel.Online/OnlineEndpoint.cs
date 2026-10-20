using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace EasyLabel.Online
{
    /// <summary>
    /// LAN endpoint in front of an <see cref="OnlineRunner"/>. One request at a time per connection.
    /// Security: IP allow-list, 32-byte shared token (fixed-time compare), body cap, no file paths in the protocol.
    /// Overload: a rejected Submit answers <see cref="WireStatus.Busy"/> immediately instead of queueing the client.
    /// </summary>
    public sealed class OnlineEndpoint : IDisposable
    {
        private readonly OnlineRunner runner;
        private readonly byte[] token;
        private readonly HashSet<IPAddress> allow;
        private readonly int maxBodyBytes;
        private readonly int resultTimeoutMs;
        private readonly int maxConnections;
        private readonly ConcurrentDictionary<string, ManualResetEventSlim> waiters = new ConcurrentDictionary<string, ManualResetEventSlim>();
        private readonly ConcurrentDictionary<string, ResultRow> results = new ConcurrentDictionary<string, ResultRow>();
        private readonly object pendingGate = new object();
        private readonly object lifecycleGate = new object();
        private readonly HashSet<TcpClient> clients = new HashSet<TcpClient>();
        private readonly HashSet<Thread> clientThreads = new HashSet<Thread>();
        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool stopping;
        private const int DefaultMaxConnections = 8;
        private const int ShutdownJoinTimeoutMs = 2000;

        public OnlineEndpoint(OnlineRunner runner, byte[] token, IEnumerable<IPAddress> allowList, int maxBodyBytes, int resultTimeoutMs)
            : this(runner, token, allowList, maxBodyBytes, resultTimeoutMs, DefaultMaxConnections)
        {
        }

        public OnlineEndpoint(OnlineRunner runner, byte[] token, IEnumerable<IPAddress> allowList, int maxBodyBytes, int resultTimeoutMs, int maxConnections)
        {
            if (runner == null) throw new ArgumentNullException("runner");
            if (token == null || token.Length != WireCodec.TokenLength) throw new ArgumentException("token must be 32 bytes");
            if (allowList == null) throw new ArgumentNullException("allowList");
            if (maxBodyBytes < 64) throw new ArgumentOutOfRangeException("maxBodyBytes");
            if (maxConnections < 1) throw new ArgumentOutOfRangeException("maxConnections");
            this.runner = runner; this.token = token; this.maxBodyBytes = maxBodyBytes; this.resultTimeoutMs = resultTimeoutMs; this.maxConnections = maxConnections;
            allow = new HashSet<IPAddress>();
            foreach (IPAddress address in allowList)
            {
                if (address == null) throw new ArgumentException("allow-list contains a null address", "allowList");
                allow.Add(NormalizeAddress(address));
            }
            if (allow.Count == 0) throw new ArgumentException("allow-list must not be empty");
            runner.ResultReady -= OnResult;
            runner.ResultReady += OnResult;
        }

        public int Port { get; private set; }
        public long Rejected { get { return Interlocked.Read(ref rejected); } }
        private long rejected;

        public void Start(IPAddress bindAddress, int port)
        {
            lock (lifecycleGate)
            {
                if (stopping) throw new ObjectDisposedException("OnlineEndpoint");
                if (listener != null) return;
                TcpListener next = new TcpListener(bindAddress, port);
                next.Start();
                listener = next;
                Port = ((IPEndPoint)next.LocalEndpoint).Port;
                acceptThread = new Thread(() => AcceptLoop(next)) { IsBackground = true, Name = "OnlineEndpoint" };
                try { acceptThread.Start(); }
                catch { listener = null; next.Stop(); throw; }
            }
        }

        public void Dispose()
        {
            Thread localAcceptThread;
            List<TcpClient> clientsToClose;
            List<Thread> threadsToJoin;
            TcpListener localListener;
            lock (lifecycleGate)
            {
                stopping = true;
                localAcceptThread = acceptThread;
                localListener = listener;
                listener = null;
                clientsToClose = new List<TcpClient>(clients);
                threadsToJoin = new List<Thread>(clientThreads);
            }

            if (localListener != null) localListener.Stop();
            for (int i = 0; i < clientsToClose.Count; i++) CloseClient(clientsToClose[i]);
            ReleasePendingWaiters();

            Stopwatch shutdownTimer = Stopwatch.StartNew();
            JoinUntil(localAcceptThread, shutdownTimer);
            for (int i = 0; i < threadsToJoin.Count; i++) JoinUntil(threadsToJoin[i], shutdownTimer);

            runner.ResultReady -= OnResult;
        }

        private void OnResult(ResultRow row)
        {
            lock (pendingGate)
            {
                ManualResetEventSlim waiter;
                if (waiters.TryGetValue(row.FrameId, out waiter))
                {
                    results[row.FrameId] = row;
                    waiter.Set();
                }
            }
        }

        private void AcceptLoop(TcpListener activeListener)
        {
            while (!stopping)
            {
                TcpClient client;
                try { client = activeListener.AcceptTcpClient(); }
                catch (Exception) { return; }
                IPEndPoint remoteEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
                IPAddress remote = remoteEndPoint == null ? null : NormalizeAddress(remoteEndPoint.Address);
                if (remote == null || !allow.Contains(remote)) { Reject(client); continue; }

                bool admitted = false;
                lock (lifecycleGate)
                {
                    if (!stopping && clients.Count < maxConnections)
                    {
                        clients.Add(client);
                        Thread clientThread = new Thread(() => Serve(client)) { IsBackground = true, Name = "OnlineEndpointClient" };
                        clientThreads.Add(clientThread);
                        try
                        {
                            clientThread.Start();
                            admitted = true;
                        }
                        catch
                        {
                            clientThreads.Remove(clientThread);
                            clients.Remove(client);
                        }
                    }
                }
                if (!admitted) Reject(client);
            }
        }

        private void Serve(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 30000;
                Stream s = client.GetStream();
                while (!stopping)
                {
                    OnlineFrame frame;
                    bool authenticated;
                    try { frame = WireCodec.ReadRequest(s, maxBodyBytes, token, out authenticated); }
                    catch (WireProtocolException) { TryReply(s, WireStatus.BadRequest, "bad request"); return; }
                    catch (Exception) { return; }
                    if (!authenticated)
                    {
                        Interlocked.Increment(ref rejected);
                        TryReply(s, WireStatus.Unauthorized, "unauthorized");
                        return;
                    }
                    if (!Handle(s, frame)) return;
                }
            }
            finally
            {
                CloseClient(client);
                lock (lifecycleGate)
                {
                    clients.Remove(client);
                    clientThreads.Remove(Thread.CurrentThread);
                }
            }
        }

        private bool Handle(Stream s, OnlineFrame frame)
        {
            var waiter = new ManualResetEventSlim(false);
            bool registered;
            lock (pendingGate)
            {
                registered = !stopping && waiters.TryAdd(frame.FrameId, waiter);
            }
            if (!registered)
            {
                waiter.Dispose();
                return stopping ? false : TryReply(s, WireStatus.BadRequest, "duplicate frameId in flight");
            }
            try
            {
                if (stopping) return false;
                if (!runner.Submit(frame)) return TryReply(s, WireStatus.Busy, "busy");
                if (!waiter.Wait(resultTimeoutMs)) return TryReply(s, WireStatus.Failed, "timeout");
                ResultRow row;
                bool hasResult;
                lock (pendingGate)
                {
                    hasResult = results.TryRemove(frame.FrameId, out row);
                }
                if (!hasResult) return TryReply(s, WireStatus.Failed, "no result");
                try { WireCodec.WriteResponse(s, WireStatus.Ok, row.Serialize()); return true; }
                catch (Exception) { return false; }
            }
            finally
            {
                lock (pendingGate)
                {
                    ManualResetEventSlim removed;
                    waiters.TryRemove(frame.FrameId, out removed);
                    ResultRow ignored;
                    results.TryRemove(frame.FrameId, out ignored);
                    waiter.Dispose();
                }
            }
        }

        private static bool TryReply(Stream s, WireStatus status, string reason)
        {
            try { WireCodec.WriteResponse(s, status, Encoding.UTF8.GetBytes(reason)); return status == WireStatus.Busy; }
            catch (Exception) { return false; }
        }

        private void ReleasePendingWaiters()
        {
            lock (pendingGate)
            {
                foreach (ManualResetEventSlim waiter in waiters.Values) waiter.Set();
            }
        }

        private static void JoinUntil(Thread thread, Stopwatch shutdownTimer)
        {
            if (thread == null || thread == Thread.CurrentThread) return;
            int remaining = ShutdownJoinTimeoutMs - (int)shutdownTimer.ElapsedMilliseconds;
            if (remaining > 0) thread.Join(remaining);
        }

        private void Reject(TcpClient client)
        {
            Interlocked.Increment(ref rejected);
            CloseClient(client);
        }

        private static void CloseClient(TcpClient client)
        {
            try { client.Close(); }
            catch (Exception) { }
        }

        private static IPAddress NormalizeAddress(IPAddress address)
        {
            return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        }
    }
}
