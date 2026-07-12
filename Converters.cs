using System.Globalization;
using System.Windows.Data;

namespace SentryNet;

public static class Fmt
{
    public static string Bytes(double v)
    {
        if (v < 1024) return $"{v:0} B";
        if (v < 1024 * 1024) return $"{v / 1024:0.0} KB";
        if (v < 1024L * 1024 * 1024) return $"{v / (1024.0 * 1024):0.0} MB";
        return $"{v / (1024.0 * 1024 * 1024):0.00} GB";
    }

    public static string Rate(double v) => v <= 0 ? "—" : Bytes(v) + "/s";
}

/// <summary>Formats a byte count; ConverterParameter="rate" appends /s and dashes zero.</summary>
public sealed class HumanBytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double v = value switch
        {
            long l => l,
            double d => d,
            int i => i,
            _ => 0
        };
        if (Equals(parameter, "rate")) return Fmt.Rate(v);
        return v <= 0 ? "—" : Fmt.Bytes(v);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
