using System.Text;
using System.Text.Json;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Effects;
using DotaTune.Core.Game;
using DotaTune.Core.Pack;
using DotaTune.Core.Platform;
using DotaTune.Core.Settings;
using ValvePak;

namespace DotaTune.Tests;

public sealed class EffectsTests : IDisposable
{
    private readonly FakeSteam _steam = new();

    public void Dispose() => _steam.Dispose();

    [Fact]
    public void References_are_read_and_other_blocks_skipped()
    {
        byte[] resource = GameFiles.Resource("particles/a.vpcf", "materials/Ёлка/b.vmat");

        Assert.Equal(["particles/a.vpcf", "materials/Ёлка/b.vmat"], ResourceReferences.Read(resource));
        Assert.Empty(ResourceReferences.Read(GameFiles.Resource()));
    }

    [Fact]
    public void Broken_resource_is_refused()
    {
        byte[] resource = GameFiles.Resource("particles/a.vpcf");

        Assert.Throws<InvalidDataException>(() => ResourceReferences.Read(resource.AsSpan(0, 10)));
        Assert.Throws<InvalidDataException>(() => ResourceReferences.Read(resource.AsSpan(0, resource.Length - 20)));

        byte[] noTerminator = resource[..^1];
        Assert.Throws<InvalidDataException>(() => ResourceReferences.Read(noTerminator));
    }

    [Fact]
    public void Game_effects_come_from_pak01()
    {
        GameEffects game = GameEffects.Read(Path.Combine(_steam.GameRoot, "game", "dota"));

        Assert.Equal(14, game.Graph.Particles.Count);
        Assert.Equal(GameFiles.Resource(), game.Blank);
        Assert.Equal(
            new HashSet<string> { GameFiles.LinaAttack, GameFiles.LinaTi7Attack },
            game.Graph.Projectiles.ToHashSet());
        Assert.Equal(new HashSet<string> { GameFiles.LinaTi7Ambient, GameFiles.LinaWeaponGlow }, game.Graph.ItemCreated.ToHashSet());
        Assert.Equal(GameFiles.Mist, game.Mist?.Path);
        Assert.True(game.Graph.Particles[GameFiles.LinaAttack].Draws);
        Assert.False(game.Graph.Particles["particles/error/null.vpcf"].Draws);
        Assert.Equal([GameFiles.LinaAttack], game.Graph.ParentsOf("particles/units/heroes/hero_lina/lina_base_attack_trail.vpcf"));
    }

    [Fact]
    public void Missing_archive_or_unreadable_particle_stops_the_build()
    {
        string dota = Path.Combine(_steam.GameRoot, "game", "dota");
        Dictionary<string, byte[]> files = GameFiles.Default();
        files["particles/world_creature_fx/moths.vpcf_c"] = [1, 2, 3];
        GameFiles.WritePak(_steam.Pak01, files);

        var broken = Assert.Throws<InvalidDataException>(() => GameEffects.Read(dota));
        Assert.Contains("moths.vpcf_c", broken.Message, StringComparison.Ordinal);

        File.WriteAllBytes(_steam.Pak01, Encoding.ASCII.GetBytes("not a vpk at all, just some bytes"));
        Assert.Throws<InvalidDataException>(() => GameEffects.Read(dota));

        File.Delete(_steam.Pak01);
        Assert.Throws<FileNotFoundException>(() => GameEffects.Read(dota));
    }

    [Fact]
    public void Unknown_group_name_in_settings_is_skipped_and_the_rest_stays()
    {
        string file = Path.Combine(_steam.DataRoot, "settings.json");
        _steam.Write(file, "{\n  \"language\": \"ru\",\n  \"effectGroups\": [\"Weather\", \"Fog\", \"3\", \"Sprays, Weather\", 5, \"Sprays\"]\n}\n");

        AppSettings settings = new SettingsStore(file, new FileLog(Path.Combine(_steam.DataRoot, "logs"))).Load();

        Assert.Equal("ru", settings.Language);
        Assert.Equal([EffectGroup.Weather, EffectGroup.Sprays], settings.EffectGroups);
    }

