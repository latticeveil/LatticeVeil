using System;
using System.Collections.Generic;
using System.Linq;

namespace LatticeVeilMonoGame.Core;

public enum BlockRenderLayer
{
    Opaque = 0,
    Cutout = 1,
    Blended = 2,
    Water = 3
}

public enum BlockConnectedTextureMode
{
    None = 0,
    Seamless = 1
}

public sealed class BlockDef
{
    public BlockId Id { get; }
    public string Name { get; }
    public bool IsSolid { get; }
    public bool IsTransparent { get; }
    public BlockRenderLayer RenderLayer { get; }
    public float Hardness { get; }
    public int AtlasIndex { get; }
    public string? TextureName { get; }
    public bool IsVisibleInInventory { get; }
    public bool HasCustomModel { get; }
    public BlockConnectedTextureMode ConnectedTextureMode { get; }
    
    // Light metadata
    public float LightLevel { get; set; } = 0f;
    public LightMode LightMode { get; set; } = LightMode.None;

    public BlockDef(
        BlockId id,
        string name,
        bool solid,
        bool transparent,
        float hardness,
        int atlasIndex,
        string? textureName = null,
        bool visible = true,
        bool customModel = false,
        BlockRenderLayer? renderLayer = null,
        BlockConnectedTextureMode connectedTextureMode = BlockConnectedTextureMode.None,
        float lightLevel = 0f,
        LightMode lightMode = LightMode.None)
    {
        Id = id;
        Name = name;
        IsSolid = solid;
        IsTransparent = transparent;
        RenderLayer = renderLayer ?? (id == BlockId.Water ? BlockRenderLayer.Water : transparent ? BlockRenderLayer.Blended : BlockRenderLayer.Opaque);
        Hardness = hardness;
        AtlasIndex = atlasIndex;
        TextureName = textureName;
        IsVisibleInInventory = visible;
        HasCustomModel = customModel;
        ConnectedTextureMode = connectedTextureMode;
        LightLevel = lightLevel;
        LightMode = lightMode;
    }
}

public static class BlockRegistry
{
    private static readonly BlockDef?[] Defs = new BlockDef?[256];
    private static readonly List<BlockDef> AllDefs = new();
    private static readonly Dictionary<string, BlockId> TokenLookup = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<BlockId, string[]> CommandTokens = new();
    private static readonly HashSet<BlockId> HiddenFromCatalogForNow = new()
    {
        BlockId.Runestone,
        BlockId.Veinstone,
        BlockId.Veilglass,
        BlockId.Gravestone,
        BlockId.InscribedTablet,
        BlockId.FragmentScrap,
        BlockId.VeilSealStone,
        BlockId.EchoBloom,
        BlockId.GraveSilt,
        BlockId.RunestoneDust,
        BlockId.VeinstoneCrystal,
        BlockId.AnchoringSalt,
        BlockId.StabilizedAsh,
        BlockId.EchoIconShard,
        BlockId.CopperWiring,
        BlockId.LimiterSigil,
        BlockId.AlignmentMatrixFragment,
        BlockId.AttunedKeystoneFragment,
        BlockId.RegulatorComponent,
        BlockId.TransitRegulatorPart,
        BlockId.WayfinderPlinthPart,
        BlockId.ResonanceCore,
        BlockId.AxiomFulgrite,
        BlockId.AnchoringAlloy,
        BlockId.CleanPhial,
        BlockId.Swiftleaf,
        BlockId.Driftcap,
        BlockId.Embermoss,
        BlockId.SigilLoom,
        BlockId.QuietAlembic,
        BlockId.RunicAnvil,
        BlockId.CinderbranchStaff,
        BlockId.StormreedStaff,
        BlockId.WayboundFrame,
        BlockId.AttunedKeystone,
        BlockId.TimedLimiter,
        BlockId.TransitRegulator,
        BlockId.WaygatePlinth,
        BlockId.WaygateRune,
        BlockId.EvergateCore,
        BlockId.EvergateCoreUnstable,
        BlockId.FleetstepDraught,
        BlockId.SkyboundPhilter,
        BlockId.PyroskinTonic,
        BlockId.BrineveilElixir,
        BlockId.WaterBucket,
        BlockId.EmptyBucket,
        BlockId.PlantFiber,
        BlockId.CoalChunk,
        BlockId.FlowingWater1,
        BlockId.FlowingWater2,
        BlockId.FlowingWater3,
        BlockId.FlowingWater4,
        BlockId.FlowingWater5,
        BlockId.FlowingWater6,
        BlockId.FlowingWater7
    };

