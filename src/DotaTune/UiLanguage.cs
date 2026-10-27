using System.Globalization;

namespace DotaTune;

/// <summary>
/// Interface language. Only the UI culture changes here: number and date format stay
/// as set in Windows, and neither the game language nor the Windows language is touched.
/// </summary>
internal static class UiLanguage
{
    public const string Russian = "ru";
    public const string English = "en";

    /// <summary>Display language of Windows at startup.</summary>
    public static CultureInfo Windows { get; private set; } = CultureInfo.CurrentUICulture;

    public static void CaptureWindowsLanguage() => Windows = CultureInfo.CurrentUICulture;

    /// <summary>Maps a saved choice ("ru", "en" or null for Windows) to the culture the interface uses.</summary>
    public static CultureInfo Resolve(string? choice)
    {
        string language = choice switch
        {
            Russian or English => choice,
            _ => string.Equals(Windows.TwoLetterISOLanguageName, Russian, StringComparison.Ordinal) ? Russian : English,
        };
        return CultureInfo.GetCultureInfo(language == Russian ? "ru-RU" : "en-US");
    }

    public static void Apply(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }
}
