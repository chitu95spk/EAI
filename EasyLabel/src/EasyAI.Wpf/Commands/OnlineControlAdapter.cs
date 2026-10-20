using EasyLabel.Commands;
using EasyLabel.Online;

namespace EasyAI.Desktop.Commands
{
    /// <summary>
    /// Host-side bridge between the ONL.* commands and the active <see cref="OnlineSession"/>.
    /// With no session attached it reports "not running" and the commands fail with NOT_RUNNING.
    /// </summary>
    public sealed class OnlineControlAdapter : IOnlineControl
    {
        private readonly object _gate = new object();
        private OnlineSession _session;

        public void Attach(OnlineSession session)
        {
            lock (_gate) _session = session;
        }

        public void Detach()
        {
            lock (_gate) _session = null;
        }

        private OnlineSession Current
        {
            get { lock (_gate) return _session; }
        }

        public bool IsRunning
        {
            get { OnlineSession s = Current; return s != null && s.IsRunning; }
        }

        public void Stop()
        {
            OnlineSession s = Current;
            if (s != null) s.Stop();
        }

        public string StatusJson()
        {
            OnlineSession s = Current;
            return s == null ? "{\"running\":false}" : s.StatusJson();
        }

        public string SetThreshold(string className, double score, double nms)
        {
            OnlineSession s = Current;
            return s == null ? "NOT_RUNNING" : s.SetThreshold(className, score, nms);
        }
    }
}
