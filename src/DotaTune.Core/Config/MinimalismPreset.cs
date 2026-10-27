namespace DotaTune.Core.Config;

/// <summary>
/// The minimalism autoexec.cfg the owner plays with, as lines of the DotaTune block
/// (docs\minimalism-razbor.md, build 6967). Of its 73 lines the ten the game no longer has,
/// the eight that set the game's own value and map_enable_portrait_worlds, which a plain config
/// cannot run, are left out, and so are its minimap colours: the owner sets those on the colours
/// page apart from the preset.
/// </summary>
public static class MinimalismPreset
{
    public static IReadOnlyList<ConfigLine> Lines { get; } =
    [
        new("fps_max", "0"),
        new("dota_selection_groups", "false"),
        new("violence_ablood", "false"),
        new("violence_agibs", "false"),
        new("violence_hblood", "false"),
        new("violence_hgibs", "false"),
        new("dota_portrait_animate", "0"),
        new("r_deferred_additive_pass", "0"),
        new("r_deferred_simple_light", "0"),
        new("r_ssao", "0"),
        new("r_dota_normal_maps", "0"),
        new("dota_ambient_creatures", "0"),
        new("dota_ambient_cloth", "0"),
        new("r_grass_quality", "0"),
        new("r_dota_fxaa", "1"),
        new("r_deferred_specular", "0"),
        new("r_deferred_specular_bloom", "0"),
        new("dota_cheap_water", "1"),
        new("r_deferred_height_fog", "0"),
        new("r_dashboard_render_quality", "0"),
        new("r_dota_allow_wind_on_trees", "0"),
        new("r_dota_bloom_compute_shader", "1"),
        new("r_texturefilteringquality", "0"),
        new("cl_particle_fallback_base", "4"),
        new("cl_globallight_shadow_mode", "0"),
        new("panorama_allow_transitions", "0"),
        new("cl_particle_fallback_multiplier", "4"),
        new("cl_particle_sim_fallback_threshold_ms", "2.5"),
        new("cl_aggregate_particles", "1"),
        new("cl_retire_low_priority_lights", "1"),
        new("dota_allow_clientside_particles", "0"),
        new("dota_disable_particle_lights", "1"),
        new("lb_shadow_texture_height_override", "128"),
        new("lb_shadow_texture_width_override", "128"),
        new("lb_dynamic_shadow_resolution_base", "128"),
        new("lb_dynamic_shadow_resolution_delay", "3"),
        new("r_dota_spotlight_shadows_resolution", "128"),
        new("r_character_decal_resolution", "256"),
        new("r_aoproxy_cull_dist", "4"),
        new("r_dota_shadow_ambient_light", "0"),
        new("r_light_flickering_enabled", "0"),
        new("r_particle_max_detail_level", "0"),
        new("r_particle_max_texture_layers", "2"),
        new("r_particle_cables_cast_shadows", "0"),
        new("r_dota_allow_desaturate_layers", "0"),
        new("r_dota_color_correction", "0"),
        new("r_dota_render_2d_skybox", "0"),
        new("r_depth_of_field", "0"),
        new("sc_clutter_density_full_size", "0.04"),
        new("sc_shadow_depth_bias", "64"),
        new("con_enable", "1"),
        new("dota_hero_auto_graball", "1"),
    ];
}
