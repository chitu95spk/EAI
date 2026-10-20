using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace EasyLabel.Online
{
    public sealed class OnlineFrame
    {
        public OnlineFrame(string frameId, int width, int height, byte[] pixels)
        {
            FrameId = frameId; Width = width; Height = height; Pixels = pixels;
        }
        public string FrameId { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public byte[] Pixels { get; private set; }
    }

    /// <summary>Runs preprocess, inference and postprocess for one frame. Adds its own stage timings to <paramref name="stageMs"/>.</summary>
    public interface IInferenceHost
    {
        IReadOnlyList<Detection> Infer(OnlineFrame frame, IDictionary<string, double> stageMs);
    }

    public enum OverloadPolicy
    {
        /// <summary>Queue full: reject the new frame (keeps the oldest queued work).</summary>
        DropNewest,
        /// <summary>Queue full: discard the oldest queued frame (keeps the freshest image).</summary>
        DropOldest
    }

    /// <summary>
    /// Bounded-queue local runner. No network, no LLM: frames in, <see cref="ResultRow"/> out via <see cref="ResultReady"/>.
    /// </summary>
    public sealed class OnlineRunner : IDisposable
    {
        private sealed class Item { public OnlineFrame Frame; public long EnqueuedTicks; }

        private readonly IInferenceHost host;
        private volatile RecipeEvaluator evaluator;
        private readonly string recipeId;
        private readonly int capacity;
        private readonly OverloadPolicy policy;
        private readonly LinkedList<Item> queue = new LinkedList<Item>();
        private readonly object gate = new object();
        private Thread worker;
        private bool running;

        public OnlineRunner(IInferenceHost host, RecipeEvaluator evaluator, string recipeId, int queueCapacity, OverloadPolicy policy)
        {
            if (host == null) throw new ArgumentNullException("host");
            if (evaluator == null) throw new ArgumentNullException("evaluator");
            if (queueCapacity < 1) throw new ArgumentOutOfRangeException("queueCapacity");
            this.host = host; this.evaluator = evaluator; this.recipeId = recipeId;
            capacity = queueCapacity; this.policy = policy;
            Metrics = new OnlineMetrics();
        }

        public OnlineMetrics Metrics { get; private set; }
        public bool IsRunning { get { lock (gate) return running; } }

        /// <summary>Raised on the worker thread. Handlers must be quick; they sit inside the frame path.</summary>
        public event Action<ResultRow> ResultReady;

        public void Start()
        {
            lock (gate)
            {
                if (running) return;
                running = true;
                worker = new Thread(Loop) { IsBackground = true, Name = "OnlineRunner" };
                worker.Start();
            }
        }

        /// <summary>Returns false when the frame was rejected (stopped, or DropNewest with a full queue).</summary>
        public bool Submit(OnlineFrame frame)
        {
            if (frame == null) throw new ArgumentNullException("frame");
            var item = new Item { Frame = frame, EnqueuedTicks = Stopwatch.GetTimestamp() };
            lock (gate)
            {
                if (!running) return false;
                if (queue.Count >= capacity)
                {
                    Metrics.CountDropped();
                    if (policy == OverloadPolicy.DropNewest) return false;
                    queue.RemoveFirst();
                }
                queue.AddLast(item);
                Metrics.CountAccepted();
                Monitor.Pulse(gate);
                return true;
            }
        }

        /// <summary>Stops accepting frames, lets the worker finish the queue, then joins.</summary>
        public void Stop()
        {
            Thread t;
            lock (gate)
            {
                if (!running) return;
                running = false;
                t = worker;
                Monitor.PulseAll(gate);
            }
            if (t != null) t.Join();
        }

        /// <summary>Atomically replaces the evaluator; frames already in flight finish with the old one.</summary>
        public void SwapEvaluator(RecipeEvaluator next)
        {
            if (next == null) throw new ArgumentNullException("next");
            evaluator = next;
        }

        public void Dispose() { Stop(); }

        private void Loop()
        {
            while (true)
            {
                Item item;
                lock (gate)
                {
                    while (queue.Count == 0)
                    {
                        if (!running) return;
                        Monitor.Wait(gate);
                    }
                    item = queue.First.Value;
                    queue.RemoveFirst();
                }
                Process(item);
            }
        }

        private void Process(Item item)
        {
            var stageMs = new Dictionary<string, double>();
            long start = Stopwatch.GetTimestamp();
            stageMs[OnlineMetrics.QueueStage] = ToMs(start - item.EnqueuedTicks);
            try
            {
                var detections = host.Infer(item.Frame, stageMs);
                long t = Stopwatch.GetTimestamp();
                var row = evaluator.Evaluate(item.Frame.FrameId, recipeId, detections, stageMs);
                stageMs["evaluate"] = ToMs(Stopwatch.GetTimestamp() - t);
                stageMs[OnlineMetrics.TotalStage] = ToMs(Stopwatch.GetTimestamp() - item.EnqueuedTicks);
                Metrics.Record(stageMs);
                var handler = ResultReady;
                if (handler != null) handler(row);
            }
            catch (Exception)
            {
                Metrics.CountFailed();
            }
        }

        private static double ToMs(long ticks) { return ticks * 1000.0 / Stopwatch.Frequency; }
    }
}
