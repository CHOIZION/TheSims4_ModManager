using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Sims4ModUpdater.Core.Models;

namespace Sims4ModUpdater.Infrastructure;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        if (parameter is string p && p.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool visible = value is Visibility v && v == Visibility.Visible;
        if (parameter is string p && p.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            visible = !visible;
        return visible;
    }
}

public sealed class NullOrEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool hasValue = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            int count => count > 0,
            System.Collections.ICollection col => col.Count > 0,
            _ => true
        };

        if (parameter is string p && p.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            hasValue = !hasValue;

        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BytesToReadableConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        long bytes = value switch
        {
            long l => l,
            int i => i,
            _ => 0
        };

        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F2} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StatusCodeToBackgroundConverter : IValueConverter
{
    private static readonly SolidColorBrush UpToDateBg = CreateBrush("#12352A");
    private static readonly SolidColorBrush UpdateNeededBg = CreateBrush("#3B2D12");
    private static readonly SolidColorBrush ManualBg = CreateBrush("#1C2C44");
    private static readonly SolidColorBrush PaidBg = CreateBrush("#2F2142");
    private static readonly SolidColorBrush ErrorBg = CreateBrush("#3F1A1D");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ModUpdateStatusCode code)
        {
            return code switch
            {
                ModUpdateStatusCode.UpToDate => UpToDateBg,
                ModUpdateStatusCode.UpdateNeeded => UpdateNeededBg,
                ModUpdateStatusCode.ManualCheck => ManualBg,
                ModUpdateStatusCode.Paid or ModUpdateStatusCode.EarlyAccess => PaidBg,
                ModUpdateStatusCode.LinkNotFound or ModUpdateStatusCode.Error => ErrorBg,
                _ => ManualBg
            };
        }
        return ManualBg;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush CreateBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}

public sealed class StatusCodeToForegroundConverter : IValueConverter
{
    private static readonly SolidColorBrush UpToDateFg = CreateBrush("#34D399");
    private static readonly SolidColorBrush UpdateNeededFg = CreateBrush("#FBBF24");
    private static readonly SolidColorBrush ManualFg = CreateBrush("#60A5FA");
    private static readonly SolidColorBrush PaidFg = CreateBrush("#C084FC");
    private static readonly SolidColorBrush ErrorFg = CreateBrush("#F87171");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ModUpdateStatusCode code)
        {
            return code switch
            {
                ModUpdateStatusCode.UpToDate => UpToDateFg,
                ModUpdateStatusCode.UpdateNeeded => UpdateNeededFg,
                ModUpdateStatusCode.ManualCheck => ManualFg,
                ModUpdateStatusCode.Paid or ModUpdateStatusCode.EarlyAccess => PaidFg,
                ModUpdateStatusCode.LinkNotFound or ModUpdateStatusCode.Error => ErrorFg,
                _ => ManualFg
            };
        }
        return ManualFg;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static SolidColorBrush CreateBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}

public sealed class EqualityToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not null && parameter is not null &&
           string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter is not null)
        {
            if (targetType.IsEnum)
                return Enum.Parse(targetType, parameter.ToString()!, ignoreCase: true);
            return parameter;
        }
        return Binding.DoNothing;
    }
}
