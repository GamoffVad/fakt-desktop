using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Fakt.Core.Files;
using Fakt.Desktop.Controls;

namespace Fakt.Desktop.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public bool Collapse { get; set; } = true;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is bool b && b;
        if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Collapse ? Visibility.Collapsed : Visibility.Hidden;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is Visibility v && v == Visibility.Visible ^ Invert;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value != null && !(value is string s && s.Length == 0);
        if (Invert)
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var count = value is int i ? i : value is long l ? l : 0;
        var visible = count > 0;
        return visible ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Размер файла в единицах «Б/КБ/МБ/ГБ» с русским форматом чисел.</summary>
public sealed class FileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (!(value is long size))
        {
            return null;
        }

        return Format(size);
    }

    public static string Format(long size)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        if (size < 1024)
        {
            return size.ToString("N0", ru) + " Б";
        }

        if (size < 1024 * 1024)
        {
            return (size / 1024.0).ToString("N1", ru) + " КБ";
        }

        if (size < 1024L * 1024 * 1024)
        {
            return (size / 1048576.0).ToString("N1", ru) + " МБ";
        }

        return (size / 1073741824.0).ToString("N2", ru) + " ГБ";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class LocalDateTimeConverter : IValueConverter
{
    public string Format { get; set; } = "dd.MM.yyyy HH:mm";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        switch (value)
        {
            case DateTime date:
                var local = date.Kind == DateTimeKind.Utc ? date.ToLocalTime() : date;
                return local.ToString(parameter as string ?? Format, CultureInfo.GetCultureInfo("ru-RU"));
            default:
                return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NumberConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        switch (value)
        {
            case long l: return l.ToString("N0", ru);
            case int i: return i.ToString("N0", ru);
            case null: return "—";
            default: return value.ToString();
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Категория статуса → тон метки. Цвет дополняет текст статуса, но не заменяет его.</summary>
public sealed class ToneConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var tone = value switch
        {
            StatusTone t => t,
            FileStatus s => s.ToTone(),
            _ => StatusTone.Neutral,
        };
        return tone switch
        {
            StatusTone.Success => BadgeTone.Success,
            StatusTone.Warning => BadgeTone.Warning,
            StatusTone.Error => BadgeTone.Error,
            StatusTone.Info => BadgeTone.Info,
            _ => BadgeTone.Neutral,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class FileStatusTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is FileStatus status ? status.ToText() : value?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class EqualityToBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Equals(value?.ToString(), parameter?.ToString());

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool b && b ? Enum.Parse(targetType, parameter.ToString()) : Binding.DoNothing;
}
