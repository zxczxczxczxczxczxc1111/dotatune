using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using DotaTune.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace DotaTune.Tests;

public static class TestAppBuilder
{
    // Real Skia rendering instead of the headless stub, so windows can be captured as images.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Runs a block under the given culture, both formatting and UI.</summary>
public sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _culture;
    private readonly CultureInfo _uiCulture;

    public CultureScope(string name)
    {
        _culture = CultureInfo.CurrentCulture;
        _uiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
    }
}

/// <summary>A temporary folder whose path has Cyrillic letters and a space, as on many players' machines.</summary>
public sealed class CyrillicTempFolder : IDisposable
{
    public CyrillicTempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DotaTune тесты", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

public static class RepoPaths
{
    /// <summary>Repository root, found by walking up to DotaTune.slnx.</summary>
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "DotaTune.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"DotaTune.slnx not found above {AppContext.BaseDirectory}");
    }
}
