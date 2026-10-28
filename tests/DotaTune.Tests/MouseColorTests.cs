using System.Buffers.Binary;
using System.Text;
using DotaTune.Core.Config;
using DotaTune.Core.Diagnostics;
using DotaTune.Core.Effects;
using DotaTune.Core.Settings;

namespace DotaTune.Tests;

public sealed class MouseColorTests
{
    private const string Folder = "particles/ui_mouseactions/";

    [Fact]
    public void Kv3_written_and_read_back_keeps_every_value_and_encoding()
    {
        Kv3Value root = Obj(
            ("_class", Str("CParticleSystemDefinition")),
            ("m_nInt", Kv3Value.Scalar(Kv3Type.Int32, Kv3Flag.None, integer: -5)),
            ("m_nBig", Kv3Value.Scalar(Kv3Type.Int64, Kv3Flag.None, integer: 1L << 40)),
            ("m_nZero", Kv3Value.Scalar(Kv3Type.Int64Zero, Kv3Flag.None)),
            ("m_nByte", Kv3Value.Scalar(Kv3Type.UInt8, Kv3Flag.None, integer: 200)),
            ("m_nShort", Kv3Value.Scalar(Kv3Type.Int16, Kv3Flag.None, integer: -300)),
            ("m_flFloat", Kv3Value.Scalar(Kv3Type.Float, Kv3Flag.None, real: 0.25)),
            ("m_flDouble", Kv3Value.Scalar(Kv3Type.Double, Kv3Flag.None, real: 0.501961)),
            ("m_bOn", Kv3Value.Scalar(Kv3Type.True, Kv3Flag.None)),
            ("m_hTexture", Kv3Value.String(Kv3Flag.Resource, "materials/ёлка.vtex")),
            ("m_Empty", Str("")),
            ("m_Color", Ints(Kv3Type.ShortTypedArray, Kv3Type.Int32, 48, 208, 249)),
            ("m_Long", Ints(Kv3Type.TypedArray, Kv3Type.UInt8, 1, 2, 3, 4)),
            ("m_List", Kv3Value.Array(Kv3Type.Array, Kv3Flag.None, [Str("a"), Kv3Value.Scalar(Kv3Type.Null, Kv3Flag.None), Obj(("x", Str("y")))])));
        var document = new Kv3Document(Guid.Parse("9bada39c-a931-42d0-abdd-e5c1b13d37a6"), root);

        byte[] written = Kv3Binary.Write(document);
        Kv3Document read = Kv3Binary.Read(written);

        Assert.Equal(document.Format, read.Format);
        Assert.Equal(Describe(root), Describe(read.Root));
        Assert.Equal(written, Kv3Binary.Write(read));
        Assert.Equal(0x4B563304u, BinaryPrimitives.ReadUInt32LittleEndian(written));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(written.AsSpan(20)));
    }

    [Fact]
    public void Kv3_version_5_with_an_auxiliary_array_and_lz4_is_read()
    {
        foreach (bool lz4 in new[] { false, true })
        {
            Kv3Document read = Kv3Binary.Read(Version5(lz4));

            Assert.Equal([0.5, 2.0], read.Root["a"]!.Numbers!);
            Assert.Equal(Kv3Type.AuxiliaryTypedArray, read.Root["a"]!.Type);
            Assert.Equal("x", read.Root["s"]!.Text);
            Assert.Equal(7.0, read.Root["n"]!.Number);

            // Written back as version 4: the auxiliary array becomes an ordinary short one.
            Kv3Document again = Kv3Binary.Read(Kv3Binary.Write(read));
            Assert.Equal(Kv3Type.ShortTypedArray, again.Root["a"]!.Type);
            Assert.Equal([0.5, 2.0], again.Root["a"]!.Numbers!);
            Assert.Equal("x", again.Root["s"]!.Text);
        }
    }

    [Fact]
    public void Damaged_kv3_is_refused()
    {
        byte[] good = Kv3Binary.Write(new Kv3Document(Guid.Empty, Obj(("a", Str("b")))));

        Assert.Throws<InvalidDataException>(() => Kv3Binary.Read(good.AsSpan(0, 10)));
        Assert.Throws<InvalidDataException>(() => Kv3Binary.Read(good.AsSpan(0, good.Length - 6)));
        byte[] badMagic = [.. good];
        badMagic[0] = 0x07;
        Assert.Throws<InvalidDataException>(() => Kv3Binary.Read(badMagic));
        byte[] zstd = [.. good];
        zstd[20] = 2;
        Assert.Throws<InvalidDataException>(() => Kv3Binary.Read(zstd));
        byte[] hugeCount = Kv3Binary.Write(new Kv3Document(Guid.Empty, Obj(("a", Kv3Value.Array(Kv3Type.Array, Kv3Flag.None, [Str("b")])))));
        // The array length follows the string count, the object length and the member name in the four-byte lane.
        int lane = 72 + Align(BinaryPrimitives.ReadInt32LittleEndian(hugeCount.AsSpan(28)), 4);
        byte[] badName = [.. hugeCount];
        BinaryPrimitives.WriteInt32LittleEndian(hugeCount.AsSpan(lane + 12), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => Kv3Binary.Read(hugeCount));
        BinaryPrimitives.WriteInt32LittleEndian(badName.AsSpan(lane + 8), 99);
        Assert.Throws<InvalidDataException>(() => Kv3Binary.Read(badName));
    }

    [Fact]
    public void Lz4_copies_overlapping_matches_and_refuses_damage()
    {
        var output = new byte[8];
        Lz4.Decode([0x22, (byte)'a', (byte)'b', 0x02, 0x00], output);
        Assert.Equal("abababab", Encoding.ASCII.GetString(output));

        Assert.Throws<InvalidDataException>(() => Lz4.Decode([0x22, (byte)'a', (byte)'b', 0x00, 0x00], new byte[8]));
        Assert.Throws<InvalidDataException>(() => Lz4.Decode([0x22, (byte)'a', (byte)'b', 0x03, 0x00], new byte[8]));
        Assert.Throws<InvalidDataException>(() => Lz4.Decode([0x22, (byte)'a', (byte)'b', 0x02, 0x00], new byte[9]));
        Assert.Throws<InvalidDataException>(() => Lz4.Decode([0xF0], new byte[20]));
    }

    [Fact]
    public void Numbers_keep_their_type_where_they_fit_and_widen_where_not()
    {
        Kv3Value bytes = Ints(Kv3Type.ShortTypedArray, Kv3Type.UInt8, 76, 193, 82, 255);
        bytes.SetNumbers([193, 0, 0, 255]);
        Assert.Equal(Kv3Type.UInt8, bytes.ElementType);
        bytes.SetNumbers([300, 0, 0, 255]);
        Assert.Equal(Kv3Type.Int64, bytes.ElementType);
        Assert.Equal([300.0, 0, 0, 255], bytes.Numbers!);

        Kv3Value zeros = Ints(Kv3Type.ShortTypedArray, Kv3Type.Int64Zero, 0, 0, 0);
        zeros.SetNumbers([0.5, 0, 1]);
        Assert.Equal(Kv3Type.Double, zeros.ElementType);
        Assert.Equal([0.5, 0, 1], Kv3Binary.Read(Kv3Binary.Write(new Kv3Document(Guid.Empty, Obj(("v", zeros))))).Root["v"]!.Numbers!);

        Kv3Value one = Kv3Value.Scalar(Kv3Type.Int64One, Kv3Flag.None, integer: 1);
        one.SetNumber(6);
        Assert.Equal((Kv3Type.Int64, 6.0), (one.Type, one.Number));
        Assert.Throws<InvalidOperationException>(() => Str("a").SetNumber(1));
    }

    [Fact]
    public void Data_block_is_replaced_and_the_rest_of_the_resource_kept()
    {
        byte[] resource = Particle(Obj(("a", Str("b"))), Folder + "child.vpcf");
        byte[] data = Kv3Binary.Write(new Kv3Document(Guid.Empty, Obj(("a", Str("a much longer text than before")))));

        byte[] changed = CompiledResource.WithData(resource, data);

        Assert.Equal((uint)changed.Length, BinaryPrimitives.ReadUInt32LittleEndian(changed));
        Assert.Equal(data, CompiledResource.Data(changed).ToArray());
        Assert.Equal([Folder + "child.vpcf"], ResourceReferences.Read(changed));
        Assert.Equal("a much longer text than before", Kv3Binary.Read(CompiledResource.Data(changed)).Root["a"]!.Text);

        Assert.Throws<InvalidDataException>(() => CompiledResource.Data(resource.AsSpan(0, resource.Length - 1)));
        Assert.Throws<InvalidDataException>(() => CompiledResource.Data(GameFiles.Resource("x.vpcf")));
    }

    [Fact]
    public void Chosen_colour_goes_into_remaps_and_literal_colours_by_brightness()
    {
        Dictionary<string, byte[]> game = MouseParticles();
        var choice = new MouseColorChoice { MoveClick = new Rgb(255, 0, 0), AttackClick = new Rgb(0, 255, 0) };

        MouseRecolor recolor = MouseEffects.Recolor(path => game.GetValueOrDefault(path), choice);

        // The roots hold only children, so they stay; the shadow both markers share cannot be red and green at once.
        Assert.Equal(
            new[] { Folder + "clicked_occlusion_basemove_arrows.vpcf_c", Folder + "clicked_occlusion_attack_rings.vpcf_c" }.Order(),
            recolor.Files.Select(f => f.Path).Order());
        Assert.Equal(1, recolor.Counts[MouseEffect.MoveClick]);
        Assert.Equal(1, recolor.Counts[MouseEffect.AttackClick]);
        Assert.Equal([Folder + "clicked_basemove.vpcf: not in the game"], recolor.Problems);

        Kv3Value arrows = Root(recolor, "clicked_occlusion_basemove_arrows");
        Kv3Value remap = arrows["m_Initializers"]!.Items![0];
        Assert.Equal([1.0, 0, 0], remap["m_vOutputMin"]!.Numbers!);
        Assert.Equal([1.0, 0, 0], remap["m_vOutputMax"]!.Numbers!);
        Assert.Equal(Kv3Type.ShortTypedArray, remap["m_vOutputMin"]!.Type);
        Kv3Value renderer = arrows["m_Renderers"]!.Items![0];
        Assert.Equal([249.0, 0, 0], renderer["m_vecColorScale"]!["m_LiteralColor"]!.Numbers!);
        Assert.Equal([0.0, 0, 0], renderer["m_OffsetMin"]!["m_LiteralColor"]!.Numbers!);
        Assert.Equal([255.0, 255, 255], renderer["m_vecTexturesInput"]!.Items![0]["m_Gradient"]!["m_Stops"]!.Items![0]["m_Color"]!.Numbers!);
        Assert.Equal([193.0, 0, 0, 255], arrows["m_ConstantColor"]!.Numbers!);
        // A remap into another field keeps its output.
        Assert.Null(arrows["m_Initializers"]!.Items![1]["m_vOutputMin"]);

        Assert.Equal([0.0, 1, 0], Root(recolor, "clicked_occlusion_attack_rings")["m_Operators"]!.Items![0]["m_vOutputMin"]!.Numbers!);
    }

    [Fact]
    public void Shared_particle_changes_only_when_every_effect_using_it_has_the_same_colour()
    {
        Dictionary<string, byte[]> game = MouseParticles();
        string shadow = Folder + "clicked_occlusion_basemove_shadow.vpcf_c";

        // The attack marker is left as in the game, so its shadow is too, though the move marker shares it.
        MouseRecolor moveOnly = MouseEffects.Recolor(path => game.GetValueOrDefault(path), new MouseColorChoice { MoveClick = new Rgb(255, 0, 0) });
        Assert.DoesNotContain(moveOnly.Files, f => f.Path == shadow);
        Assert.Equal(1, moveOnly.Counts[MouseEffect.MoveClick]);
        Assert.False(moveOnly.Counts.ContainsKey(MouseEffect.AttackClick));

        var same = new MouseColorChoice { MoveClick = new Rgb(0, 0, 255), AttackClick = new Rgb(0, 0, 255) };
        MouseRecolor both = MouseEffects.Recolor(path => game.GetValueOrDefault(path), same);
        Assert.Contains(both.Files, f => f.Path == shadow);
        Assert.Equal((2, 2), (both.Counts[MouseEffect.MoveClick], both.Counts[MouseEffect.AttackClick]));
        Kv3Value remap = Root(both, "clicked_occlusion_basemove_shadow")["m_Initializers"]!.Items![0];
        Assert.Equal([0.0, 0, 1], remap["m_vOutputMin"]!.Numbers!);
        Assert.Equal([0.0, 0, 0], Root(both, "clicked_occlusion_basemove_shadow")["m_ConstantColor"]!.Numbers![..3]);
    }

    [Fact]
    public void Particle_changed_in_an_unforeseen_way_stays_and_the_rest_goes_on()
    {
        Dictionary<string, byte[]> game = MouseParticles();
        Kv3Value Remap(string set, params double[] max) => Obj(
            ("_class", Str("C_OP_RemapCPtoVector")),
            ("m_nFieldOutput", Kv3Value.Scalar(Kv3Type.Int32, Kv3Flag.None, integer: 6)),
            ("m_nSetMethod", Str(set)),
            ("m_vOutputMax", Kv3Value.Doubles(max)));
        // A four-number output and a remap that scales the colour are left alone; a damaged file is reported.
        game[Folder + "clicked_occlusion_attack_rings.vpcf_c"] = Particle(Obj(("m_Operators", Kv3Value.Array(Kv3Type.Array, Kv3Flag.None,
            [Remap("PARTICLE_SET_REPLACE_VALUE", 1, 1, 1, 1), Remap("PARTICLE_SET_SCALE_INITIAL_VALUE", 1, 1, 1)]))));
        byte[] damaged = [.. game[Folder + "clicked_occlusion_basemove_arrows.vpcf_c"]];
        damaged[^5] ^= 0xFF;
        game[Folder + "clicked_occlusion_basemove_arrows.vpcf_c"] = damaged;

        MouseRecolor recolor = MouseEffects.Recolor(
            path => game.GetValueOrDefault(path), new MouseColorChoice { MoveClick = new Rgb(255, 0, 0), AttackClick = new Rgb(255, 0, 0) });

        Assert.Equal([Folder + "clicked_occlusion_basemove_shadow.vpcf_c"], recolor.Files.Select(f => f.Path));
        Assert.Contains(recolor.Problems, p => p.StartsWith(Folder + "clicked_occlusion_basemove_arrows.vpcf: ", StringComparison.Ordinal));
        Assert.Equal((1, 1), (recolor.Counts[MouseEffect.MoveClick], recolor.Counts[MouseEffect.AttackClick]));
    }

    [Fact]
    public void Tower_range_takes_the_colour_in_place_of_the_one_the_game_passes_in_a_point()
    {
        // Without a scale the point counts in full.
        Kv3Value Input(double? scale = null)
        {
            var members = new List<(string, Kv3Value)>
            {
                ("m_nType", Str("PVEC_TYPE_CP_VALUE")),
                ("m_LiteralColor", Ints(Kv3Type.ShortTypedArray, Kv3Type.Int32, 0, 0, 0)),
                ("m_nControlPoint", Kv3Value.Scalar(Kv3Type.Int32, Kv3Flag.None, integer: 4)),
            };
            if (scale is { } each)
            {
                members.Add(("m_vCPValueScale", Kv3Value.Doubles(each, each, each)));
            }

            return Obj([.. members]);
        }

        Kv3Value ground = Obj(
            ("m_ConstantColor", Ints(Kv3Type.TypedArray, Kv3Type.Int32, 0, 0, 0, 255)),
            ("m_Operators", Kv3Value.Array(Kv3Type.Array, Kv3Flag.None,
            [
                Obj(("_class", Str("C_OP_SetVec")), ("m_InputValue", Input())),
                Obj(("_class", Str("C_OP_SetVec")), ("m_InputValue", Input()), ("m_nOutputField", Kv3Value.Scalar(Kv3Type.Int32, Kv3Flag.None, integer: 0))),
                Obj(("_class", Str("C_INIT_InitVec")), ("m_InputValue", Input())),
                Obj(("_class", Str("C_OP_SetVec")), ("m_InputValue", Input(0.5))),
            ])));
        var game = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [Folder + "tower_range_indicator_default.vpcf_c"] = Particle(Obj(), Folder + "tower_range_indicator_default_ground.vpcf"),
            [Folder + "tower_range_indicator_default_ground.vpcf_c"] = Particle(ground),
        };

        // A dark colour: the literal the input becomes is not painted again by its brightness.
        MouseRecolor recolor = MouseEffects.Recolor(path => game.GetValueOrDefault(path), new MouseColorChoice { TowerRange = new Rgb(128, 0, 64) });

        Assert.Equal([Folder + "tower_range_indicator_default_ground.vpcf_c"], recolor.Files.Select(f => f.Path));
        Assert.Equal(1, recolor.Counts[MouseEffect.TowerRange]);
        Assert.Equal(
            [Folder + "tower_range_indicator_alt.vpcf: not in the game", Folder + "range_finder_tower_experimental_pulse.vpcf: not in the game"],
            recolor.Problems);
        List<Kv3Value> operators = Root(recolor, "tower_range_indicator_default_ground")["m_Operators"]!.Items!;
        Assert.Equal("PVEC_TYPE_LITERAL_COLOR", operators[0]["m_InputValue"]!["m_nType"]!.Text);
        Assert.Equal([128.0, 0, 64], operators[0]["m_InputValue"]!["m_LiteralColor"]!.Numbers!);
        // A SetVec into another field and an initializer naming no field keep the point.
        Assert.Equal("PVEC_TYPE_CP_VALUE", operators[1]["m_InputValue"]!["m_nType"]!.Text);
        Assert.Equal("PVEC_TYPE_CP_VALUE", operators[2]["m_InputValue"]!["m_nType"]!.Text);
        Assert.Equal([0.0, 0, 0], operators[2]["m_InputValue"]!["m_LiteralColor"]!.Numbers!);
        // A layer that takes the point at half keeps half of the colour.
        Assert.Equal("PVEC_TYPE_LITERAL_COLOR", operators[3]["m_InputValue"]!["m_nType"]!.Text);
        Assert.Equal([64.0, 0, 32], operators[3]["m_InputValue"]!["m_LiteralColor"]!.Numbers!);
    }

    [Fact]
    public void Nothing_chosen_gives_no_files_and_the_choice_round_trips_through_settings()
    {
        Dictionary<string, byte[]> game = MouseParticles();
        Assert.Empty(MouseEffects.Recolor(path => game.GetValueOrDefault(path), new MouseColorChoice()).Files);

        using var folder = new CyrillicTempFolder();
        string file = Path.Combine(folder.Path, "settings.json");
        var store = new SettingsStore(file, new FileLog(Path.Combine(folder.Path, "logs")));
        var choice = new MouseColorChoice { MoveClick = new Rgb(160, 96, 255), HeroHighlight = new Rgb(255, 255, 0), TowerRange = new Rgb(33, 161, 255) };
        store.Save(new AppSettings { MouseColors = choice });

        Assert.Equal(choice, store.Load().MouseColors);
        Assert.Contains("\"moveClick\": \"#A060FF\"", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Contains("\"towerRange\": \"#21A1FF\"", File.ReadAllText(file), StringComparison.Ordinal);

        // A colour that is not one and a name of a later version are dropped, the rest stays.
        File.WriteAllText(file, "{ \"mouseColors\": { \"moveClick\": \"#zz\", \"unitClick\": \"#00FFFF\", \"cursorTrail\": \"#FFFFFF\" } }");
        Assert.Equal(new MouseColorChoice { UnitClick = new Rgb(0, 255, 255) }, store.Load().MouseColors);
    }

    /// <summary>
    /// Two markers sharing a shadow, the way build 6967 has them: the move marker's arrows hold a
    /// remap of control point 1 into the colour, a remap into the alpha, a literal colour and things
    /// that must stay (an offset's unused colour, a texture gradient).
    /// </summary>
    internal static Dictionary<string, byte[]> MouseParticles()
    {
        Kv3Value arrows = Obj(
            ("m_ConstantColor", Ints(Kv3Type.TypedArray, Kv3Type.Int32, 76, 193, 82, 255)),
            ("m_Initializers", Kv3Value.Array(Kv3Type.Array, Kv3Flag.None,
            [
                Obj(("_class", Str("C_INIT_RemapTransformToVector")), ("m_vOutputMax", Kv3Value.Doubles(1, 1, 1)), ("m_nFieldOutput", Kv3Value.Scalar(Kv3Type.Int32, Kv3Flag.None, integer: 6))),
                Obj(("_class", Str("C_INIT_RemapCPtoVector")), ("m_vOutputMax", Kv3Value.Doubles(1, 1, 1)), ("m_nFieldOutput", Kv3Value.Scalar(Kv3Type.Int32, Kv3Flag.None, integer: 7))),
            ])),
            ("m_Renderers", Kv3Value.Array(Kv3Type.Array, Kv3Flag.None,
            [
                Obj(
                    ("_class", Str("C_OP_RenderTrails")),
                    ("m_vecColorScale", Obj(("m_nType", Str("PVEC_TYPE_LITERAL_COLOR")), ("m_LiteralColor", Ints(Kv3Type.ShortTypedArray, Kv3Type.Int32, 48, 208, 249)))),
                    ("m_OffsetMin", Obj(("m_nType", Str("PVEC_TYPE_LITERAL")), ("m_LiteralColor", Ints(Kv3Type.ShortTypedArray, Kv3Type.Int32, 0, 0, 0)))),
                    ("m_vecTexturesInput", Kv3Value.Array(Kv3Type.Array, Kv3Flag.None,
                        [Obj(("m_Gradient", Obj(("m_Stops", Kv3Value.Array(Kv3Type.Array, Kv3Flag.None, [Obj(("m_Color", Ints(Kv3Type.ShortTypedArray, Kv3Type.Int32, 255, 255, 255)))])))))]))),
            ])));
        Kv3Value shadow = Obj(("m_ConstantColor", Ints(Kv3Type.TypedArray, Kv3Type.Int32, 0, 0, 0, 255)),
            ("m_Initializers", Kv3Value.Array(Kv3Type.Array, Kv3Flag.None,
                [Obj(("_class", Str("C_INIT_RemapTransformToVector")), ("m_nFieldOutput", Kv3Value.Scalar(Kv3Type.Int64, Kv3Flag.None, integer: 6)))])));
        Kv3Value rings = Obj(("m_Operators", Kv3Value.Array(Kv3Type.Array, Kv3Flag.None,
            [Obj(("_class", Str("C_OP_RemapCPtoVector")), ("m_nFieldOutput", Kv3Value.Scalar(Kv3Type.Int32, Kv3Flag.None, integer: 6)), ("m_vOutputMax", Kv3Value.Doubles(1, 1, 1)))])));
        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [Folder + "clicked_occlusion_basemove.vpcf_c"] = Particle(Obj(), Folder + "clicked_occlusion_basemove_shadow.vpcf", Folder + "clicked_occlusion_basemove_arrows.vpcf", "materials/particle/arrow.vmat"),
            [Folder + "clicked_occlusion_basemove_arrows.vpcf_c"] = Particle(arrows),
            [Folder + "clicked_occlusion_basemove_shadow.vpcf_c"] = Particle(shadow),
            [Folder + "clicked_moveto.vpcf_c"] = Particle(Obj(), Folder + "clicked_occlusion_basemove_arrows.vpcf"),
            [Folder + "clicked_occlusion_basemove_attack.vpcf_c"] = Particle(Obj(), Folder + "clicked_occlusion_basemove_shadow.vpcf", Folder + "clicked_occlusion_attack_rings.vpcf"),
            [Folder + "clicked_occlusion_attack_rings.vpcf_c"] = Particle(rings),
        };
    }

    private static Kv3Value Root(MouseRecolor recolor, string name) =>
        Kv3Binary.Read(CompiledResource.Data(recolor.Files.Single(f => f.Path == Folder + name + ".vpcf_c").Bytes)).Root;

    /// <summary>A compiled particle: the header, a references block and the DATA block last.</summary>
    internal static byte[] Particle(Kv3Value root, params string[] references)
    {
        const int Table = 16;
        const int Rerl = Table + (2 * 12);
        const int List = Rerl + 8;
        byte[][] names = [.. references.Select(r => Encoding.UTF8.GetBytes(r + "\0"))];
        int namesAt = List + (references.Length * 16);
        int dataAt = Align(namesAt + names.Sum(n => n.Length), 16);
        byte[] data = Kv3Binary.Write(new Kv3Document(Guid.Empty, root));
        var bytes = new byte[dataAt + data.Length];
        Span<byte> span = bytes;

        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)bytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 12);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], Table - 8);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], 2);
        Block(span, Table, "RERL", Rerl, dataAt - Rerl);
        Block(span, Table + 12, "DATA", dataAt, data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(span[Rerl..], List - Rerl);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(Rerl + 4)..], (uint)references.Length);
        int at = namesAt;
        for (int i = 0; i < names.Length; i++)
        {
            int entry = List + (i * 16);
            BinaryPrimitives.WriteUInt64LittleEndian(span[entry..], (ulong)(i + 1));
            BinaryPrimitives.WriteInt64LittleEndian(span[(entry + 8)..], at - (entry + 8));
            names[i].CopyTo(span[at..]);
            at += names[i].Length;
        }

        data.CopyTo(span[dataAt..]);
        return bytes;
    }

    /// <summary>
    /// A version 5 block by hand: { a = [0.5, 2.0] in the auxiliary lanes, s = "x", n = 7 }, both
    /// buffers stored as they are or as LZ4 blocks of literals.
    /// </summary>
    private static byte[] Version5(bool lz4)
    {
        using var first = new MemoryStream();
        first.Write("a\0s\0x\0n\0"u8);
        first.Write(BitConverter.GetBytes(4));
        first.Write(new byte[4]);
        first.Write(BitConverter.GetBytes(0.5));
        first.Write(BitConverter.GetBytes(2.0));
        using var second = new MemoryStream();
        second.Write(BitConverter.GetBytes(3));
        second.WriteByte(2);
        second.Write(new byte[3]);
        foreach (int value in new[] { 0, 1, 2, 3, 7 })
        {
            second.Write(BitConverter.GetBytes(value));
        }

        second.Write([9, 25, 5, 6, 11]);
        second.Write(BitConverter.GetBytes(0xFFEEDD00));
        byte[] buffer1 = first.ToArray(), buffer2 = second.ToArray();
        byte[] stored1 = lz4 ? Literals(buffer1) : buffer1, stored2 = lz4 ? Literals(buffer2) : buffer2;

        using var block = new MemoryStream();
        int[] head = [8, 1, 2, 5];
        block.Write(BitConverter.GetBytes(0x4B563305));
        block.Write(new byte[16]);
        block.Write(BitConverter.GetBytes(lz4 ? 1 : 0));
        block.Write(BitConverter.GetBytes((ushort)0));
        block.Write(BitConverter.GetBytes((ushort)(lz4 ? 0x4000 : 0)));
        foreach (int value in head)
        {
            block.Write(BitConverter.GetBytes(value));
        }

        block.Write(BitConverter.GetBytes((ushort)1));
        block.Write(BitConverter.GetBytes((ushort)1));
        foreach (int value in new[]
        {
            buffer1.Length + buffer2.Length, stored1.Length + stored2.Length, 0, 0, 0, 0,
            buffer1.Length, lz4 ? stored1.Length : 0, buffer2.Length, lz4 ? stored2.Length : 0, 1, 0, 5, 0, 4, 1, 1, 2,
        })
        {
            block.Write(BitConverter.GetBytes(value));
        }

        block.Write(stored1);
        block.Write(stored2);
        return block.ToArray();
    }

    // An LZ4 block that holds the data as one run of literals.
    private static byte[] Literals(byte[] data)
    {
        using var block = new MemoryStream();
        block.WriteByte((byte)(Math.Min(data.Length, 15) << 4));
        for (int rest = data.Length - 15; rest >= 0; rest -= 255)
        {
            block.WriteByte((byte)Math.Min(rest, 255));
            if (rest < 255)
            {
                break;
            }
        }

        block.Write(data);
        return block.ToArray();
    }

    private static string Describe(Kv3Value value) => value.Members is { } members
        ? $"{{{string.Join(",", members.Select(m => $"{m.Name}={Describe(m.Value)}"))}}}"
        : value.Items is { } items
            ? $"{value.Type}/{value.ElementType}[{string.Join(",", items.Select(Describe))}]"
            : $"{value.Type}/{value.Flag}/{value.Text}/{value.Number.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}";

    private static Kv3Value Obj(params (string Name, Kv3Value Value)[] members) =>
        Kv3Value.Object(Kv3Flag.None, [.. members.Select(m => new Kv3Member(m.Name, m.Value))]);

    private static Kv3Value Str(string text) => Kv3Value.String(Kv3Flag.None, text);

    private static Kv3Value Ints(Kv3Type arrayType, Kv3Type elementType, params long[] values) =>
        Kv3Value.Array(arrayType, Kv3Flag.None, [.. values.Select(v => Kv3Value.Scalar(elementType, Kv3Flag.None, integer: v))], elementType);

    private static int Align(int offset, int alignment) => (offset + alignment - 1) / alignment * alignment;

    private static void Block(Span<byte> span, int entry, string type, int start, int size)
    {
        Encoding.ASCII.GetBytes(type).CopyTo(span[entry..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(entry + 4)..], (uint)(start - (entry + 4)));
        BinaryPrimitives.WriteUInt32LittleEndian(span[(entry + 8)..], (uint)size);
    }
}
