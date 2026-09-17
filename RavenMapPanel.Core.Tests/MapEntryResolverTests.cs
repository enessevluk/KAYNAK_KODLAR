using Xunit;

namespace RavenMapPanel;

public sealed class MapEntryResolverTests
{
    private static MapEntry Entry(string rawName, string category = "", string name = "") =>
        new(category, name, rawName, "saved_map_monument_prefab", 10, 0, 20, 2);

    [Theory]
    [InlineData("assets/bundled/prefabs/autospawn/monument/roadside/radtown_1.prefab", "radtown")]
    [InlineData("assets/bundled/prefabs/autospawn/monument/medium/radtown_small_3.prefab", "sewer_branch")]
    [InlineData("assets/bundled/prefabs/autospawn/monument/medium/sewer_branch_1.prefab", "sewer_branch")]
    [InlineData("assets/bundled/prefabs/autospawn/monument/roadside/gas_station_1.prefab", "gas_station")]
    [InlineData("assets/bundled/prefabs/autospawn/monument/roadside/supermarket_1.prefab", "supermarket")]
    [InlineData("assets/bundled/prefabs/autospawn/monument/swamp/swamp_c.prefab", "abandoned_cabins")]
    public void ResolveCategory_PrefersExactPrefabName(string rawName, string expected)
    {
        var resolver = new MapEntryResolver(AssetStore.LoadCatalog());

        Assert.Equal(expected, resolver.ResolveCategory(Entry(rawName)));
    }

    [Fact]
    public void ResolveCategory_RepairsStaleSwampCategoryForAbandonedCabins()
    {
        var resolver = new MapEntryResolver(AssetStore.LoadCatalog());

        var category = resolver.ResolveCategory(Entry(
            "assets/bundled/prefabs/autospawn/monument/swamp/swamp_c.prefab",
            "swamps",
            "Swamp"));

        Assert.Equal("abandoned_cabins", category);
    }

    [Theory]
    [InlineData("assets/bundled/prefabs/autospawn/monument/cave/cave_large_hard.prefab", "caves", "cave_large_hard")]
    [InlineData("assets/bundled/prefabs/autospawn/power substations/big/power_sub_big_2.prefab", "power_substations", "power_sub_big_2")]
    public void ResolveCategory_RepairsLegacyAggregateCategories(string rawName, string legacyCategory, string expected)
    {
        var resolver = new MapEntryResolver(AssetStore.LoadCatalog());

        Assert.Equal(expected, resolver.ResolveCategory(Entry(rawName, legacyCategory, legacyCategory)));
    }

    [Fact]
    public void Merge_DeduplicatesSewerBranchReportedByBothReporters()
    {
        var resolver = new MapEntryResolver(AssetStore.LoadCatalog());
        const string prefab = "assets/bundled/prefabs/autospawn/monument/medium/radtown_small_3.prefab";
        var main = new[] { Entry(prefab, name: "Sewer Branch") };
        var world = new[] { Entry(prefab, "sewer_branch", "Sewer Branch") };

        var resolved = resolver.Merge(main, world);

        var sewer = Assert.Single(resolved);
        Assert.Equal("sewer_branch", sewer.Category);
    }

    [Fact]
    public void ConcreteMonuments_UseTheirActualPrefabIconNames()
    {
        var catalog = AssetStore.LoadCatalog().ToDictionary(x => x.Id);
        var expected = new Dictionary<string, string>
        {
            ["launch_site"] = "launch_site_1.png",
            ["military_tunnels"] = "military_tunnel_1.png",
            ["airfield"] = "airfield_1.png",
            ["trainyard"] = "trainyard_1.png",
            ["powerplant"] = "powerplant_1.png",
            ["water_treatment"] = "water_treatment_plant_1.png",
            ["excavator"] = "excavator_1.png",
            ["junkyard"] = "junkyard_1.png",
            ["nuclear_silo"] = "nuclear_missile_silo.png",
            ["dome"] = "sphere_tank.png",
            ["apartment_complex"] = "apartments_complex.png",
            ["radtown"] = "radtown_1.png",
            ["supermarket"] = "supermarket_1.png",
            ["gas_station"] = "gas_station_1.png",
            ["sewer_branch"] = "radtown_small_3.png",
            ["abandoned_cabins"] = "swamp_c.png",
            ["outpost"] = "compound.png",
            ["bandit_camp"] = "bandit_town.png",
            ["stables_a"] = "stables_a.png",
            ["stables_b"] = "stables_b.png",
            ["fishing_village_a"] = "fishing_village_a.png",
            ["fishing_village_b"] = "fishing_village_b.png",
            ["fishing_village_c"] = "fishing_village_c.png",
            ["oilrig_large"] = "oilrig_1.png",
            ["oilrig_small"] = "oilrig_2.png",
            ["stone_quarry"] = "mining_quarry_b.png",
            ["sulfur_quarry"] = "mining_quarry_a.png",
            ["hqm_quarry"] = "mining_quarry_c.png",
            ["power_sub_big_1"] = "power_sub_big_1.png",
            ["power_sub_big_2"] = "power_sub_big_2.png",
            ["power_sub_small_1"] = "power_sub_small_1.png",
            ["power_sub_small_2"] = "power_sub_small_2.png",
            ["cave_small_easy"] = "cave_small_easy.png",
            ["cave_medium_easy"] = "cave_medium_easy.png",
            ["cave_small_medium"] = "cave_small_medium.png",
            ["cave_medium_medium"] = "cave_medium_medium.png",
            ["cave_large_medium"] = "cave_large_medium.png",
            ["cave_small_hard"] = "cave_small_hard.png",
            ["cave_medium_hard"] = "cave_medium_hard.png",
            ["cave_large_hard"] = "cave_large_hard.png",
            ["cave_large_sewers_hard"] = "cave_large_sewers_hard.png"
        };

        foreach (var pair in expected)
            Assert.Equal(pair.Value, catalog[pair.Key].Icon);
    }

