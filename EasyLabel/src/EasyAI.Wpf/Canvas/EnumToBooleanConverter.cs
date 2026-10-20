using System.Globalization;
using System.Windows.Data;

namespace EasyAI.Desktop.Annotation;

/// <summary>Binds one enum value to a toggle: checked when the bound value equals the converter parameter.</summary>
public sealed class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value != null && parameter != null && value.Equals(parameter);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true && parameter != null ? parameter : Binding.DoNothing;
    }
}
