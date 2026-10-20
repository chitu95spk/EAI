using System.Globalization;
using System.Resources;

namespace EasyAI.Desktop.Resources
{
    /// <summary>Online-mode labels using the application's selected UI culture.</summary>
    public static class OnlineStrings
    {
        private static readonly ResourceManager Manager =
            new ResourceManager("EasyAI.Desktop.Resources.OnlineStrings", typeof(OnlineStrings).Assembly);

        private static string Get(string name)
        {
            return Manager.GetString(name, Strings.Culture ?? CultureInfo.CurrentUICulture) ?? name;
        }

        public static string WindowTitle { get { return Get(nameof(WindowTitle)); } }
        public static string StatusRunning { get { return Get(nameof(StatusRunning)); } }
        public static string StatusStopped { get { return Get(nameof(StatusStopped)); } }
        public static string StatusUnavailable { get { return Get(nameof(StatusUnavailable)); } }
        public static string Recipe { get { return Get(nameof(Recipe)); } }
        public static string Accepted { get { return Get(nameof(Accepted)); } }
        public static string Completed { get { return Get(nameof(Completed)); } }
        public static string Dropped { get { return Get(nameof(Dropped)); } }
        public static string Failed { get { return Get(nameof(Failed)); } }
        public static string Unavailable { get { return Get(nameof(Unavailable)); } }
        public static string P95BudgetFormat { get { return Get(nameof(P95BudgetFormat)); } }
        public static string Thresholds { get { return Get(nameof(Thresholds)); } }
        public static string Score { get { return Get(nameof(Score)); } }
        public static string Nms { get { return Get(nameof(Nms)); } }
        public static string Apply { get { return Get(nameof(Apply)); } }
        public static string Stop { get { return Get(nameof(Stop)); } }
        public static string InvalidThreshold { get { return Get(nameof(InvalidThreshold)); } }
        public static string ThresholdApplied { get { return Get(nameof(ThresholdApplied)); } }
        public static string NotRunning { get { return Get(nameof(NotRunning)); } }
        public static string UnknownClass { get { return Get(nameof(UnknownClass)); } }
        public static string ThresholdFailed { get { return Get(nameof(ThresholdFailed)); } }
    }
}