    [Fact]
    public void Refusal_is_known_before_the_pack_is_built()
    {
        Assert.Null(PackService.RefusalFor(_steam.Read()));

        _steam.SetLaunchOptions("-language english");
        Assert.Equal(InstallRefusal.NothingToDo, PackService.RefusalFor(_steam.Read()));

        _steam.Running.Add("dota2");
        Assert.Equal(InstallRefusal.DotaRunning, PackService.RefusalFor(_steam.Read()));
    }

    [Fact]
    public void Only_declared_projectiles_count_in_items_game_and_worn_effects_are_listed()
    {
        var declared = new HashSet<string>(StringComparer.Ordinal) { GameFiles.LinaAttack };
        byte[] itemsGame = Encoding.UTF8.GetBytes(GameFiles.ItemsGame);

        GameEffects.ItemParticles found = GameEffects.ItemsGameParticles(itemsGame, declared);

        Assert.Equal([GameFiles.LinaTi7Attack], found.Replacements);
        Assert.Equal([GameFiles.LinaTi7Ambient, GameFiles.LinaWeaponGlow], found.Created);
        Assert.Empty(GameEffects.ItemsGameParticles(itemsGame, new HashSet<string>(StringComparer.Ordinal)).Replacements);
    }

    [Fact]
    public void Worn_effect_that_also_replaces_a_game_particle_is_not_listed()
    {
        const string Tree = "particles/econ/items/tiny/tiny_dryad_tree/tiny_dryad_tree.vpcf";
        const string Glow = "particles/econ/items/tiny/tiny_prestige/tiny_prestige_glow.vpcf";
        string itemsGame = $$"""
            "1" { "visuals" { "asset_modifier" { "type" "particle_create" "modifier" "{{Tree}}" } } }
            "2"
            {
            	"asset_modifier"
            	{
            		"type"		"particle_create"
            		"modifier"		"{{Tree}}"
            	}
            	"asset_modifier1"
            	{
            		"type"		"particle_create"
            		"modifier"		"{{Glow}}"
            	}
            	"asset_modifier2"
            	{
            		"type"		"particle_create"
            		"modifier"		"{{Glow}}"
            	}
            	"asset_modifier3"
            	{
            		"type"		"particle"
            		"asset"		"particles/units/heroes/hero_tiny/tiny_scepter_empty.vpcf"
            		"modifier"		"{{Tree}}"
            	}
            }
            """;

        GameEffects.ItemParticles found = GameEffects.ItemsGameParticles(Encoding.UTF8.GetBytes(itemsGame), new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal([Glow], found.Created);
    }

    [Fact]
    public void Broken_or_missing_haze_material_leaves_the_haze_out_with_a_reason()
    {
        string dota = Path.Combine(_steam.GameRoot, "game", "dota");
        Dictionary<string, byte[]> files = GameFiles.Default();
        files[FogMist.Material] = [1, 2, 3];
        GameFiles.WritePak(_steam.Pak01, files);

        GameEffects broken = GameEffects.Read(dota);

        Assert.Null(broken.Mist);
        Assert.Contains("unreadable", broken.MistProblem, StringComparison.Ordinal);
        Assert.Equal(0, broken.FilesOf(EffectGroup.FogMist, []));

        files.Remove(FogMist.Material);
        GameFiles.WritePak(_steam.Pak01, files);
        Assert.Contains("not in the archive", GameEffects.Read(dota).MistProblem, StringComparison.Ordinal);

        files[FogMist.Material] = GameFiles.Resource("materials/default/default_tga_c23e38fb.vtex");
        GameFiles.WritePak(_steam.Pak01, files);
        GameEffects plain = GameEffects.Read(dota);
        Assert.Null(plain.Mist);
        Assert.Contains("no plain RGBA", plain.MistProblem, StringComparison.Ordinal);
    }

    [Fact]
    public void Worn_effects_of_cosmetics_go_and_default_items_and_shared_children_stay()
    {
        const string Ti7 = "particles/econ/items/lina/lina_ti7/";
        const string Courier = "particles/econ/courier/courier_greevil/courier_greevil_ambient.vpcf";
        const string Shared = Ti7 + "lina_ti7_flames.vpcf";
        ParticleGraph graph = new(
            new Dictionary<string, ParticleInfo>(StringComparer.Ordinal)
            {
                [Ti7 + "lina_ti7_ambient.vpcf"] = new([Shared], true),
                [Ti7 + "lina_ti7_hair.vpcf"] = new([], true),
                [Shared] = new([], true),
                [Ti7 + "lina_ti7_dragon_slave.vpcf"] = new([Ti7 + "lina_ti7_hair.vpcf"], true),
                [Courier] = new([], true),
                [GameFiles.LinaWeaponGlow] = new([], true),
                ["particles/econ/wards/portal/portal_ward_ambient.vpcf"] = new([], true),
            },
            [],
            [Ti7 + "lina_ti7_ambient.vpcf", Ti7 + "lina_ti7_hair.vpcf", Courier, GameFiles.LinaWeaponGlow, "particles/econ/wards/portal/portal_ward_ambient.vpcf", "particles/missing.vpcf"]);

        IReadOnlyList<string> worn = EffectRules.Select(graph, [EffectGroup.CosmeticAmbience])[EffectGroup.CosmeticAmbience];

        // The hair glow is also part of the item's spell, the default weapon glow is not a cosmetic folder.
        Assert.Equal([Courier, Ti7 + "lina_ti7_ambient.vpcf"], worn);
        Assert.DoesNotContain("particles/missing.vpcf", graph.ItemCreated);
    }

    [Fact]
    public void Fog_haze_texture_is_cleared_only_when_it_is_plain_rgba()
    {
        byte[] texture = GameFiles.Texture(8, 8);

        byte[]? cleared = FogMist.Cleared(texture);

        Assert.NotNull(cleared);
        Assert.Equal(texture.Length, cleared.Length);
        int pixels = texture.Length - (8 * 8 * 4);
        Assert.Equal(texture[..pixels], cleared[..pixels]);
        Assert.All(cleared[pixels..], b => Assert.Equal(0, b));
        Assert.All(texture[pixels..], b => Assert.Equal(0x80, b));

        Assert.Null(FogMist.Cleared(GameFiles.Texture(8, 8, format: 1)));
        Assert.Null(FogMist.Cleared(GameFiles.Texture(8, 8, mips: 4)));
        Assert.Null(FogMist.Cleared(GameFiles.Texture(8, 8, tail: 16)));
        Assert.Null(FogMist.Cleared(texture.AsSpan(0, texture.Length - 1)));
        Assert.Null(FogMist.Cleared(texture.AsSpan(0, 10)));
        Assert.Null(FogMist.Cleared(GameFiles.Resource("materials/a.vtex")));
    }

    [Fact]
    public void Fog_haze_is_found_through_the_post_process_material()
    {
        byte[] material = GameFiles.Resource("materials/default/default_tga_c23e38fb.vtex", "Materials/Models/Effects/Mist_00_color_psd_e168b78b.vtex");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [GameFiles.Mist] = GameFiles.Texture(8, 8) };

        PackedFile? mist = FogMist.Clear(material, files.GetValueOrDefault);

        Assert.Equal(GameFiles.Mist, mist?.Path);
        Assert.Equal(FogMist.Cleared(files[GameFiles.Mist]), mist?.Bytes);
        Assert.Null(FogMist.Clear(material, _ => null));
        Assert.Null(FogMist.Clear(GameFiles.Resource("materials/default/default_tga_c23e38fb.vtex"), files.GetValueOrDefault));
        Assert.Throws<InvalidDataException>(() => FogMist.Clear(material.AsSpan(0, 10), files.GetValueOrDefault));
    }

