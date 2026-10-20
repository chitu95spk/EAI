using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Infrastructure;
using EasyAI.Desktop.Resources;
using EasyLabel.SegAI;
using EasyLabel.SegAI.Model;

namespace EasyAI.Desktop.ViewModels;

/// <summary>One class row of the class table editor: name, kind (Defect or Good) and the enabled flag.</summary>
public sealed class ClassTableRowViewModel : ObservableObject
{
    private string _name;
    private SegAiClassKind _kind;
    private bool _enabled;

    public ClassTableRowViewModel(int position, string name, SegAiClassKind kind, bool enabled, string groupName)
    {
        Position = position;
        _name = name;
        _kind = kind;
        _enabled = enabled;
        GroupName = groupName;
    }

    /// <summary>0-based row position (rows are only appended, so it is stable); suffixes the AutomationIds.</summary>
    public int Position { get; }

    /// <summary>Radio group of this row's kind buttons (unique per row, so each row chooses its own kind).</summary>
    public string GroupName { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public SegAiClassKind Kind
    {
        get => _kind;
        set
        {
            if (SetProperty(ref _kind, value))
            {
                OnPropertyChanged(nameof(IsDefect));
                OnPropertyChanged(nameof(IsGood));
            }
        }
    }

    public bool IsDefect
    {
        get => _kind == SegAiClassKind.Defect;
        set
        {
            if (value) Kind = SegAiClassKind.Defect;
        }
    }

    public bool IsGood
    {
        get => _kind == SegAiClassKind.Good;
        set
        {
            if (value) Kind = SegAiClassKind.Good;
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }
}

/// <summary>
/// Class table editor: rename, kind, enable and add classes. No delete and no reorder (class ids follow the position, so labelled
/// data would shift). OK validates the draft, saves it only when it changed, and keeps the dialog open with the error otherwise.
/// </summary>
public sealed partial class ClassTableViewModel : ObservableObject
{
    private readonly SegAiSession _session;
    private readonly List<SegAiClassEntry> _saved;
    private int _groupSeed;
    private string _errorText = string.Empty;

    public ClassTableViewModel(SegAiSession session)
    {
        _session = session;
        _saved = session.ClassTable.Classes.ToList();
        Rows = new ObservableCollection<ClassTableRowViewModel>();
        foreach (SegAiClassEntry entry in _saved)
        {
            Rows.Add(NewRow(entry.Name, entry.Kind, entry.Enabled));
        }
    }

    public ObservableCollection<ClassTableRowViewModel> Rows { get; }

    /// <summary>True while the table holds fewer than <see cref="SegAiClassTable.MaxClasses"/> classes.</summary>
    public bool CanAdd => Rows.Count < SegAiClassTable.MaxClasses;

    /// <summary>Validation message of the last OK, or empty.</summary>
    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _errorText.Length > 0;

    /// <summary>True after <see cref="TrySave"/> wrote a changed table.</summary>
    public bool Saved { get; private set; }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddClass()
    {
        Rows.Add(NewRow("Class " + (Rows.Count + 1).ToString(CultureInfo.InvariantCulture), SegAiClassKind.Defect, true));
        OnPropertyChanged(nameof(CanAdd));
        AddClassCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// OK: validates the draft (the error is shown inline on failure), saves it only when it differs from the stored table and
    /// returns true when the dialog may close.
    /// </summary>
    public bool TrySave()
    {
        var draft = Rows.Select(r => new SegAiClassEntry(r.Name, r.Kind, r.Enabled)).ToList();
        SegAiModelProblem? problem = ClassTableEditRules.Validate(draft);
        if (problem != null)
        {
            ErrorText = Localize(problem);
            return false;
        }

        if (!ClassTableEditRules.HasChanges(_saved, draft))
        {
            ErrorText = string.Empty;
            Saved = false;
            return true;
        }

        try
        {
            _session.SaveClassTable(new SegAiClassTable(draft));
        }
        catch (Exception ex) when (ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
        {
            ErrorText = Strings.ClassEditSaveFailed + ex.Message;
            return false;
        }

        ErrorText = string.Empty;
        Saved = true;
        return true;
    }

    private ClassTableRowViewModel NewRow(string name, SegAiClassKind kind, bool enabled)
    {
        _groupSeed++;
        return new ClassTableRowViewModel(Rows.Count, name, kind, enabled, "ClassKind" + _groupSeed.ToString(CultureInfo.InvariantCulture));
    }

    private static string Localize(SegAiModelProblem problem)
    {
        CultureInfo culture = Strings.Culture ?? CultureInfo.CurrentUICulture;
        return string.Equals(culture.TwoLetterISOLanguageName, "vi", StringComparison.OrdinalIgnoreCase) ? problem.Vi : problem.En;
    }
}
