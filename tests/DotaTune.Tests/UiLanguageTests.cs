namespace DotaTune.Tests;

public class UiLanguageTests
{
    [Theory]
    [InlineData("ru", "ru-RU")]
    [InlineData("en", "en-US")]
    public void Explicit_choice_wins(string choice, string expected) =>
        Assert.Equal(expected, UiLanguage.Resolve(choice).Name);

    [Fact]
    public void Unknown_choice_follows_windows()
    {
        string expected = UiLanguage.Windows.TwoLetterISOLanguageName == "ru" ? "ru-RU" : "en-US";

        Assert.Equal(expected, UiLanguage.Resolve(null).Name);
        Assert.Equal(expected, UiLanguage.Resolve("de").Name);
    }
}