    [Fact]
    public void Each_group_is_picked_by_its_rules()
    {
        string[] particles =
        [
            "particles/rain_fx/econ_weather_snow.vpcf", "particles/rain_fx/rain_mist.vpcf", "particles/winter_fx/weather_frostivus_snow.vpcf",
            "particles/rain_fx/oracle_scepter_rain.vpcf", "particles/rain_fx/autumn_terrain.vpcf", "particles/rain_fx/desert_sand_drift.vpcf",
            "particles/world_environmental_fx/fire_torch.vpcf", "particles/world_creature_fx/butterflies.vpcf",
            "particles/terrain_fx/classical_fountain001_water.vpcf", "particles/dire_fx/dire_lava_gloops.vpcf",
            "maps/cavern_assets/particles/fireflies_radiant_cavern.vpcf", "maps/backgrounds/particles/bp21_meteor.vpcf",
            "particles/world_environmental_fx/rune_ambient.vpcf", "particles/world_environmental_fx/radiant_creep_glow.vpcf",
            "particles/econ/events/ti10/emblem/ti10_emblem_effect.vpcf", "particles/econ/events/anniversary_10th/anniversary_10th_hat_ambient_npc_dota_hero_lina.vpcf",
            "particles/econ/events/killbanners/screen_killbanner_rampage.vpcf", "particles/econ/events/ti10/teleport/ti10_teleport_emblem.vpcf",
            "particles/econ/events/ti9/ti9_emblem_tp_end.vpcf", "particles/econ/events/ti10/blink_dagger_start_ti10.vpcf",
            "particles/sprays/lina_angry_spray.vpcf", "particles/blood_impact/blood_advisor_pierce_spray.vpcf",
            "particles/econ/wards/portal/portal_ward_ambient.vpcf", "particles/items2_fx/ward.vpcf",
            "particles/radiant_fx/good_barracks_melee001_amb.vpcf", "particles/dire_fx/bad_ancient_ambdefl1.vpcf",
            "particles/addons_gameplay/fountain_tintable_splashes.vpcf", "particles/addons_gameplay/lamp_flame_new_ember.vpcf",
            "particles/radiant_fx/radiant_melee_barracks001_destruction.vpcf", "particles/base_static/experience_shrine_ambient.vpcf",
            "particles/base_static/team_portal_ambient_glow.vpcf", "particles/radiant_fx/good_barracks_gen_hitsparks.vpcf",
            "particles/addons_gameplay/player_deferred_light.vpcf", "particles/units/heroes/hero_x/x_ambient.vpcf",
        ];
        ParticleGraph graph = Graph([.. particles.Select(p => (p, Array.Empty<string>(), true))]);

        var picked = EffectRules.Select(graph, EffectRules.All);

        Assert.Equal(["particles/rain_fx/econ_weather_snow.vpcf", "particles/rain_fx/rain_mist.vpcf", "particles/winter_fx/weather_frostivus_snow.vpcf"], picked[EffectGroup.Weather]);
        Assert.Equal(
            [
                "maps/cavern_assets/particles/fireflies_radiant_cavern.vpcf", "particles/dire_fx/dire_lava_gloops.vpcf", "particles/rain_fx/autumn_terrain.vpcf",
                "particles/rain_fx/desert_sand_drift.vpcf", "particles/terrain_fx/classical_fountain001_water.vpcf", "particles/world_creature_fx/butterflies.vpcf",
                "particles/world_environmental_fx/fire_torch.vpcf",
            ],
            picked[EffectGroup.MapAmbience]);
        Assert.Equal(
            [
                "particles/econ/events/anniversary_10th/anniversary_10th_hat_ambient_npc_dota_hero_lina.vpcf",
                "particles/econ/events/killbanners/screen_killbanner_rampage.vpcf", "particles/econ/events/ti10/emblem/ti10_emblem_effect.vpcf",
            ],
            picked[EffectGroup.EventCosmetics]);
        Assert.Equal(
            [
                "particles/addons_gameplay/fountain_tintable_splashes.vpcf", "particles/addons_gameplay/lamp_flame_new_ember.vpcf",
                "particles/dire_fx/bad_ancient_ambdefl1.vpcf", "particles/radiant_fx/good_barracks_melee001_amb.vpcf",
            ],
            picked[EffectGroup.BuildingAmbience]);
        Assert.Equal(["particles/sprays/lina_angry_spray.vpcf"], picked[EffectGroup.Sprays]);
        Assert.Equal(["particles/econ/wards/portal/portal_ward_ambient.vpcf"], picked[EffectGroup.CosmeticWards]);
        Assert.Empty(picked[EffectGroup.AttackFlashes]);
        Assert.Empty(picked[EffectGroup.FogMist]);
        Assert.Empty(picked[EffectGroup.CosmeticAmbience]);
    }

