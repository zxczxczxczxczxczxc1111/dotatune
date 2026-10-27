using System.Globalization;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Media;

namespace DotaTune.Tests;

/// <summary>
/// The built-in fonts must draw every letter of both languages themselves. A missing
/// glyph would silently come from another font and show up as mixed letter shapes.
/// </summary>
public class FontCoverageTests
{
    [AvaloniaTheory]
    [InlineData("Onest", FontWeight.Normal)]
    [InlineData("Onest", FontWeight.Bold)]
    [InlineData("Unbounded", FontWeight.SemiBold)]
    public void Embedded_font_has_every_character_of_both_string_files(string family, FontWeight weight)
    {
        var typeface = new Typeface(new FontFamily($"avares://DotaTune/Assets/Fonts#{family}"), FontStyle.Normal, weight);

        Assert.True(FontManager.Current.TryGetGlyphTypeface(typeface, out GlyphTypeface? glyphs), $"{family} {weight} is not loaded");
        Assert.StartsWith(family, glyphs!.FamilyName, StringComparison.Ordinal);
        Assert.Equal(weight, glyphs.Weight);

        // The installer draws with the same fonts, so its strings count too.
        string text = string.Concat(
            ResourceTests.Load("Strings.resx").Values
                .Concat(ResourceTests.Load("Strings.ru.resx").Values)
                .Concat(ResourceTests.Load("DotaTune.Setup", "SetupStrings.resx").Values)
                .Concat(ResourceTests.Load("DotaTune.Setup", "SetupStrings.ru.resx").Values)) + "DotaTune";
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(rune) && !glyphs.CharacterToGlyphMap.ContainsGlyph(rune.Value))
            {
                missing.Add(string.Create(CultureInfo.InvariantCulture, $"U+{rune.Value:X4} {rune}"));
            }
        }

        Assert.Empty(missing);
    }
}
