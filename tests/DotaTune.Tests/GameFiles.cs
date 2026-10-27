using System.Buffers.Binary;
using System.Text;
using DotaTune.Core.Effects;
using ValvePak;

namespace DotaTune.Tests;

/// <summary>
/// Game archive content for tests. A compiled particle here is a real Source 2 resource
/// header with two blocks: a RED2 stand-in the reader has to skip and the references block.
/// </summary>
internal static class GameFiles
{
    public const string Blank = "particles/error/null.vpcf_c";
    public const string LinaAttack = "particles/units/heroes/hero_lina/lina_base_attack.vpcf";
    public const string LinaTi7Attack = "particles/econ/items/lina/lina_ti7/lina_ti7_base_attack.vpcf";
    public const string LinaTi7Ambient = "particles/econ/items/lina/lina_ti7/lina_ti7_ambient.vpcf";
    public const string LinaWeaponGlow = "particles/units/heroes/hero_lina/lina_weapon_glow.vpcf";
    public const string Mist = "materials/models/effects/mist_00_color_psd_e168b78b.vtex_c";

    public static readonly string ItemsGame = """
        "items_game"
        {
        	"items"
        	{
        		"7385"
        		{
        			"visuals"
        			{
        				"asset_modifier"
        				{
        					"type"		"particle"
        					"asset"		"particles/units/heroes/hero_lina/lina_base_attack.vpcf"
        					"modifier"		"particles/econ/items/lina/lina_ti7/lina_ti7_base_attack.vpcf"
        				}
        				"asset_modifier1"
        				{
        					"type"		"particle"
        					"asset"		"particles/units/heroes/hero_lina/lina_dragon_slave.vpcf"
        					"modifier"		"particles/econ/items/lina/lina_ti7/lina_ti7_dragon_slave.vpcf"
        				}
        				"asset_modifier2"
        				{
        					"type"		"model"
        					"asset"		"particles/units/heroes/hero_lina/lina_base_attack.vpcf"
        					"modifier"		"models/heroes/lina/lina_ti7.vmdl"
        				}
        				"asset_modifier3"
        				{
        					"type"		"particle_create"
        					"modifier"		"particles/econ/items/lina/lina_ti7/lina_ti7_ambient.vpcf"
        				}
        			}
        		}
        		"7386"
        		{
        			"visuals"
        			{
        				"asset_modifier"
        				{
        					"type"		"particle_create"
        					"modifier"		"particles/units/heroes/hero_lina/lina_weapon_glow.vpcf"
        				}
        			}
        		}
        	}
        }
        """.ReplaceLineEndings("\r\n");