    [Fact]
    public void Attack_loses_flashes_and_keeps_the_projectile_and_its_body()
    {
        const string X = "particles/units/heroes/hero_x/";
        ParticleGraph graph = Graph(
            [
                (X + "x_base_attack.vpcf", [X + "x_base_attack_trail.vpcf", X + "x_base_attack_explosion.vpcf", X + "x_base_attack_core_glow.vpcf", X + "z_base_attack.vpcf"], true),
                (X + "x_base_attack_trail.vpcf", [], true),
                (X + "x_base_attack_explosion.vpcf", [X + "x_base_attack_explosion_b.vpcf", X + "x_base_attack_explosion_core.vpcf"], true),
                (X + "x_base_attack_explosion_b.vpcf", [], true),
                (X + "x_base_attack_explosion_core.vpcf", [], true),
                (X + "x_base_attack_core_glow.vpcf", [], true),
                (X + "z_base_attack.vpcf", [X + "z_base_attack_trail.vpcf"], true),
                (X + "z_base_attack_trail.vpcf", [], true),
                (X + "y_base_attack.vpcf", [X + "y_base_attack_trail.vpcf"], false),
                (X + "y_base_attack_trail.vpcf", [], true),
            ],
            X + "x_base_attack.vpcf", X + "y_base_attack.vpcf", X + "z_base_attack.vpcf");

        IReadOnlyList<string> flashes = EffectRules.Select(graph, [EffectGroup.AttackFlashes])[EffectGroup.AttackFlashes];

        Assert.Equal(
            [X + "x_base_attack_explosion.vpcf", X + "x_base_attack_explosion_b.vpcf", X + "x_base_attack_trail.vpcf", X + "z_base_attack_trail.vpcf"],
            flashes);
    }

