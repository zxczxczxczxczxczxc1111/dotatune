using System.Globalization;
using System.Text;
using System.Text.Json;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.HeroGrids;

namespace DotaTune.Tests;

public class HeroGridTests
{
    private const string GridPath = "remote/cfg/hero_grid_config.json";

    /// <summary>
    /// A file laid out as the game writes it (tabs, lists on their own line, six decimals, no
    /// line end at the end) with a Cyrillic name, a layout of Minify and one of the transfer.
    /// </summary>
    internal static readonly string GameFile = Tabs("""
        {
            "version": 3,
            "configs":
            [
                {
                    "config_name": "Мои герои",
                    "categories":
                    [
                        {
                            "category_name": "очень приятные герои",
                            "x_position": 933.913086,
                            "y_position": 530.869568,
                            "width": 241.304352,
                            "height": 50.217392,
                            "hero_ids":
                            [
                                113,
                                61
                            ]
                        },
                        {
                            "category_name": "",
                            "x_position": 0.000000,
                            "y_position": 0.000000,
                            "width": 144.130432,
                            "height": 393.260864,
                            "hero_ids":
                            [
                                11
                            ]
                        }
                    ]
                },
                {
                    "config_name": "Stratz - Immortal Meta #Minify-Import",
                    "categories":
                    [
                        {
                            "category_name": "Pos 1",
                            "x_position": 0.000000,
                            "y_position": 0.000000,
                            "width": 1100.000000,
                            "height": 107.742523,
                            "hero_ids":
                            [
                                52
                            ]
                        }
                    ]
                },
                {
                    "config_name": "С другого аккаунта #DotaTune",
                    "categories":
                    [
                        {
                            "category_name": "Керри",
                            "x_position": 10.500000,
                            "y_position": 20.250000,
                            "width": 300.000000,
                            "height": 80.000000,
                            "hero_ids":
                            [
                                1
                            ]
                        }
                    ]
                }
            ]
        }
        """);

    /// <summary>How D2PT and egezenn give their files: one line, other key order, long decimals.</summary>
    internal const string D2ptFile = """{"version":3,"configs":[{"categories":[{"width":455,"height":75,"hero_ids":[48,54,12],"x_position":0,"y_position":0,"category_name":"Carry"},{"width":455,"height":75,"hero_ids":[74,106],"x_position":0,"y_position":95,"category_name":"Mid"}],"config_name":"Dota2ProTracker 7.41f - All Roles"},{"categories":[{"width":65.123456789,"height":525,"hero_ids":[48],"x_position":0,"y_position":0,"category_name":"Top Heroes Pos 1"}],"config_name":"Dota2ProTracker 7.41f - Carry"}]}""";

    /// <summary>Every kind of player file under the cultures of the compatibility rules, the invariant one as "".</summary>
    public static TheoryData<string, string> PlayerFiles()
    {
        var data = new TheoryData<string, string>();
        foreach (string culture in new[] { "ru-RU", "en-US", "" })
        {
            foreach (string kind in new[] { "game", "compact", "empty", "empty-compact", "bom", "crlf", "spaces" })
            {
                data.Add(kind, culture);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PlayerFiles))]
    public void Put_in_and_taken_out_leaves_the_same_bytes(string kind, string cultureName)
    {
        using var culture = new CultureScope(cultureName);
        using var setup = new Setup();
        byte[] original = PlayerFile(kind);
        string file = setup.Write(original);
        IReadOnlyList<HeroGridLayout> catalog = GridCatalogStore.BuiltIn().Layouts;
        GridFile before = GridFile.Parse(original);

        GridResult put = setup.Service.Apply(setup.Steam.Read(), FakeSteam.AccountId, [.. catalog.Take(2).Select(l => new GridChoice(l.Name, l))]);

        Assert.Equal(new GridResult(null, 2, 0), put);
        byte[] with = File.ReadAllBytes(file);
        GridFile after = GridFile.Parse(with);
        Assert.Equal(before.Entries.Count + 2, after.Entries.Count);
        for (int i = 0; i < before.Entries.Count; i++)
        {
            // Every layout of the player is the same run of bytes.
            Assert.Equal(Slice(original, before.Entries[i]), Slice(with, after.Entries[i]));
        }

        Assert.Equal([catalog[0].Name + GridMark.Suffix, catalog[1].Name + GridMark.Suffix], after.Entries.Skip(before.Entries.Count).Select(e => e.Name));
        Assert.True(after.Entries[^1].Layout!.SameAs(catalog[1]));
        Assert.Equal(original, File.ReadAllBytes(setup.Backups.CopyOf(FakeSteam.AccountId)));

        GridResult removed = setup.Service.RemoveAll(setup.Steam.Read(), FakeSteam.AccountId);

        Assert.Equal(new GridResult(null, 0, 2), removed);
        Assert.Equal(original, File.ReadAllBytes(file));
        Assert.Empty(setup.Backups.Load());
        Assert.False(File.Exists(setup.Backups.CopyOf(FakeSteam.AccountId)));
    }