    /// <summary>A compiled resource that references the given files and holds nothing else.</summary>
    public static byte[] Resource(params string[] references)
    {
        const int Table = 16;
        const int Red2 = Table + (2 * 12);
        const int Rerl = Red2 + 4;
        const int List = Rerl + 8;
        byte[][] names = [.. references.Select(r => Encoding.UTF8.GetBytes(r + "\0"))];
        int namesAt = List + (references.Length * 16);
        var data = new byte[namesAt + names.Sum(n => n.Length)];
        Span<byte> span = data;

        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 12);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], Table - 8);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], 2);
        Block(span, Table, "RED2", Red2, 4);
        "kv3!"u8.CopyTo(span[Red2..]);
        Block(span, Table + 12, "RERL", Rerl, data.Length - Rerl);
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

        return data;
    }

    /// <summary>
    /// A compiled texture: the header, one DATA block and the pixels right after it, filled with
    /// 0x80. Format 4 is RGBA8888; <paramref name="tail"/> adds bytes the size check must notice.
    /// </summary>
    public static byte[] Texture(int width, int height, byte format = 4, byte mips = 1, int tail = 0)
    {
        const int Table = 16;
        const int Data = Table + 12;
        const int DataSize = 40;
        const int BlocksEnd = Data + DataSize;
        var bytes = new byte[BlocksEnd + (width * height * 4) + tail];
        Span<byte> span = bytes;

        BinaryPrimitives.WriteUInt32LittleEndian(span, BlocksEnd);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 12);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], Table - 8);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], 1);
        Block(span, Table, "DATA", Data, DataSize);
        BinaryPrimitives.WriteUInt16LittleEndian(span[Data..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(Data + 20)..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(Data + 22)..], (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(Data + 24)..], 1);
        span[Data + 26] = format;
        span[Data + 27] = mips;
        span[BlocksEnd..].Fill(0x80);
        return bytes;
    }

    /// <summary>A short text in the manner of the game's script of the "match found" window, CRLF and no line end at the close.</summary>
    public const string AcceptScriptText = "$.Schedule( 0.0, function ()\r\n{\r\n\tif ( !$.GetContextPanel().BHasClass( 'BattleCup' ) )\r\n\t\treturn;\r\n} );";

    /// <summary>A compiled panorama script: RED2 first and the text in DATA at the end, the way the game keeps them.</summary>
    public static byte[] Script(string text)
    {
        const int Table = 16;
        const int Red2 = Table + (2 * 12);
        const int Data = Red2 + 4;
        byte[] body = Encoding.UTF8.GetBytes(text);
        var bytes = new byte[Data + body.Length];
        Span<byte> span = bytes;

        BinaryPrimitives.WriteUInt32LittleEndian(span, (uint)bytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], 12);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], Table - 8);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], 2);
        Block(span, Table, "RED2", Red2, 4);
        "kv3!"u8.CopyTo(span[Red2..]);
        Block(span, Table + 12, "DATA", Data, body.Length);
        body.CopyTo(span[Data..]);
        return bytes;
    }

    /// <summary>
    /// What FakeSteam puts into pak01: a little of several groups, Lina's attack and a cosmetic for
    /// it, a worn effect of that cosmetic, a barracks glow and the fog haze texture.
    /// </summary>
    public static Dictionary<string, byte[]> Default()
    {
        const string Lina = "particles/units/heroes/hero_lina/";
        const string Ti7 = "particles/econ/items/lina/lina_ti7/";
        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [Blank] = Resource(),
            [LinaTi7Ambient + "_c"] = Resource(Ti7 + "lina_ti7_ambient_glow.vpcf", "materials/particle/fire_ti7.vmat"),
            [Ti7 + "lina_ti7_ambient_glow.vpcf_c"] = Resource("materials/particle/fire_ti7.vmat"),
            [LinaWeaponGlow + "_c"] = Resource("materials/particle/fire.vmat"),
            ["particles/radiant_fx/good_barracks_melee001_amb.vpcf_c"] = Resource("materials/particle/glow.vmat"),
            ["particles/radiant_fx/radiant_melee_barracks001_destruction.vpcf_c"] = Resource("materials/particle/glow.vmat"),
            [FogMist.Material] = Resource("materials/models/effects/mist_00_color_psd_e168b78b.vtex", "materials/default/default_tga_c23e38fb.vtex"),
            [Mist] = Texture(8, 8),
            ["particles/rain_fx/econ_weather_snow.vpcf_c"] = Resource("particles/rain_fx/econ_weather_snow_b.vpcf", "materials/particle/snow.vmat"),
            ["particles/rain_fx/econ_weather_snow_b.vpcf_c"] = Resource("materials/particle/snow.vmat"),
            ["particles/world_creature_fx/butterflies.vpcf_c"] = Resource("materials/particle/butterfly.vmat"),
            [LinaAttack + "_c"] = Resource(Lina + "lina_base_attack_trail.vpcf", Lina + "lina_base_attack_explosion.vpcf", "materials/particle/fire.vmat"),
            [Lina + "lina_base_attack_trail.vpcf_c"] = Resource("materials/particle/fire.vmat"),
            [Lina + "lina_base_attack_explosion.vpcf_c"] = Resource("materials/particle/fire.vmat"),
            [LinaTi7Attack + "_c"] = Resource(Ti7 + "lina_ti7_base_attack_trail.vpcf", "materials/particle/fire_ti7.vmat"),
            [Ti7 + "lina_ti7_base_attack_trail.vpcf_c"] = Resource("materials/particle/fire_ti7.vmat"),
            ["scripts/npc/heroes/npc_dota_hero_lina.txt"] = Encoding.UTF8.GetBytes(
                $"\"DOTAHeroes\"\n{{\n\t\"npc_dota_hero_lina\"\n\t{{\n\t\t\"ProjectileModel\"\t\t\"{LinaAttack}\"\n\t}}\n}}\n"),
            ["scripts/npc/npc_units.txt"] = Encoding.UTF8.GetBytes(
                "\"DOTAUnits\"\n{\n\t\"npc_dota_creep_goodguys_ranged\"\n\t{\n\t\t\"ProjectileModel\"\t\t\"particles/base_attacks/ranged_goodguy.vpcf\"\n\t}\n}\n"),
            ["scripts/items/items_game.txt"] = Encoding.UTF8.GetBytes(ItemsGame),
        };
    }

    public static void WritePak(string file, IReadOnlyDictionary<string, byte[]> files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var package = new Package();
        foreach ((string path, byte[] data) in files)
        {
            package.AddFile(path, data);
        }

        package.Write(file);
    }

    private static void Block(Span<byte> span, int entry, string type, int start, int size)
    {
        Encoding.ASCII.GetBytes(type).CopyTo(span[entry..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(entry + 4)..], (uint)(start - (entry + 4)));
        BinaryPrimitives.WriteUInt32LittleEndian(span[(entry + 8)..], (uint)size);
    }
}
