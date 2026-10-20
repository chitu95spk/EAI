using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasyAI.Desktop.Resources;
using EasyLabel.Camera;

namespace EasyAI.Desktop.ViewModels;

/// <summary>One row of the "Set parameter" panel. Values are applied on commit by <see cref="LiveCameraViewModel"/>; refreshes never write back.</summary>
public abstract class LiveParamRowViewModel : ObservableObject
{
    private string? _error;
    private bool _isEditable = true;
    private string _display = string.Empty;

    protected LiveParamRowViewModel(LiveCameraViewModel owner, LiveParamRowModel model)
    {
        Owner = owner;
        Id = model.Id;
        Kind = model.Kind;
        LabelKey = model.LabelKey;
    }

    protected LiveCameraViewModel Owner { get; }

    public CameraParameterId Id { get; }

    public CameraParameterKind Kind { get; }

    protected string LabelKey { get; }

    /// <summary>True while a refresh assigns values: box write-backs (rounding, selection resets) must not be committed.</summary>
    protected bool Updating { get; set; }

    public string Label => Strings.ByName(LabelKey);

    public string AutomationId => "LiveParam_" + Id;

    public string AutoAutomationId => "LiveParamAuto_" + Id;

    public string ErrorAutomationId => "LiveParamError_" + Id;

    public bool IsEditable
    {
        get => _isEditable;
        private set => SetProperty(ref _isEditable, value);
    }

    /// <summary>Current value as text (option name, or number with unit); also the automation status of the box.</summary>
    public string Display
    {
        get => _display;
        private set => SetProperty(ref _display, value);
    }

    public string? Error => _error;

    public bool HasError => !string.IsNullOrEmpty(_error);

    internal void SetError(string? error)
    {
        string? value = string.IsNullOrWhiteSpace(error) ? null : error;
        if (string.Equals(_error, value, StringComparison.Ordinal))
        {
            return;
        }

        _error = value;
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
    }

    /// <summary>Takes the freshly read model. Same Id and Kind only.</summary>
    internal void Update(LiveParamRowModel model)
    {
        Updating = true;
        try
        {
            IsEditable = model.IsEditable;
            Display = model.Display;
            UpdateCore(model);
        }
        finally
        {
            Updating = false;
        }
    }

    protected abstract void UpdateCore(LiveParamRowModel model);
}

/// <summary>Numeric row: a number box with the camera's range and step, plus an Auto button when the camera has an auto mode.</summary>
public sealed class LiveNumberParamViewModel : LiveParamRowViewModel
{
    private double? _editValue;
    private double _applied;
    private double _minimum;
    private double _maximum;
    private double _smallChange = 1;
    private double _largeChange = 10;
    private int _maxDecimalPlaces;
    private bool _supportsAuto;
    private bool _isAuto;

    public LiveNumberParamViewModel(LiveCameraViewModel owner, LiveParamRowModel model)
        : base(owner, model)
    {
        ToggleAutoCommand = new RelayCommand(ToggleAuto);
        Update(model);
    }

    public IRelayCommand ToggleAutoCommand { get; }

    public double Minimum
    {
        get => _minimum;
        private set => SetProperty(ref _minimum, value);
    }

    public double Maximum
    {
        get => _maximum;
        private set => SetProperty(ref _maximum, value);
    }

    public double SmallChange
    {
        get => _smallChange;
        private set => SetProperty(ref _smallChange, value);
    }

    public double LargeChange
    {
        get => _largeChange;
        private set => SetProperty(ref _largeChange, value);
    }

    public int MaxDecimalPlaces
    {
        get => _maxDecimalPlaces;
        private set => SetProperty(ref _maxDecimalPlaces, value);
    }

    public bool SupportsAuto
    {
        get => _supportsAuto;
        private set => SetProperty(ref _supportsAuto, value);
    }

    public bool IsAuto
    {
        get => _isAuto;
        private set => SetProperty(ref _isAuto, value);
    }

    /// <summary>Bound to the number box. The box commits on Enter, on losing focus and with its spin buttons.</summary>
    public double? EditValue
    {
        get => _editValue;
        set
        {
            if (Updating)
            {
                return;
            }

            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
            {
                // Cleared box: show the applied value again.
                OnPropertyChanged(nameof(EditValue));
                return;
            }

            double normalized = LiveCameraRules.Normalize(value.Value, Minimum, Maximum, 0);
            _editValue = normalized;
            OnPropertyChanged(nameof(EditValue));
            if (LiveCameraRules.SameAtPrecision(normalized, _applied, MaxDecimalPlaces))
            {
                return;
            }

            Owner.CommitValue(this, normalized);
        }
    }

    protected override void UpdateCore(LiveParamRowModel model)
    {
        Minimum = model.Min;
        Maximum = model.Max;
        SmallChange = model.SmallChange;
        LargeChange = model.LargeChange;
        MaxDecimalPlaces = model.Decimals;
        SupportsAuto = model.SupportsAuto;
        IsAuto = model.IsAuto;
        _applied = model.Value;

        // Rounded to the shown precision so the box's own rounding writes back the same number.
        double shown = Math.Round(model.Value, Math.Max(0, Math.Min(model.Decimals, 15)));
        if (_editValue != shown)
        {
            _editValue = shown;
            OnPropertyChanged(nameof(EditValue));
        }
    }

    private void ToggleAuto()
    {
        Owner.CommitAuto(this, !IsAuto);
    }
}

/// <summary>Choice row: a drop-down of the camera's options (trigger mode, trigger source, pixel format, white balance mode).</summary>
public sealed class LiveOptionParamViewModel : LiveParamRowViewModel
{
    private IReadOnlyList<CameraParameterOption> _options = Array.Empty<CameraParameterOption>();
    private long? _selectedValue;
    private long _applied;

    public LiveOptionParamViewModel(LiveCameraViewModel owner, LiveParamRowModel model)
        : base(owner, model)
    {
        Update(model);
    }

    public IReadOnlyList<CameraParameterOption> Options => _options;

    /// <summary>Bound to the drop-down; a user selection is committed at once.</summary>
    public long? SelectedValue
    {
        get => _selectedValue;
        set
        {
            if (Updating || !value.HasValue)
            {
                return;
            }

            if (value.Value == _applied && value == _selectedValue)
            {
                return;
            }

            _selectedValue = value;
            OnPropertyChanged(nameof(SelectedValue));
            if (value.Value != _applied)
            {
                Owner.CommitValue(this, value.Value);
            }
        }
    }

    protected override void UpdateCore(LiveParamRowModel model)
    {
        _applied = (long)model.Value;
        if (!SameOptions(_options, model.Options))
        {
            _options = model.Options;
            OnPropertyChanged(nameof(Options));
        }

        _selectedValue = _applied;
        OnPropertyChanged(nameof(SelectedValue));
    }

    private static bool SameOptions(IReadOnlyList<CameraParameterOption> a, IReadOnlyList<CameraParameterOption> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Value != b[i].Value || !string.Equals(a[i].Name, b[i].Name, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