    [Fact]
    public void Layout_is_written_as_the_game_writes_it()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(GameFile);
        GridFile grid = GridFile.Parse(bytes);

        Assert.Equal(3, grid.Entries.Count);
        foreach (GridEntry entry in grid.Entries)
        {
            string name = entry.Name!;
            string written = entry.Layout!.Write(name[entry.Layout.Name.Length..], "\t\t");
            Assert.Equal(Encoding.UTF8.GetString(Slice(bytes, entry)), written);
        }
    }

    [Fact]
    public void Listed_but_missing_file_is_created_and_goes_away_again()
    {
        using var setup = new Setup();
        Directory.CreateDirectory(setup.Steam.AccountGameFolder());
        string file = Path.Combine(setup.Steam.AccountGameFolder(), "remote", "cfg", "hero_grid_config.json");
        HeroGridLayout layout = GridCatalogStore.BuiltIn().Layouts[0];

        setup.Service.Apply(setup.Steam.Read(), FakeSteam.AccountId, [new GridChoice(layout.Name, layout)]);

        Assert.Equal([layout.Name + GridMark.Suffix], GridFile.Parse(File.ReadAllBytes(file)).Entries.Select(e => e.Name));
        Assert.False(File.Exists(setup.Backups.CopyOf(FakeSteam.AccountId)));
        Assert.False(Assert.Single(setup.Backups.Load()).Existed);

        setup.Service.RemoveAll(setup.Steam.Read(), FakeSteam.AccountId);

        Assert.False(File.Exists(file));
        Assert.Empty(setup.Backups.Load());
    }

    [Fact]
    public void Repeat_changes_nothing_and_a_new_version_replaces_only_its_layout()
    {
        using var setup = new Setup();
        string file = setup.Write(Encoding.UTF8.GetBytes(GameFile));
        IReadOnlyList<HeroGridLayout> catalog = GridCatalogStore.BuiltIn().Layouts;
        HeroGridLayout a = catalog[0];
        HeroGridLayout b = catalog[1];
        setup.Service.Apply(setup.Steam.Read(), FakeSteam.AccountId, [new GridChoice(a.Name, a), new GridChoice(b.Name, b)]);
        byte[] first = File.ReadAllBytes(file);
        DateTime written = File.GetLastWriteTimeUtc(file);

        Assert.Equal(new GridResult(null, 0, 0), setup.Service.Apply(setup.Steam.Read(), FakeSteam.AccountId, [new GridChoice(a.Name, a), new GridChoice(b.Name, null)]));
        Assert.Equal(first, File.ReadAllBytes(file));
        Assert.Equal(written, File.GetLastWriteTimeUtc(file));

        var newer = new HeroGridLayout(b.Name, [.. b.Categories.Take(2)]);
        Assert.Equal(new GridResult(null, 1, 1), setup.Service.Apply(setup.Steam.Read(), FakeSteam.AccountId, [new GridChoice(a.Name, a), new GridChoice(b.Name, newer)]));
        GridFile grid = GridFile.Parse(File.ReadAllBytes(file));
        Assert.Equal(Slice(first, GridFile.Parse(first).Entries[3]), Slice(File.ReadAllBytes(file), grid.Entries[3]));
        Assert.True(grid.Entries[4].Layout!.SameAs(newer));

        Assert.Equal(new GridResult(null, 0, 1), setup.Service.Apply(setup.Steam.Read(), FakeSteam.AccountId, [new GridChoice(a.Name, a)]));
        Assert.Equal([a.Name + GridMark.Suffix], GridFile.Parse(File.ReadAllBytes(file)).Entries.Where(e => e.IsOurs).Select(e => e.Name));

        // The copy is still the file before the first change, not an intermediate one.
        Assert.Equal(GameFile, File.ReadAllText(setup.Backups.CopyOf(FakeSteam.AccountId)));
    }

    [Fact]
    public void Grid_the_player_made_in_between_stays()
    {
        using var setup = new Setup();
        string file = setup.Write(Encoding.UTF8.GetBytes(GameFile));
        HeroGridLayout layout = GridCatalogStore.BuiltIn().Layouts[0];
        setup.Service.Apply(setup.Steam.Read(), FakeSteam.AccountId, [new GridChoice(layout.Name, layout)]);

        // The game writes the file again with a new grid of the player behind ours.
        string mine = new HeroGridLayout("Новая", [new HeroGridCategory("Саппорты", 1, 2, 3, 4, [5, 6])]).Write(string.Empty, "\t\t");
        string text = File.ReadAllText(file);
        File.WriteAllText(file, text[..^"\n\t]\n}".Length] + ",\n\t\t" + mine + "\n\t]\n}");

        setup.Service.RemoveAll(setup.Steam.Read(), FakeSteam.AccountId);

        Assert.Equal(GameFile[..^"\n\t]\n}".Length] + ",\n\t\t" + mine + "\n\t]\n}", File.ReadAllText(file));
        Assert.Empty(setup.Backups.Load());
    }

    [Fact]
    public void Copy_follows_the_file_once_none_of_ours_is_left_in_it()
    {
        using var setup = new Setup();
        string file = setup.Write(Encoding.UTF8.GetBytes(GameFile));
        HeroGridLayout layout = GridCatalogStore.BuiltIn().Layouts[0];
        setup.Service.Apply(setup.Steam.Read(), FakeSteam.AccountId, [new GridChoice(layout.Name, layout)]);

        // A transfer put back or a preset replaced the file: ours are gone, the record is not.
        string other = GameFile.Replace("Мои герои", "Другие герои", StringComparison.Ordinal);
        File.WriteAllText(file, other);
        File.WriteAllText(file + ".dotatune.tmp", "обрывок");
        setup.Service.Apply(setup.Steam.Read(), FakeSteam.AccountId, [new GridChoice(layout.Name, layout)]);

        Assert.Equal(other, File.ReadAllText(setup.Backups.CopyOf(FakeSteam.AccountId)));
        Assert.False(File.Exists(file + ".dotatune.tmp"));
        setup.Service.RemoveAll(setup.Steam.Read(), FakeSteam.AccountId);
        Assert.Equal(other, File.ReadAllText(file));
    }

    [Fact]
    public void Account_without_a_dota_folder_is_forgotten()
    {
        using var setup = new Setup();
        string file = setup.Write(Encoding.UTF8.GetBytes(GameFile));
        HeroGridLayout layout = GridCatalogStore.BuiltIn().Layouts[0];
        setup.Service.Apply(setup.Steam.Read(), FakeSteam.AccountId, [new GridChoice(layout.Name, layout)]);
        Directory.Delete(setup.Steam.AccountGameFolder(), recursive: true);

        Assert.Equal(GridRefusal.NoAccount, setup.Service.RemoveAll(setup.Steam.Read(), FakeSteam.AccountId).Refusal);

        Assert.Empty(setup.Backups.Load());
        Assert.False(File.Exists(setup.Backups.CopyOf(FakeSteam.AccountId)));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Layouts_in_a_crlf_file_get_crlf()
    {
        using var setup = new Setup();
        string crlf = GameFile.Replace("\n", "\r\n", StringComparison.Ordinal);
        string file = setup.Write(Encoding.UTF8.GetBytes(crlf));
        HeroGridLayout layout = GridCatalogStore.BuiltIn().Layouts[0];

        setup.Service.Apply(setup.Steam.Read(), FakeSteam.AccountId, [new GridChoice(layout.Name, layout)]);

        string text = File.ReadAllText(file);
        Assert.Equal(text.Split('\n').Length, text.Split("\r\n").Length);
    }

    [Fact]
    public void File_without_our_layouts_is_not_written()
    {
        using var setup = new Setup();
        string file = setup.Write(Encoding.UTF8.GetBytes(GameFile));
        DateTime written = File.GetLastWriteTimeUtc(file);

        Assert.Equal(new GridResult(null, 0, 0), setup.Service.RemoveAll(setup.Steam.Read(), FakeSteam.AccountId));

        Assert.Equal(written, File.GetLastWriteTimeUtc(file));
        Assert.Empty(setup.Backups.Load());
    }

    [Fact]
    public void Broken_file_and_running_dota_are_refused_without_a_write()
    {
        using var setup = new Setup();
        string file = setup.Write(Encoding.UTF8.GetBytes("{\"version\": 3, \"configs\": [ {\"config_name\": \"Мои\"}, ]}"));
        HeroGridLayout layout = GridCatalogStore.BuiltIn().Layouts[0];

        GridResult broken = setup.Service.Apply(setup.Steam.Read(), FakeSteam.AccountId, [new GridChoice(layout.Name, layout)]);

        Assert.Equal(GridRefusal.BrokenFile, broken.Refusal);
        Assert.Contains(file, broken.Detail, StringComparison.Ordinal);
        Assert.Equal(GridRefusal.BrokenFile, HeroGridService.Read(setup.Steam.Read()).Problem);
        Assert.Empty(setup.Backups.Load());

        setup.Write(Encoding.UTF8.GetBytes(GameFile));
        setup.Steam.Running.Add("dota2");
        Assert.Equal(GridRefusal.DotaRunning, setup.Service.Apply(setup.Steam.Read(), FakeSteam.AccountId, [new GridChoice(layout.Name, layout)]).Refusal);
        Assert.Equal(GameFile, File.ReadAllText(file));
        setup.Steam.Running.Clear();
        Assert.Equal(GridRefusal.NoAccount, setup.Service.Apply(setup.Steam.Read(), 42, []).Refusal);
    }

    [Fact]
    public void Layouts_are_not_put_into_a_file_steam_does_not_list_but_are_taken_out()
    {
        using var setup = new Setup();
        HeroGridLayout layout = GridCatalogStore.BuiltIn().Layouts[0];
        string file = setup.Write(Encoding.UTF8.GetBytes(GameFile));
        setup.Service.Apply(setup.Steam.Read(), FakeSteam.AccountId, [new GridChoice(layout.Name, layout)]);
        byte[] ours = File.ReadAllBytes(file);
        File.Delete(Path.Combine(setup.Steam.AccountGameFolder(), "remotecache.vdf"));

        Assert.False(HeroGridService.Read(setup.Steam.Read()).SeenByGame);
        HeroGridLayout other = GridCatalogStore.BuiltIn().Layouts[1];
        Assert.Equal(new GridResult(GridRefusal.NotSeenByGame, 0, 0),
            setup.Service.Apply(setup.Steam.Read(), FakeSteam.AccountId, [new GridChoice(layout.Name, layout), new GridChoice(other.Name, other)]));
        Assert.Equal(ours, File.ReadAllBytes(file));

        Assert.Equal(new GridResult(null, 0, 1), setup.Service.RemoveAll(setup.Steam.Read(), FakeSteam.AccountId));
        Assert.Equal(GameFile, File.ReadAllText(file));
        Assert.Equal(new GridResult(GridRefusal.NotSeenByGame, 0, 0),
            setup.Service.Apply(setup.Steam.Read(), FakeSteam.AccountId, [new GridChoice(layout.Name, layout)]));
        Assert.Empty(setup.Backups.Load());
    }

    [Fact]
    public void Read_lists_our_layouts_of_the_signed_in_account()
    {
        using var setup = new Setup();
        setup.Write(Encoding.UTF8.GetBytes(GameFile));
        setup.Steam.SetCloud(FakeSteam.AccountId, on: false);
        HeroGridLayout layout = GridCatalogStore.BuiltIn().Layouts[2];
        setup.Service.Apply(setup.Steam.Read(), FakeSteam.AccountId, [new GridChoice(layout.Name, layout)]);

        GridAccountState state = HeroGridService.Read(setup.Steam.Read());

        Assert.Null(state.Problem);
        Assert.Equal(FakeSteam.AccountId, state.Account!.AccountId);
        Assert.False(state.CloudEnabled);
        Assert.True(state.SeenByGame);
        InstalledGrid installed = Assert.Single(state.Installed);
        Assert.Equal(layout.Name, installed.Name);
        Assert.True(installed.Layout!.SameAs(layout));
    }

    [Fact]
    public void Layouts_from_other_programs_are_checked()
    {
        IReadOnlyList<HeroGridLayout> d2pt = GridLayouts.Read(Encoding.UTF8.GetBytes(D2ptFile));

        Assert.Equal(["Dota2ProTracker 7.41f - All Roles", "Dota2ProTracker 7.41f - Carry"], d2pt.Select(l => l.Name));
        Assert.Equal(
            """{"config_name":"Dota2ProTracker 7.41f - Carry","categories":[{"category_name":"Top Heroes Pos 1","x_position":0.000000,"y_position":0.000000,"width":65.123459,"height":525.000000,"hero_ids":[48]}]}""",
            d2pt[1].Compact);

        string mixed = """
            {"version":3,"configs":[
              {"config_name":"Строка вместо героя","categories":[{"category_name":"a","x_position":0,"y_position":0,"width":1,"height":1,"hero_ids":["1"]}]},
              {"config_name":"Минус","categories":[{"category_name":"a","x_position":0,"y_position":0,"width":1,"height":1,"hero_ids":[-3]}]},
              {"config_name":"Перевод\nстроки","categories":[{"category_name":"a","x_position":0,"y_position":0,"width":1,"height":1,"hero_ids":[1]}]},
              {"config_name":"Без категорий"},
              {"config_name":"Огромная","categories":[{"category_name":"a","x_position":1e9,"y_position":0,"width":1,"height":1,"hero_ids":[1]}]},
              {"config_name":"Годная #DotaTune-Import","categories":[{"category_name":"a","x_position":0,"y_position":0,"width":1,"height":1,"hero_ids":[1,2]}]},
              {"config_name":"Годная","categories":[{"category_name":"дубль","x_position":0,"y_position":0,"width":1,"height":1,"hero_ids":[3]}]},
              7
            ]}
            """;
        HeroGridLayout good = Assert.Single(GridLayouts.Read(Encoding.UTF8.GetBytes(mixed)));
        Assert.Equal("Годная", good.Name);
        Assert.Equal([1, 2], good.Categories[0].HeroIds);

        Assert.Throws<InvalidDataException>(() => GridLayouts.Read(Encoding.UTF8.GetBytes("""{"version":3,"configs":[{"config_name":"Минус","categories":[]}]}""")));
        Assert.Throws<InvalidDataException>(() => GridLayouts.Read(Encoding.UTF8.GetBytes("<html>403</html>")));
        Assert.Throws<InvalidDataException>(() => GridLayouts.Read(Encoding.UTF8.GetBytes("""{"configs":{}}""")));
        Assert.Throws<InvalidDataException>(() => GridLayouts.Read(new byte[GridLayouts.MaxFileBytes + 1]));
    }

    [Fact]
    public void Art_grid_of_text_categories_is_taken_and_put_back_out()
    {
        // As art grids are made: thousands of empty categories whose names draw the picture,
        // long text lines, and a few big boxes for heroes with "POS" labels next to them.
        var categories = new List<HeroGridCategory>();
        for (int i = 0; i < 2000; i++)
        {
            categories.Add(new HeroGridCategory(".", i % 40 * 29.5, i / 40 * 11.75, 30, 30, []));
        }

        categories.Add(new HeroGridCategory("⣿" + new string('-', 200), 4.5, 300, 30, 30, []));
        categories.Add(new HeroGridCategory("1 POS", 600, 20, 60, 20, []));
        categories.Add(new HeroGridCategory(string.Empty, 660, 20, 528, 79, [1, 2, 3]));
        string file = "{\"version\":3,\"configs\":[" + new HeroGridLayout("Шизуку", categories).Write(string.Empty, null) + "],\"_comment\":\"gridstudio\"}";

        HeroGridLayout art = Assert.Single(GridLayouts.Read(Encoding.UTF8.GetBytes(file)));

        Assert.Equal(2003, art.Categories.Count);
        using var setup = new Setup();
        string path = setup.Write(Encoding.UTF8.GetBytes(GameFile));
        setup.Service.Apply(setup.Steam.Read(), FakeSteam.AccountId, [new GridChoice(art.Name, art)]);
        Assert.True(GridFile.Parse(File.ReadAllBytes(path)).Entries[^1].Layout!.SameAs(art));
        setup.Service.RemoveAll(setup.Steam.Read(), FakeSteam.AccountId);
        Assert.Equal(GameFile, File.ReadAllText(path));
    }

    [Fact]
    public void Numbers_keep_the_games_form_under_any_culture()
    {
        using var culture = new CultureScope("ru-RU");
        var layout = new HeroGridLayout("Дроби", [new HeroGridCategory("x", 107.74252707286725, -0.5, 1e-7, 790.1119065023294, [1])]);

        string text = layout.Write(string.Empty, null);

        // Expected values printed by C printf("%f") of the same floats.
        Assert.Contains("\"x_position\":107.742531,\"y_position\":-0.500000,\"width\":0.000000,\"height\":790.111877", text, StringComparison.Ordinal);
        using JsonDocument again = JsonDocument.Parse(text);
        Assert.Equal(text, HeroGridLayout.From(again.RootElement)!.Write(string.Empty, null));
    }

    [Fact]
    public void Cut_down_layout_keeps_its_earlier_full_version()
    {
        var full = new HeroGridLayout("Spectral - League Meta", [Category("Safe Lane Core"), Category("All Heroes")]);
        var cut = new HeroGridLayout("Spectral - League Meta", [Category("All Heroes")]);
        var other = new HeroGridLayout("Stratz - Turbo Meta", [Category("Pos 1")]);

        IReadOnlyList<HeroGridLayout> merged = GridLayouts.Merge([full], [other, cut]);

        Assert.Same(other, merged[0]);
        Assert.Same(full, merged[1]);
        Assert.Same(cut, GridLayouts.Merge([], [cut])[0]);

        static HeroGridCategory Category(string name) => new(name, 0, 0, 100, 50, [1, 2]);
    }

    [Fact]
    public async Task Catalog_uses_the_download_and_falls_back_to_the_built_in_copy()
    {
        using var folder = new CyrillicTempFolder();
        string cache = Path.Combine(folder.Path, "herogrids", "catalog.json");
        var log = new FileLog(Path.Combine(folder.Path, "logs"));
        GridCatalog builtIn = GridCatalogStore.BuiltIn();
        Assert.Equal(["Stratz - Immortal Meta", "Spectral - League Meta", "Stratz - Turbo Meta"], builtIn.Layouts.Select(l => l.Name));
        Assert.All(builtIn.Layouts, l => Assert.Equal(6, l.Categories.Count));

        var offline = new GridCatalogStore(cache, null, log);
        Assert.False(offline.IsStale(DateTimeOffset.Now));
        Assert.Equal(CatalogOrigin.BuiltIn, offline.Current().Origin);

        // The day's file with Spectral cut down to "All Heroes".
        string day = $$$"""{"version":3,"configs":[{{{builtIn.Layouts[0].Compact}}},{"config_name":"Spectral - League Meta","categories":[{"category_name":"All Heroes","x_position":0,"y_position":0,"width":1100,"height":790,"hero_ids":[1,2,3]}]}]}""";
        var online = new GridCatalogStore(cache, _ => Task.FromResult(Encoding.UTF8.GetBytes(day)), log);
        Assert.True(online.IsStale(DateTimeOffset.Now));

        GridCatalog downloaded = await online.RefreshAsync(CancellationToken.None);

        Assert.Equal(CatalogOrigin.Downloaded, downloaded.Origin);
        Assert.Equal(["Stratz - Immortal Meta", "Spectral - League Meta"], downloaded.Layouts.Select(l => l.Name));
        Assert.Equal(6, downloaded.Layouts[1].Categories.Count);
        Assert.False(online.IsStale(DateTimeOffset.Now));
        Assert.True(online.IsStale(DateTimeOffset.Now.AddHours(13)));
        Assert.True(offline.Current().Layouts[1].SameAs(builtIn.Layouts[1]));

        var failing = new GridCatalogStore(cache, _ => throw new HttpRequestException("нет сети"), log);
        await Assert.ThrowsAsync<HttpRequestException>(() => failing.RefreshAsync(CancellationToken.None));
        Assert.Equal(2, failing.Current().Layouts.Count);

        var answersHtml = new GridCatalogStore(cache, _ => Task.FromResult(Encoding.UTF8.GetBytes("<html></html>")), log);
        await Assert.ThrowsAsync<InvalidDataException>(() => answersHtml.RefreshAsync(CancellationToken.None));
        Assert.Equal(2, answersHtml.Current().Layouts.Count);

        await File.WriteAllTextAsync(cache, "{ испорчен", TestContext.Current.CancellationToken);
        Assert.Equal(CatalogOrigin.BuiltIn, offline.Current().Origin);
    }

    private static byte[] PlayerFile(string kind) => kind switch
    {
        "game" => Encoding.UTF8.GetBytes(GameFile),
        "compact" => Encoding.UTF8.GetBytes(D2ptFile),
        "empty" => GridFile.Empty,
        "empty-compact" => Encoding.UTF8.GetBytes("""{"version":3,"configs":[]}"""),
        "bom" => [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(GameFile)],
        "crlf" => Encoding.UTF8.GetBytes(GameFile.Replace("\n", "\r\n", StringComparison.Ordinal)),
        "spaces" => Encoding.UTF8.GetBytes(GameFile.Replace("\t", "  ", StringComparison.Ordinal)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static byte[] Slice(byte[] bytes, GridEntry entry) => bytes[entry.Start..entry.End];

    /// <summary>Four leading spaces of the source into one tab each, line ends into LF.</summary>
    private static string Tabs(string text) => string.Join('\n', text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line =>
    {
        int spaces = line.TakeWhile(c => c == ' ').Count();
        return new string('\t', spaces / 4) + line[(spaces / 4 * 4)..];
    }));

    /// <summary>A fake Steam with the signed-in account and the service DotaTune uses on it.</summary>
    private sealed class Setup : IDisposable
    {
        private readonly CyrillicTempFolder _data = new();

        public Setup()
        {
            Backups = new GridBackupStore(Path.Combine(_data.Path, "herogrids.json"), Path.Combine(_data.Path, "herogrids"), new FileLog(Path.Combine(_data.Path, "logs")));
            Service = new HeroGridService(Backups, new FileLog(Path.Combine(_data.Path, "logs")));
            Steam.SetGridSeen();
        }

        public FakeSteam Steam { get; } = new();

        public GridBackupStore Backups { get; }

        public HeroGridService Service { get; }

        public string Write(byte[] content)
        {
            string file = Steam.WriteGameFile(FakeSteam.AccountId, GridPath, string.Empty);
            File.WriteAllBytes(file, content);
            return file;
        }

        public void Dispose()
        {
            Steam.Dispose();
            _data.Dispose();
        }
    }
}
