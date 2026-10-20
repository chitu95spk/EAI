using System;
using System.Globalization;
using System.Text.Json;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Commands;
using EasyAI.Desktop.Resources;

namespace EasyAI.Desktop.ViewModels
{
    public enum BudgetState
    {
        Unknown,
        Ok,
        Over
    }

    public sealed class OnlineStatusViewModel : ObservableObject, IDisposable
    {
        private readonly OnlineControlAdapter _control;
        private readonly DispatcherTimer _timer;
        private bool _disposed;
        private bool _statusUnavailable;
        private bool _isRunning;
        private string _recipeId = string.Empty;
        private long _accepted;
        private long _dropped;
        private long _completed;
        private long _failed;
        private double? _totalP95Ms;
        private string _scoreText = string.Empty;
        private string _nmsText = string.Empty;
        private string _thresholdMessage = string.Empty;
        private bool _stopping;

        /// <summary>Create and dispose on the window's dispatcher thread.</summary>
        public OnlineStatusViewModel(OnlineControlAdapter control, double budgetMs = 100)
        {
            if (control == null) throw new ArgumentNullException(nameof(control));
            if (double.IsNaN(budgetMs) || double.IsInfinity(budgetMs) || budgetMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(budgetMs));

            _control = control;
            BudgetMs = budgetMs;
            StopCommand = new RelayCommand(Stop, () => IsRunning && !_stopping);
            ApplyThresholdCommand = new RelayCommand(ApplyThreshold, () => IsRunning && !_stopping);
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _timer.Tick -= OnTick;
            _timer.Tick += OnTick;
            PollStatus();
            _timer.Start();
        }

        public bool IsRunning
        {
            get { return _isRunning; }
            private set
            {
                if (!SetProperty(ref _isRunning, value)) return;
                StopCommand.NotifyCanExecuteChanged();
                ApplyThresholdCommand.NotifyCanExecuteChanged();
            }
        }

        public string RecipeId
        {
            get { return _recipeId; }
            private set { SetProperty(ref _recipeId, value); }
        }

        public long Accepted
        {
            get { return _accepted; }
            private set { SetProperty(ref _accepted, value); }
        }

        public long Dropped
        {
            get { return _dropped; }
            private set { SetProperty(ref _dropped, value); }
        }

        public long Completed
        {
            get { return _completed; }
            private set { SetProperty(ref _completed, value); }
        }

        public long Failed
        {
            get { return _failed; }
            private set { SetProperty(ref _failed, value); }
        }

        public double? TotalP95Ms
        {
            get { return _totalP95Ms; }
            private set { SetProperty(ref _totalP95Ms, value); }
        }

        public double BudgetMs { get; }

        public BudgetState BudgetState
        {
            get
            {
                if (!TotalP95Ms.HasValue) return BudgetState.Unknown;
                return TotalP95Ms.Value <= BudgetMs ? BudgetState.Ok : BudgetState.Over;
            }
        }

        public string StatusText
        {
            get
            {
                if (_statusUnavailable) return OnlineStrings.StatusUnavailable;
                return IsRunning ? OnlineStrings.StatusRunning : OnlineStrings.StatusStopped;
            }
        }

        public string P95BudgetText
        {
            get
            {
                CultureInfo culture = Strings.Culture ?? CultureInfo.CurrentUICulture;
                string p95 = TotalP95Ms.HasValue
                    ? TotalP95Ms.Value.ToString("0.###", culture)
                    : OnlineStrings.Unavailable;
                return string.Format(culture, OnlineStrings.P95BudgetFormat, p95, BudgetMs);
            }
        }

        public string ScoreText
        {
            get { return _scoreText; }
            set { SetProperty(ref _scoreText, value); }
        }

        public string NmsText
        {
            get { return _nmsText; }
            set { SetProperty(ref _nmsText, value); }
        }

        public string ThresholdMessage
        {
            get { return _thresholdMessage; }
            private set { SetProperty(ref _thresholdMessage, value); }
        }

        public IRelayCommand StopCommand { get; }
        public IRelayCommand ApplyThresholdCommand { get; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            _timer.Tick -= OnTick;
            IsRunning = false;
            OnPropertyChanged(nameof(StatusText));
        }

        private void OnTick(object sender, EventArgs e)
        {
            PollStatus();
        }