    public static TheoryData<string, string> AuthoritativePrefabCases => new()
    {
        { "assets/bundled/prefabs/autospawn/monument/large/launch_site_1.prefab", "launch_site" },
        { "assets/bundled/prefabs/autospawn/monument/large/military_tunnel_1.prefab", "military_tunnels" },
        { "assets/bundled/prefabs/autospawn/monument/large/airfield_1.prefab", "airfield" },
        { "assets/bundled/prefabs/autospawn/monument/large/trainyard_1.prefab", "trainyard" },
        { "assets/bundled/prefabs/autospawn/monument/large/powerplant_1.prefab", "powerplant" },
        { "assets/bundled/prefabs/autospawn/monument/large/water_treatment_plant_1.prefab", "water_treatment" },
        { "assets/bundled/prefabs/autospawn/monument/large/excavator_1.prefab", "excavator" },
        { "assets/bundled/prefabs/autospawn/monument/medium/junkyard_1.prefab", "junkyard" },
        { "assets/bundled/prefabs/autospawn/monument/medium/nuclear_missile_silo.prefab", "nuclear_silo" },
        { "assets/bundled/prefabs/autospawn/monument/arctic_bases/arctic_research_base_a.prefab", "arctic_research_base" },
        { "assets/bundled/prefabs/autospawn/monument/military_bases/desert_military_base_b.prefab", "desert_military_base" },
        { "assets/bundled/prefabs/autospawn/monument/harbor/ferry_terminal_1.prefab", "ferry_terminal" },
        { "assets/bundled/prefabs/autospawn/monument/small/satellite_dish.prefab", "satellite_dish" },
        { "assets/bundled/prefabs/autospawn/monument/small/sphere_tank.prefab", "dome" },
        { "assets/bundled/prefabs/autospawn/monument/medium/apartments_complex_1.prefab", "apartment_complex" },
        { "assets/bundled/prefabs/autospawn/monument/jungle_ruins/jungle_ziggurat_a.prefab", "ziggurat" },
        { "assets/bundled/prefabs/autospawn/monument/roadside/radtown_1.prefab", "radtown" },
        { "assets/bundled/prefabs/autospawn/monument/roadside/supermarket_1.prefab", "supermarket" },
        { "assets/bundled/prefabs/autospawn/monument/roadside/gas_station_1.prefab", "gas_station" },
        { "assets/bundled/prefabs/autospawn/monument/medium/radtown_small_3.prefab", "sewer_branch" },
        { "assets/bundled/prefabs/autospawn/monument/roadside/warehouse.prefab", "warehouse" },
        { "assets/bundled/prefabs/autospawn/monument/medium/compound.prefab", "outpost" },
        { "assets/bundled/prefabs/autospawn/monument/medium/bandit_town.prefab", "bandit_camp" },
        { "assets/bundled/prefabs/autospawn/monument/small/stables_a.prefab", "stables_a" },
        { "assets/bundled/prefabs/autospawn/monument/small/stables_b.prefab", "stables_b" },
        { "assets/bundled/prefabs/autospawn/monument/harbor/harbor_1.prefab", "harbor" },
        { "assets/bundled/prefabs/autospawn/monument/fishing_village/fishing_village_a.prefab", "fishing_village_a" },
        { "assets/bundled/prefabs/autospawn/monument/fishing_village/fishing_village_b.prefab", "fishing_village_b" },
        { "assets/bundled/prefabs/autospawn/monument/fishing_village/fishing_village_c.prefab", "fishing_village_c" },
        { "assets/bundled/prefabs/autospawn/monument/lighthouse/lighthouse.prefab", "lighthouse" },
        { "assets/bundled/prefabs/autospawn/monument/offshore/oilrig_1.prefab", "oilrig_large" },
        { "assets/bundled/prefabs/autospawn/monument/offshore/oilrig_2.prefab", "oilrig_small" },
        { "assets/bundled/prefabs/autospawn/monument/underwater_lab/underwater_lab_a.prefab", "underwater_labs" },
        { "assets/bundled/prefabs/autospawn/monument/cave/cave_small_easy.prefab", "cave_small_easy" },
        { "assets/bundled/prefabs/autospawn/monument/cave/cave_medium_easy.prefab", "cave_medium_easy" },
        { "assets/bundled/prefabs/autospawn/monument/cave/cave_small_medium.prefab", "cave_small_medium" },
        { "assets/bundled/prefabs/autospawn/monument/cave/cave_medium_medium.prefab", "cave_medium_medium" },
        { "assets/bundled/prefabs/autospawn/monument/cave/cave_large_medium.prefab", "cave_large_medium" },
        { "assets/bundled/prefabs/autospawn/monument/cave/cave_small_hard.prefab", "cave_small_hard" },
        { "assets/bundled/prefabs/autospawn/monument/cave/cave_medium_hard.prefab", "cave_medium_hard" },
        { "assets/bundled/prefabs/autospawn/monument/cave/cave_large_hard.prefab", "cave_large_hard" },
        { "assets/bundled/prefabs/autospawn/monument/cave/cave_large_sewers_hard.prefab", "cave_large_sewers_hard" },
        { "assets/bundled/prefabs/autospawn/power substations/big/power_sub_big_1.prefab", "power_sub_big_1" },
        { "assets/bundled/prefabs/autospawn/power substations/big/power_sub_big_2.prefab", "power_sub_big_2" },
        { "assets/bundled/prefabs/autospawn/power substations/small/power_sub_small_1.prefab", "power_sub_small_1" },
        { "assets/bundled/prefabs/autospawn/power substations/small/power_sub_small_2.prefab", "power_sub_small_2" },
        { "assets/bundled/prefabs/autospawn/monument/tiny/water_well_e.prefab", "water_wells" },
        { "assets/bundled/prefabs/autospawn/monument/small/mining_quarry_b.prefab", "stone_quarry" },
        { "assets/bundled/prefabs/autospawn/monument/small/mining_quarry_a.prefab", "sulfur_quarry" },
        { "assets/bundled/prefabs/autospawn/monument/small/mining_quarry_c.prefab", "hqm_quarry" },
        { "assets/bundled/prefabs/autospawn/monument/jungle_ruins/jungle_ruins_e.prefab", "ruins" },
        { "assets/bundled/prefabs/autospawn/decor/iceberg/iceberg_5.prefab", "icebergs" },
        { "assets/bundled/prefabs/autospawn/monument/swamp/swamp_a.prefab", "swamps" },
        { "assets/bundled/prefabs/autospawn/monument/swamp/swamp_b.prefab", "swamps" },
        { "assets/bundled/prefabs/autospawn/monument/swamp/swamp_c.prefab", "abandoned_cabins" },
        { "assets/bundled/prefabs/autospawn/unique_environment/jungle/ue_jungle_swamp_a.prefab", "swamps" },
        { "assets/bundled/prefabs/autospawn/unique_environment/lake/ue_lake_a.prefab", "lakes" },
        { "assets/bundled/prefabs/autospawn/unique_environment/canyon/ue_canyon_a.prefab", "canyons" },
        { "assets/bundled/prefabs/autospawn/unique_environment/oasis/ue_oasis_a.prefab", "oases" },
        { "assets/bundled/prefabs/autospawn/decor/v3_rock_formations_large/rock_formation_a.prefab", "god_rocks" },
        { "assets/bundled/prefabs/autospawn/powerlines/powerline_d.prefab", "powerlines" }
    };

    [Theory]
    [MemberData(nameof(AuthoritativePrefabCases))]
    public void ResolveCategory_MapsEachKnownPrefabToItsOwnCatalogEntry(string rawName, string expected)
    {
        var resolver = new MapEntryResolver(AssetStore.LoadCatalog());

        Assert.Equal(expected, resolver.ResolveCategory(Entry(rawName)));
    }

    [Fact]
    public void Catalog_AllConfiguredIconsExistInTheClientPackage()
    {
        foreach (var rule in AssetStore.LoadCatalog())
        {
            using var icon = AssetStore.Open("icons." + rule.Icon);
            Assert.True(icon.Length > 0, $"{rule.Id}: {rule.Icon}");
        }
    }
}