    [Fact]
    public void Flash_shared_with_an_ability_stays_with_everything_under_it()
    {
        const string X = "particles/units/heroes/hero_x/";
        ParticleGraph graph = Graph(
            [
                (X + "x_base_attack.vpcf", [X + "x_base_attack_launch.vpcf", X + "x_base_attack_trail.vpcf"], true),
                (X + "x_base_attack_launch.vpcf", [X + "x_base_attack_launch_b.vpcf"], true),
                (X + "x_base_attack_launch_b.vpcf", [], true),
                (X + "x_base_attack_trail.vpcf", [], true),
                (X + "x_ultimate.vpcf", [X + "x_base_attack_launch.vpcf"], true),
            ],
            X + "x_base_attack.vpcf");

        Assert.Equal([X + "x_base_attack_trail.vpcf"], EffectRules.Select(graph, [EffectGroup.AttackFlashes])[EffectGroup.AttackFlashes]);
    }

    [Fact]
    public void Projectile_an_ability_also_starts_keeps_its_flashes()
    {
        const string X = "particles/units/heroes/hero_x/";
        ParticleGraph graph = Graph(
            [
                (X + "x_base_attack.vpcf", [X + "x_base_attack_trail.vpcf"], true),
                (X + "x_base_attack_trail.vpcf", [], true),
                (X + "x_multishot.vpcf", [X + "x_base_attack.vpcf"], true),
            ],
            X + "x_base_attack.vpcf");

        Assert.Empty(EffectRules.Select(graph, [EffectGroup.AttackFlashes])[EffectGroup.AttackFlashes]);
    }

    [Fact]
    public void Effect_started_by_a_kept_one_stays_unless_that_one_goes_too()
    {
        const string Emblem = "particles/econ/events/ti10/emblem/ti10_emblem_weather.vpcf";
        const string Snow = "particles/rain_fx/econ_weather_x.vpcf";
        const string Butterflies = "particles/world_creature_fx/butterflies.vpcf";
        ParticleGraph graph = Graph(
            [
                (Emblem, [Snow], true),
                (Snow, [], true),
                (Butterflies, [], true),
                ("particles/units/heroes/hero_x/x_spell.vpcf", [Butterflies], true),
            ]);

        Assert.Empty(EffectRules.Select(graph, [EffectGroup.Weather])[EffectGroup.Weather]);
        Assert.Equal([Snow], EffectRules.Select(graph, [EffectGroup.Weather, EffectGroup.EventCosmetics])[EffectGroup.Weather]);
        Assert.Empty(EffectRules.Select(graph, [EffectGroup.MapAmbience])[EffectGroup.MapAmbience]);
        Assert.Empty(EffectRules.Select(graph, []));
    }