    static BlockRegistry()
    {
        Register(new BlockDef(BlockId.Air, "Air", solid: false, transparent: true, hardness: 0f, atlasIndex: 0));
        Register(new BlockDef(BlockId.Grass, "Grass", solid: true, transparent: false, hardness: 0.6f, atlasIndex: 1));
        Register(new BlockDef(BlockId.Dirt, "Dirt", solid: true, transparent: false, hardness: 0.5f, atlasIndex: 2));
        Register(new BlockDef(BlockId.Stone, "Stone", solid: true, transparent: false, hardness: 1.5f, atlasIndex: 3));
        Register(new BlockDef(BlockId.Water, "Water", solid: false, transparent: true, hardness: 0f, atlasIndex: 4, visible: false, renderLayer: BlockRenderLayer.Water, connectedTextureMode: BlockConnectedTextureMode.Seamless));
        Register(new BlockDef(BlockId.FlowingWater1, "Flowing Water 1", solid: false, transparent: true, hardness: 0f, atlasIndex: 68, textureName: "flowing_water", visible: false, renderLayer: BlockRenderLayer.Water, connectedTextureMode: BlockConnectedTextureMode.Seamless));
        Register(new BlockDef(BlockId.FlowingWater2, "Flowing Water 2", solid: false, transparent: true, hardness: 0f, atlasIndex: 68, textureName: "flowing_water", visible: false, renderLayer: BlockRenderLayer.Water, connectedTextureMode: BlockConnectedTextureMode.Seamless));
        Register(new BlockDef(BlockId.FlowingWater3, "Flowing Water 3", solid: false, transparent: true, hardness: 0f, atlasIndex: 68, textureName: "flowing_water", visible: false, renderLayer: BlockRenderLayer.Water, connectedTextureMode: BlockConnectedTextureMode.Seamless));
        Register(new BlockDef(BlockId.FlowingWater4, "Flowing Water 4", solid: false, transparent: true, hardness: 0f, atlasIndex: 68, textureName: "flowing_water", visible: false, renderLayer: BlockRenderLayer.Water, connectedTextureMode: BlockConnectedTextureMode.Seamless));
        Register(new BlockDef(BlockId.FlowingWater5, "Flowing Water 5", solid: false, transparent: true, hardness: 0f, atlasIndex: 68, textureName: "flowing_water", visible: false, renderLayer: BlockRenderLayer.Water, connectedTextureMode: BlockConnectedTextureMode.Seamless));
        Register(new BlockDef(BlockId.FlowingWater6, "Flowing Water 6", solid: false, transparent: true, hardness: 0f, atlasIndex: 68, textureName: "flowing_water", visible: false, renderLayer: BlockRenderLayer.Water, connectedTextureMode: BlockConnectedTextureMode.Seamless));
        Register(new BlockDef(BlockId.FlowingWater7, "Flowing Water 7", solid: false, transparent: true, hardness: 0f, atlasIndex: 68, textureName: "flowing_water", visible: false, renderLayer: BlockRenderLayer.Water, connectedTextureMode: BlockConnectedTextureMode.Seamless));
        Register(new BlockDef(BlockId.Sand, "Sand", solid: true, transparent: false, hardness: 0.4f, atlasIndex: 5));
        Register(new BlockDef(BlockId.OakLog, "Oak Log", solid: true, transparent: false, hardness: 1.0f, atlasIndex: 6, textureName: "oak_log"));
        Register(new BlockDef(BlockId.Leaves, "Leaves", solid: true, transparent: true, hardness: 0.2f, atlasIndex: 7, renderLayer: BlockRenderLayer.Cutout));
        Register(new BlockDef(BlockId.Chest, "Chest", solid: true, transparent: true, hardness: 1.0f, atlasIndex: 10));
        Register(new BlockDef(BlockId.CoalOre, "Coal Ore", solid: true, transparent: false, hardness: 2.0f, atlasIndex: 11, textureName: "coal"));
        Register(new BlockDef(BlockId.IronOre, "Iron Ore", solid: true, transparent: false, hardness: 3.0f, atlasIndex: 12, textureName: "iron"));
        Register(new BlockDef(BlockId.ArtificerBench, "Artificer Bench", solid: true, transparent: false, hardness: 1.0f, atlasIndex: 13));
        Register(new BlockDef(
            BlockId.Glass,
            "Glass",
            solid: true,
            transparent: true,
            hardness: 0.3f,
            atlasIndex: 14,
            renderLayer: BlockRenderLayer.Blended,
            connectedTextureMode: BlockConnectedTextureMode.Seamless));
        Register(new BlockDef(BlockId.Nullrock, "Nullrock", solid: true, transparent: false, hardness: -1.0f, atlasIndex: 15));
        Register(new BlockDef(BlockId.Gravel, "Gravel", solid: true, transparent: false, hardness: 0.6f, atlasIndex: 16));
        Register(new BlockDef(BlockId.OakPlanks, "Oak Planks", solid: true, transparent: false, hardness: 0.8f, atlasIndex: 17, textureName: "oak_planks"));
        Register(new BlockDef(BlockId.GoldOre, "Gold Ore", solid: true, transparent: false, hardness: 3.0f, atlasIndex: 18, textureName: "gold"));
        Register(new BlockDef(BlockId.Diamond, "Diamond Ore", solid: true, transparent: false, hardness: 4.0f, atlasIndex: 19, textureName: "diamond"));
        Register(new BlockDef(BlockId.Runestone, "Runestone", solid: true, transparent: false, hardness: 1.2f, atlasIndex: 20, textureName: "runestone"));
        Register(new BlockDef(BlockId.Veinstone, "Veinstone", solid: true, transparent: false, hardness: 1.6f, atlasIndex: 21, textureName: "veinstone"));
        Register(new BlockDef(
            BlockId.Veilglass,
            "Veilglass",
            solid: true,
            transparent: true,
            hardness: 0.3f,
            atlasIndex: 22,
            textureName: "veilglass",
            renderLayer: BlockRenderLayer.Blended,
            connectedTextureMode: BlockConnectedTextureMode.Seamless));
        Register(new BlockDef(BlockId.Gravestone, "Gravestone", solid: true, transparent: false, hardness: 1.0f, atlasIndex: 23, textureName: "gravestone", customModel: true));
        Register(new BlockDef(BlockId.InscribedTablet, "Inscribed Tablet", solid: true, transparent: true, hardness: 0.8f, atlasIndex: 24, textureName: "inscribed_tablet"));
        Register(new BlockDef(BlockId.FragmentScrap, "Fragment Scrap", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 25, textureName: "fragment_scrap"));
        Register(new BlockDef(BlockId.VeilSealStone, "Veil Sealstone", solid: true, transparent: false, hardness: 2.0f, atlasIndex: 26, textureName: "veil_sealstone"));
        Register(new BlockDef(BlockId.EchoBloom, "Echo Bloom", solid: false, transparent: true, hardness: 0.2f, atlasIndex: 27, textureName: "echo_bloom"));
        Register(new BlockDef(BlockId.GraveSilt, "Grave Silt", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 28, textureName: "grave_silt"));
        Register(new BlockDef(BlockId.RunestoneDust, "Runestone Dust", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 29, textureName: "runestone_dust"));
        Register(new BlockDef(BlockId.VeinstoneCrystal, "Veinstone Crystal", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 30, textureName: "veinstone_crystal"));
        Register(new BlockDef(BlockId.AnchoringSalt, "Anchoring Salt", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 31, textureName: "anchoring_salt"));
        Register(new BlockDef(BlockId.StabilizedAsh, "Stabilized Ash", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 32, textureName: "stabilized_ash"));
        Register(new BlockDef(BlockId.EchoIconShard, "Echo Icon Shard", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 33, textureName: "echo_icon_shard"));
        Register(new BlockDef(BlockId.CopperWiring, "Copper Wiring", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 34, textureName: "copper_wiring"));
        Register(new BlockDef(BlockId.LimiterSigil, "Limiter Sigil", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 35, textureName: "limiter_sigil"));
        Register(new BlockDef(BlockId.AlignmentMatrixFragment, "Alignment Matrix Fragment", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 36, textureName: "alignment_matrix_fragment"));
        Register(new BlockDef(BlockId.AttunedKeystoneFragment, "Attuned Keystone Fragment", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 37, textureName: "attuned_keystone_fragment"));
        Register(new BlockDef(BlockId.RegulatorComponent, "Regulator Component", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 38, textureName: "regulator_component"));
        Register(new BlockDef(BlockId.TransitRegulatorPart, "Transit Regulator Part", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 39, textureName: "transit_regulator_part"));
        Register(new BlockDef(BlockId.WayfinderPlinthPart, "Wayfinder Plinth Part", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 40, textureName: "wayfinder_plinth_part"));
        Register(new BlockDef(BlockId.ResonanceCore, "Resonance Core", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 41, textureName: "resonance_core"));
        Register(new BlockDef(BlockId.AxiomFulgrite, "Axiom Fulgrite", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 42, textureName: "axiom_fulgrite"));
        Register(new BlockDef(BlockId.AnchoringAlloy, "Anchoring Alloy", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 43, textureName: "anchoring_alloy"));
        Register(new BlockDef(BlockId.CleanPhial, "Clean Phial", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 44, textureName: "clean_phial"));
        Register(new BlockDef(BlockId.Swiftleaf, "Swiftleaf", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 45, textureName: "swiftleaf"));
        Register(new BlockDef(BlockId.Driftcap, "Driftcap", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 46, textureName: "driftcap"));
        Register(new BlockDef(BlockId.Embermoss, "Embermoss", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 47, textureName: "embermoss"));
        Register(new BlockDef(BlockId.SigilLoom, "Sigil Loom", solid: true, transparent: false, hardness: 1.0f, atlasIndex: 48, textureName: "sigil_loom"));
        Register(new BlockDef(BlockId.QuietAlembic, "Quiet Alembic", solid: true, transparent: true, hardness: 0.8f, atlasIndex: 49, textureName: "quiet_alembic"));
        Register(new BlockDef(BlockId.RunicAnvil, "Runic Anvil", solid: true, transparent: false, hardness: 2.0f, atlasIndex: 50, textureName: "runic_anvil"));
        Register(new BlockDef(BlockId.CinderbranchStaff, "Cinderbranch Staff", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 51, textureName: "cinderbranch_staff"));
        Register(new BlockDef(BlockId.StormreedStaff, "Stormreed Staff", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 52, textureName: "stormreed_staff"));
        Register(new BlockDef(BlockId.WayboundFrame, "Waybound Frame", solid: true, transparent: false, hardness: 3.0f, atlasIndex: 53, textureName: "waybound_frame"));
        Register(new BlockDef(BlockId.AttunedKeystone, "Attuned Keystone", solid: true, transparent: true, hardness: 3.0f, atlasIndex: 54, textureName: "attuned_keystone"));
        Register(new BlockDef(BlockId.TimedLimiter, "Timed Limiter", solid: true, transparent: true, hardness: 2.0f, atlasIndex: 55, textureName: "timed_limiter"));
        Register(new BlockDef(BlockId.TransitRegulator, "Transit Regulator", solid: true, transparent: true, hardness: 2.0f, atlasIndex: 56, textureName: "transit_regulator"));
        Register(new BlockDef(BlockId.WaygatePlinth, "Waygate Plinth", solid: true, transparent: false, hardness: 2.5f, atlasIndex: 57, textureName: "waygate_plinth"));
        Register(new BlockDef(BlockId.WaygateRune, "Waygate Rune", solid: true, transparent: true, hardness: 1.0f, atlasIndex: 58, textureName: "waygate_rune"));
        Register(new BlockDef(BlockId.EvergateCore, "Evergate Core", solid: true, transparent: true, hardness: 3.5f, atlasIndex: 59, textureName: "evergate_core"));
        Register(new BlockDef(BlockId.EvergateCoreUnstable, "Evergate Core (Unstable)", solid: true, transparent: true, hardness: 3.5f, atlasIndex: 60, textureName: "evergate_core_unstable"));
        Register(new BlockDef(BlockId.FleetstepDraught, "Fleetstep Draught", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 61, textureName: "fleetstep_draught"));
        Register(new BlockDef(BlockId.SkyboundPhilter, "Skybound Philter", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 62, textureName: "skybound_philter"));
        Register(new BlockDef(BlockId.PyroskinTonic, "Pyroskin Tonic", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 63, textureName: "pyroskin_tonic"));
        Register(new BlockDef(BlockId.BrineveilElixir, "Brineveil Elixir", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 64, textureName: "brineveil_elixir"));
        Register(new BlockDef(BlockId.WaterBucket, "Water Bucket", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 65, textureName: "water_bucket", customModel: true));
        Register(new BlockDef(BlockId.PlantFiber, "Plant Fiber", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 66, textureName: "plant_fiber"));
        Register(new BlockDef(BlockId.CoalChunk, "Coal Chunk", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 67, textureName: "coal_chunk"));
        Register(new BlockDef(BlockId.IronBlock, "Iron Block", solid: true, transparent: false, hardness: 4.0f, atlasIndex: 69, textureName: "iron_block"));
        Register(new BlockDef(BlockId.GoldBlock, "Gold Block", solid: true, transparent: false, hardness: 3.0f, atlasIndex: 70, textureName: "gold_block"));
        Register(new BlockDef(BlockId.DiamondBlock, "Diamond Block", solid: true, transparent: false, hardness: 5.0f, atlasIndex: 71, textureName: "diamond_block"));
        Register(new BlockDef(BlockId.EmptyBucket, "Empty Bucket", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 72, textureName: "empty_bucket", customModel: true));
        Register(new BlockDef(BlockId.Torch, "Torch", solid: false, transparent: true, hardness: 0.2f, atlasIndex: 73, textureName: "torch", customModel: true, renderLayer: BlockRenderLayer.Cutout, lightLevel: 14f, lightMode: LightMode.Both));
        Register(new BlockDef(BlockId.FiberWrappedTorch, "Fiber-Wrapped Torch", solid: false, transparent: true, hardness: 0.2f, atlasIndex: 74, textureName: "fiber_wrapped_torch", customModel: true, renderLayer: BlockRenderLayer.Cutout, lightLevel: 14f, lightMode: LightMode.Both));
        
        // Log torch block lighting metadata
        var torchBlock = Get(BlockId.Torch);
        var fiberTorchBlock = Get(BlockId.FiberWrappedTorch);
        System.Diagnostics.Debug.WriteLine($"[LIGHTING DIAGNOSTIC] BlockRegistry: Torch blocks registered - Torch.LightLevel={torchBlock.LightLevel}, Torch.LightMode={torchBlock.LightMode}, FiberTorch.LightLevel={fiberTorchBlock.LightLevel}, FiberTorch.LightMode={fiberTorchBlock.LightMode}");
        Register(new BlockDef(BlockId.BasicKiln, "Basic Kiln", solid: true, transparent: false, hardness: 2.2f, atlasIndex: 75, textureName: "basic_kiln"));
        Register(new BlockDef(BlockId.AdvancedKiln, "Advanced Kiln", solid: true, transparent: false, hardness: 3.2f, atlasIndex: 76, textureName: "advanced_kiln"));
        Register(new BlockDef(BlockId.FieldOven, "Field Oven", solid: true, transparent: false, hardness: 2.4f, atlasIndex: 77, textureName: "field_oven"));
        Register(new BlockDef(BlockId.IronBillet, "Iron Billet", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 78, textureName: "iron_billet"));
        Register(new BlockDef(BlockId.GoldBillet, "Gold Billet", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 79, textureName: "gold_billet"));
        Register(new BlockDef(BlockId.DiamondGem, "Diamond", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 87, textureName: "diamond_gem"));
        Register(new BlockDef(BlockId.Pebbles, "Pebbles", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 88, textureName: "pebbles"));
        Register(new BlockDef(BlockId.CopperOre, "Copper Ore", solid: true, transparent: false, hardness: 2.5f, atlasIndex: 89, textureName: "copper"));
        Register(new BlockDef(BlockId.IronCluster, "Iron Cluster", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 90, textureName: "iron_cluster"));
        Register(new BlockDef(BlockId.GoldCluster, "Gold Cluster", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 91, textureName: "gold_cluster"));
        Register(new BlockDef(BlockId.CopperCluster, "Copper Cluster", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 92, textureName: "copper_cluster"));
        Register(new BlockDef(BlockId.CopperBillet, "Copper Billet", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 93, textureName: "copper_billet"));
        Register(new BlockDef(BlockId.EmeraldOre, "Emerald Ore", solid: true, transparent: false, hardness: 4.0f, atlasIndex: 94, textureName: "emerald"));
        Register(new BlockDef(BlockId.RubyOre, "Ruby Ore", solid: true, transparent: false, hardness: 4.0f, atlasIndex: 95, textureName: "ruby"));
        Register(new BlockDef(BlockId.SapphireOre, "Sapphire Ore", solid: true, transparent: false, hardness: 4.0f, atlasIndex: 96, textureName: "sapphire"));
        Register(new BlockDef(BlockId.AmethystOre, "Amethyst Ore", solid: true, transparent: false, hardness: 4.0f, atlasIndex: 97, textureName: "amethyst"));
        Register(new BlockDef(BlockId.Emerald, "Emerald", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 98, textureName: "emerald_gem"));
        Register(new BlockDef(BlockId.RubyStone, "Ruby Stone", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 99, textureName: "ruby_stone"));
        Register(new BlockDef(BlockId.SapphireGem, "Sapphire Gem", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 100, textureName: "sapphire_gem"));
        Register(new BlockDef(BlockId.AmethystShard, "Amethyst Shard", solid: false, transparent: true, hardness: 0.0f, atlasIndex: 101, textureName: "amethyst_shard"));
        Register(new BlockDef(BlockId.BasicKilnLit, "Basic Kiln", solid: true, transparent: false, hardness: 2.2f, atlasIndex: 102, textureName: "basic_kiln_on", visible: false));
        Register(new BlockDef(BlockId.AdvancedKilnLit, "Advanced Kiln", solid: true, transparent: false, hardness: 3.2f, atlasIndex: 103, textureName: "advanced_kiln_on", visible: false));
        Register(new BlockDef(BlockId.FieldOvenLit, "Field Oven", solid: true, transparent: false, hardness: 2.4f, atlasIndex: 104, textureName: "field_oven_on", visible: false));
        Register(new BlockDef(BlockId.SleepingBag, "Sleeping Bag", solid: false, transparent: true, hardness: 0.5f, atlasIndex: 105, textureName: "sleeping_bag", customModel: true, renderLayer: BlockRenderLayer.Cutout));

        AllDefs.Sort((a, b) => a.AtlasIndex.CompareTo(b.AtlasIndex));
        BuildTokenRegistry();
    }

