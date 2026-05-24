using System.Globalization;
using System.Windows.Data;

namespace LexiVerseVideoCaptioner.Converters;
// ────────────────────────────────────────────────────────────────────
// TimeSpan → "MM:SS" 変換コンバーター（XAML バインディング用）
// ────────────────────────────────────────────────────────────────────
public class TimeSpanToStringConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is TimeSpan ts)
            return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
        return "00:00";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}