        private void PollStatus()
        {
            if (_disposed) return;
            OnlineStatusSnapshot snapshot;
            _statusUnavailable = !TryParseStatus(_control.StatusJson(), out snapshot);
            if (_statusUnavailable)
            {
                // Keep Stop available even if a malformed snapshot cannot be displayed.
                IsRunning = _control.IsRunning;
                TotalP95Ms = null;
            }
            else
            {
                IsRunning = snapshot.Running;
                RecipeId = snapshot.RecipeId;
                Accepted = snapshot.Accepted;
                Dropped = snapshot.Dropped;
                Completed = snapshot.Completed;
                Failed = snapshot.Failed;
                TotalP95Ms = snapshot.TotalP95Ms;
            }

            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(BudgetState));
            OnPropertyChanged(nameof(P95BudgetText));
        }

        private void Stop()
        {
            if (_disposed || !IsRunning || _stopping) return;
            // OnlineRunner.Stop joins the worker and drains the queue; keep that off the UI thread.
            _stopping = true;
            StopCommand.NotifyCanExecuteChanged();
            ApplyThresholdCommand.NotifyCanExecuteChanged();
            Dispatcher dispatcher = _timer.Dispatcher;
            System.Threading.Tasks.Task.Run(() =>
            {
                try { _control.Stop(); }
                finally
                {
                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        _stopping = false;
                        PollStatus();
                        StopCommand.NotifyCanExecuteChanged();
                        ApplyThresholdCommand.NotifyCanExecuteChanged();
                    }));
                }
            });
        }

        private void ApplyThreshold()
        {
            if (_disposed || !IsRunning) return;
            double score;
            double nms;
            if (!TryParseThresholds(ScoreText, NmsText, out score, out nms))
            {
                ThresholdMessage = OnlineStrings.InvalidThreshold;
                return;
            }

            string error = _control.SetThreshold(null, score, nms);
            switch (error)
            {
                case null: ThresholdMessage = OnlineStrings.ThresholdApplied; break;
                case "NOT_RUNNING": ThresholdMessage = OnlineStrings.NotRunning; break;
                case "UNKNOWN_CLASS": ThresholdMessage = OnlineStrings.UnknownClass; break;
                case "INVALID_THRESHOLD": ThresholdMessage = OnlineStrings.InvalidThreshold; break;
                default: ThresholdMessage = OnlineStrings.ThresholdFailed; break;
            }

            PollStatus();
        }

        internal static bool TryParseThresholds(string scoreText, string nmsText, out double score, out double nms)
        {
            score = 0;
            nms = 0;
            return double.TryParse(scoreText, NumberStyles.Float, CultureInfo.InvariantCulture, out score)
                && double.TryParse(nmsText, NumberStyles.Float, CultureInfo.InvariantCulture, out nms)
                && !double.IsNaN(score) && !double.IsInfinity(score) && score >= 0 && score <= 1
                && !double.IsNaN(nms) && !double.IsInfinity(nms) && nms >= 0 && nms <= 1;
        }

        internal static bool TryParseStatus(string json, out OnlineStatusSnapshot snapshot)
        {
            snapshot = new OnlineStatusSnapshot();
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    JsonElement root = document.RootElement;
                    JsonElement value;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("running", out value)
                        || (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)) return false;
                    snapshot.Running = value.GetBoolean();
                    if (root.TryGetProperty("recipeId", out value))
                    {
                        if (value.ValueKind != JsonValueKind.String) return false;
                        snapshot.RecipeId = value.GetString() ?? string.Empty;
                    }

                    long count;
                    if (!TryReadCounter(root, "accepted", out count)) return false;
                    snapshot.Accepted = count;
                    if (!TryReadCounter(root, "dropped", out count)) return false;
                    snapshot.Dropped = count;
                    if (!TryReadCounter(root, "completed", out count)) return false;
                    snapshot.Completed = count;
                    if (!TryReadCounter(root, "failed", out count)) return false;
                    snapshot.Failed = count;
                    if (root.TryGetProperty("totalP95Ms", out value) && value.ValueKind != JsonValueKind.Null)
                    {
                        double p95;
                        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out p95)
                            || double.IsNaN(p95) || double.IsInfinity(p95) || p95 < 0) return false;
                        snapshot.TotalP95Ms = p95;
                    }
                }

                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool TryReadCounter(JsonElement root, string name, out long count)
        {
            count = 0;
            JsonElement value;
            return !root.TryGetProperty(name, out value)
                || (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out count) && count >= 0);
        }

        internal sealed class OnlineStatusSnapshot
        {
            internal bool Running { get; set; }
            internal string RecipeId { get; set; } = string.Empty;
            internal long Accepted { get; set; }
            internal long Dropped { get; set; }
            internal long Completed { get; set; }
            internal long Failed { get; set; }
            internal double? TotalP95Ms { get; set; }
        }
    }
}
