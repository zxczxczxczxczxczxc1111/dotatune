using System.Globalization;

namespace DotaTune.Core.Text;

/// <summary>Picks the plural form of a word for a number.</summary>
public static class Plural
{
    /// <summary>
    /// Returns <paramref name="one"/>, <paramref name="few"/> or <paramref name="many"/>
    /// by the rules of <paramref name="culture"/>. Russian has three forms
    /// (1 файл, 2 файла, 5 файлов); every other language here uses one and many.
    /// </summary>
    public static string Choose(CultureInfo culture, long count, string one, string few, string many)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return string.Equals(culture.TwoLetterISOLanguageName, "ru", StringComparison.Ordinal)
            ? Russian(count, one, few, many)
            : Math.Abs(count) == 1 ? one : many;
    }

    /// <summary>Russian rule: 1, 21, 101 take one; 2-4, 22-24 take few; 0, 5-20, 111-114 take many.</summary>
    public static string Russian(long count, string one, string few, string many)
    {
        long n = Math.Abs(count);
        long lastTwo = n % 100;
        if (lastTwo is >= 11 and <= 14)
        {
            return many;
        }

        return (n % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }
}