    [Fact]
    public void Pack_carries_the_blank_under_every_chosen_effect()
    {
        using var folder = new CyrillicTempFolder();
        AppServices services = AppServices.Create(new AppPaths(Path.Combine(folder.Path, "Иван"), Path.Combine(folder.Path, "work")), _steam.Read);

        BuiltPack pack = services.BuildPack(_steam.Read());

        Assert.Equal(2, pack.Effects[EffectGroup.Weather]);
        Assert.Equal(1, pack.Effects[EffectGroup.MapAmbience]);
        Assert.Equal(3, pack.Effects[EffectGroup.AttackFlashes]);
        Assert.Equal(1, pack.Effects[EffectGroup.BuildingAmbience]);
        Assert.Equal(1, pack.Effects[EffectGroup.CosmeticAmbience]);
        Assert.Equal(1, pack.Effects[EffectGroup.FogMist]);
        using (Package package = Open(pack.Bytes, folder.Path))
        {
            foreach (string path in new[]
            {
                "particles/rain_fx/econ_weather_snow_b.vpcf_c", "particles/econ/items/lina/lina_ti7/lina_ti7_base_attack_trail.vpcf_c",
                GameFiles.LinaTi7Ambient + "_c", "particles/radiant_fx/good_barracks_melee001_amb.vpcf_c",
            })
            {
                package.ReadEntry(package.FindEntry(path)!, out byte[] data, validateCrc: true);
                Assert.Equal(GameFiles.Resource(), data);
            }

            package.ReadEntry(package.FindEntry(GameFiles.Mist)!, out byte[] mist, validateCrc: true);
            Assert.Equal(FogMist.Cleared(GameFiles.Texture(8, 8)), mist);
            Assert.Null(package.FindEntry(GameFiles.LinaAttack + "_c"));
            Assert.Null(package.FindEntry(GameFiles.LinaWeaponGlow + "_c"));
            package.ReadEntry(package.FindEntry(LanguageFolders.MarkerPath)!, out byte[] json, validateCrc: true);
            using JsonDocument marker = JsonDocument.Parse(json);
            Assert.Equal(2, marker.RootElement.GetProperty("effects").GetProperty("Weather").GetInt32());
        }

        services.Settings.Save(new AppSettings { EffectGroups = [] });
        Assert.Contains("\"effectGroups\": []", File.ReadAllText(services.Paths.SettingsFile), StringComparison.Ordinal);
        BuiltPack empty = services.BuildPack(_steam.Read());
        Assert.Empty(empty.Effects);
        using (Package package = Open(empty.Bytes, folder.Path))
        {
            Assert.Single(package.Entries!.SelectMany(e => e.Value));
        }

        services.Settings.Save(new AppSettings { EffectGroups = [EffectGroup.Sprays, EffectGroup.Weather] });
        Assert.Contains("\"Sprays\"", File.ReadAllText(services.Paths.SettingsFile), StringComparison.Ordinal);
        Assert.Equal([EffectGroup.Sprays, EffectGroup.Weather], services.Settings.Load().EffectGroups);
    }

    [Fact]
    public void Install_state_remembers_what_the_pack_replaces()
    {
        var log = new FileLog(Path.Combine(_steam.DataRoot, "logs"));
        var store = new InstallStateStore(Path.Combine(_steam.DataRoot, "install.json"), log);
        var service = new PackService(store, log);
        var effects = new Dictionary<EffectGroup, int> { [EffectGroup.Weather] = 121, [EffectGroup.AttackFlashes] = 568 };
        byte[] pak = PackBuilder.Build(new PackMarker(PackBuilder.AppName, "0.1.0", "6967", DateTimeOffset.UnixEpoch, effects), new Dictionary<string, byte[]>());

        service.Install(_steam.Read, pak, effects);

        Assert.Equal(effects.OrderBy(e => e.Key), store.Load()!.Effects!.OrderBy(e => e.Key));
        service.Remove(_steam.Read());
    }

    private static ParticleGraph Graph((string Path, string[] Children, bool Draws)[] particles, params string[] projectiles) =>
        new(particles.ToDictionary(p => p.Path, p => new ParticleInfo(p.Children, p.Draws), StringComparer.Ordinal), projectiles);

    private static Package Open(byte[] pak, string folder)
    {
        string file = Path.Combine(folder, $"{Guid.NewGuid():N}.vpk");
        File.WriteAllBytes(file, pak);
        var package = new Package();
        package.Read(file);
        return package;
    }
}