    private static void Register(BlockDef def)
    {
        if (HiddenFromCatalogForNow.Contains(def.Id) && def.IsVisibleInInventory)
        {
            def = new BlockDef(
                def.Id,
                def.Name,
                def.IsSolid,
                def.IsTransparent,
                def.Hardness,
                def.AtlasIndex,
                def.TextureName,
                visible: false,
                customModel: def.HasCustomModel,
                renderLayer: def.RenderLayer,
                connectedTextureMode: def.ConnectedTextureMode);
        }

        var idx = (byte)def.Id;

        if (Defs[idx] != null)
            throw new InvalidOperationException($"Duplicate BlockId {idx} ({def.Id}).");
        
        // Replace in array
        Defs[idx] = def;
        
        // Replace or add in list
        var existingIdx = AllDefs.FindIndex(d => d.Id == def.Id);
        if (existingIdx >= 0)
            AllDefs[existingIdx] = def;
        else
            AllDefs.Add(def);
    }

    public static BlockDef Get(BlockId id) => Defs[(byte)id] ?? Defs[(byte)BlockId.Air]!;

    public static BlockDef Get(byte id) => Defs[id] ?? Defs[(byte)BlockId.Air]!;

    public static bool IsSolid(byte id) => Get(id).IsSolid;

    public static bool IsTransparent(byte id) => Get(id).IsTransparent;

