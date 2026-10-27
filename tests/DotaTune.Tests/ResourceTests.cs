using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DotaTune.Tests;

/// <summary>Both string files have to stay in step: same keys, same placeholders, no empty texts.</summary>
public partial class ResourceTests
{
    /// <summary>The program's strings and the installer's, each as project folder and file name without the language.</summary>
    public static TheoryData<string, string> Sets() => new()
    {
        { "DotaTune", "Strings" },
        { "DotaTune.Setup", "SetupStrings" },
    };

    public static Dictionary<string, string> Load(string file) => Load("DotaTune", file);

    public static Dictionary<string, string> Load(string project, string file) =>
        XDocument.Load(Path.Combine(RepoPaths.Root, "src", project, "Resources", file))
            .Root!
            .Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty, StringComparer.Ordinal);

    [Theory]
    [MemberData(nameof(Sets))]
    public void Russian_and_English_have_the_same_keys(string project, string name)
    {
        Dictionary<string, string> en = Load(project, $"{name}.resx");
        Dictionary<string, string> ru = Load(project, $"{name}.ru.resx");

        Assert.Empty(en.Keys.Except(ru.Keys, StringComparer.Ordinal));
        Assert.Empty(ru.Keys.Except(en.Keys, StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Sets))]
    public void No_empty_texts_and_no_long_dashes(string project, string name)
    {
        foreach (string file in new[] { $"{name}.resx", $"{name}.ru.resx" })
        {
            foreach ((string key, string value) in Load(project, file))
            {
                Assert.False(string.IsNullOrWhiteSpace(value), $"{file}: {key} is empty");
                Assert.DoesNotContain('—', value);
                Assert.DoesNotContain('–', value);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Sets))]
    public void Placeholders_match(string project, string name)
    {
        Dictionary<string, string> en = Load(project, $"{name}.resx");
        Dictionary<string, string> ru = Load(project, $"{name}.ru.resx");

        foreach ((string key, string text) in en)
        {
            string[] expected = Placeholders(text);
            string[] actual = Placeholders(ru[key]);
            Assert.True(expected.SequenceEqual(actual), $"{key}: {string.Join(',', expected)} vs {string.Join(',', actual)}");
        }
    }

    private static string[] Placeholders(string text) =>
        [.. PlaceholderPattern().Matches(text).Select(m => m.Value).Order(StringComparer.Ordinal)];

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex PlaceholderPattern();
}
