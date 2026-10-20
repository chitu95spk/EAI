using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading.Tasks;
using EasyAI.Desktop.Resources;
using EasyLabel.SegAI;
using Newtonsoft.Json;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace EasyAI.Desktop.ViewModels
{
    public sealed partial class MainViewModel
    {
        private async Task<bool> SynchronizeAnnotationClassesAsync([AllowNull] SegAiSession session)
        {
            if (session == null || !ReferenceEquals(session, _segSession)) return false;
            try
            {
                IReadOnlyList<string> skipped = Array.Empty<string>();
                IReadOnlyList<string> added = await Task.Run(() => session.SynchronizeClassTableFromAnnotations(out skipped));
                if (!ReferenceEquals(session, _segSession)) return false;
                foreach (string problem in skipped) _store.Log("Annotation class skipped: " + problem);
                if (added.Count == 0)
                {
                    if (skipped.Count > 0)
                        ShowDataNotice(Strings.NewLabelTitle, string.Format(Strings.NewLabelFailedFormat, string.Join("; ", skipped)), InfoBarSeverity.Warning);
                    return false;
                }
                Canvas.RefreshClasses();
                ClassCount = session.ClassTable.Classes.Count;
                ShowDataNotice(Strings.NewLabelTitle, string.Format(Strings.ClassTableSyncAddedFormat, string.Join(", ", added)), InfoBarSeverity.Informational);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException
                || ex is JsonException || ex is InvalidOperationException)
            {
                _store.Log("Annotation class sync failed: " + ex);
                ShowDataNotice(Strings.NewLabelTitle, string.Format(Strings.NewLabelFailedFormat, ex.Message), InfoBarSeverity.Warning);
                return false;
            }
        }
    }
}