    public static BlockRenderLayer GetRenderLayer(byte id) => Get(id).RenderLayer;

    public static bool UsesConnectedTexture(byte id) => Get(id).ConnectedTextureMode != BlockConnectedTextureMode.None;

    public static bool TryResolveToken(string token, out BlockId id)
    {
        id = BlockId.Air;
        var normalized = NormalizeToken(token);
        if (normalized.Length == 0)
            return false;

        return TokenLookup.TryGetValue(normalized, out id) && id != BlockId.Air;
    }

    public static IReadOnlyList<string> GetCommandTokens(BlockId id)
    {
        if (CommandTokens.TryGetValue(id, out var tokens))
            return tokens;

        return Array.Empty<string>();
    }

    public static IReadOnlyList<BlockDef> All => AllDefs;

    public static int AtlasRegionCount => AllDefs.Count == 0 ? 0 : AllDefs.Max(d => d.AtlasIndex) + 1;

    private static void BuildTokenRegistry()
    {
        TokenLookup.Clear();
        CommandTokens.Clear();

        foreach (var def in AllDefs)
        {
            if (def.Id == BlockId.Air)
                continue;

            var completionTokens = new List<string>();
            var completionSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddAlias(ToSnakeCase(def.Name), includeInCompletion: true);
            AddAlias(def.Id.ToString(), includeInCompletion: true);
            AddAlias(def.Name, includeInCompletion: false);
            AddAlias(def.Name.Replace(" ", string.Empty, StringComparison.Ordinal), includeInCompletion: true);

            if (!string.IsNullOrWhiteSpace(def.TextureName))
                AddAlias(def.TextureName, includeInCompletion: true);

            // Friendly give-command aliases.
            switch (def.Id)
            {
                case BlockId.CoalOre:
                    AddAlias("coal", includeInCompletion: true);
                    AddAlias("coal_ore", includeInCompletion: true);
                    break;
                case BlockId.OakLog:
                    AddAlias("wood", includeInCompletion: true);
                    AddAlias("oak", includeInCompletion: true);
                    AddAlias("oak_wood", includeInCompletion: true);
                    AddAlias("oak_log", includeInCompletion: true);
                    break;
                case BlockId.OakPlanks:
                    AddAlias("plank", includeInCompletion: true);
                    AddAlias("planks", includeInCompletion: true);
                    AddAlias("oak_plank", includeInCompletion: true);
                    AddAlias("oak_planks", includeInCompletion: true);
                    break;
                case BlockId.IronOre:
                    AddAlias("iron", includeInCompletion: true);
                    AddAlias("iron_ore", includeInCompletion: true);
                    break;
                case BlockId.GoldOre:
                    AddAlias("gold", includeInCompletion: true);
                    AddAlias("gold_ore", includeInCompletion: true);
                    break;
                case BlockId.Diamond:
                    AddAlias("diamond", includeInCompletion: true);
                    AddAlias("diamond_ore", includeInCompletion: true);
                    break;
                case BlockId.CopperOre:
                    AddAlias("copper", includeInCompletion: true);
                    AddAlias("copper_ore", includeInCompletion: true);
                    break;
                case BlockId.EmeraldOre:
                    AddAlias("emerald", includeInCompletion: true);
                    AddAlias("emerald_ore", includeInCompletion: true);
                    break;
                case BlockId.RubyOre:
                    AddAlias("ruby", includeInCompletion: true);
                    AddAlias("ruby_ore", includeInCompletion: true);
                    break;
                case BlockId.SapphireOre:
                    AddAlias("sapphire", includeInCompletion: true);
                    AddAlias("sapphire_ore", includeInCompletion: true);
                    break;
                case BlockId.AmethystOre:
                    AddAlias("amethyst", includeInCompletion: true);
                    AddAlias("amethyst_ore", includeInCompletion: true);
                    break;
                case BlockId.IronBlock:
                    AddAlias("iron_block", includeInCompletion: true);
                    break;
                case BlockId.GoldBlock:
                    AddAlias("gold_block", includeInCompletion: true);
                    break;
                case BlockId.DiamondBlock:
                    AddAlias("diamond_block", includeInCompletion: true);
                    break;
                case BlockId.EmptyBucket:
                    AddAlias("bucket", includeInCompletion: true);
                    AddAlias("empty_bucket", includeInCompletion: true);
                    break;
            }

            CommandTokens[def.Id] = completionTokens.ToArray();

            void AddAlias(string? token, bool includeInCompletion)
            {
                var trimmed = (token ?? string.Empty).Trim();
                if (trimmed.Length == 0)
                    return;

                var normalized = NormalizeToken(trimmed);
                if (normalized.Length == 0)
                    return;

                if (!TokenLookup.ContainsKey(normalized))
                    TokenLookup[normalized] = def.Id;

                if (!includeInCompletion)
                    return;

                var displayToken = trimmed.ToLowerInvariant().Replace(' ', '_');
                if (displayToken.Length == 0 || !completionSeen.Add(displayToken))
                    return;
                completionTokens.Add(displayToken);
            }
        }
    }

    private static string NormalizeToken(string token)
    {
        var normalized = (token ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return string.Empty;

        normalized = normalized.Replace("_", string.Empty, StringComparison.Ordinal);
        normalized = normalized.Replace("-", string.Empty, StringComparison.Ordinal);
        normalized = normalized.Replace(" ", string.Empty, StringComparison.Ordinal);
        return normalized;
    }

    private static string ToSnakeCase(string value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
            return string.Empty;

        var chars = new List<char>(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c == '-' || c == '_')
            {
                if (chars.Count == 0 || chars[^1] == '_')
                    continue;
                chars.Add('_');
                continue;
            }

            if (char.IsUpper(c))
            {
                if (i > 0 && char.IsLower(text[i - 1]) && chars.Count > 0 && chars[^1] != '_')
                    chars.Add('_');
                chars.Add(char.ToLowerInvariant(c));
                continue;
            }

            chars.Add(char.ToLowerInvariant(c));
        }

        return new string(chars.ToArray()).Trim('_');
    }
}
