using System.Globalization;
using DotaTune.Core.Text;

namespace DotaTune.Tests;

public class PluralTests
{
    [Theory]
    [InlineData(0, "файлов")]
    [InlineData(1, "файл")]
    [InlineData(2, "файла")]
    [InlineData(4, "файла")]
    [InlineData(5, "файлов")]
    [InlineData(11, "файлов")]
    [InlineData(12, "файлов")]
    [InlineData(14, "файлов")]
    [InlineData(21, "файл")]
    [InlineData(22, "файла")]
    [InlineData(25, "файлов")]
    [InlineData(101, "файл")]
    [InlineData(111, "файлов")]
    [InlineData(-1, "файл")]
    public void Russian_forms(long count, string expected) =>
        Assert.Equal(expected, Plural.Choose(CultureInfo.GetCultureInfo("ru-RU"), count, "файл", "файла", "файлов"));

    [Theory]
    [InlineData(0, "files")]
    [InlineData(1, "file")]
    [InlineData(2, "files")]
    [InlineData(21, "files")]
    public void English_forms(long count, string expected) =>
        Assert.Equal(expected, Plural.Choose(CultureInfo.GetCultureInfo("en-US"), count, "file", "files", "files"));
}
