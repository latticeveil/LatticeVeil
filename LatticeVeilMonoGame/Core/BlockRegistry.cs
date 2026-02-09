using System;
using System.Collections.Generic;
using System.Linq;

namespace LatticeVeilMonoGame.Core;

public sealed class BlockDef
{
    public BlockId Id { get; }
    public string Name { get; }
    public bool IsSolid { get; }
    public bool IsTransparent { get; }
    public float Hardness { get; }
    public int AtlasIndex { get; }
    public string? TextureName { get; }
    public bool IsVisibleInInventory { get; }
    public bool HasCustomModel { get; }

    public BlockDef(BlockId id, string name, bool solid, bool transparent, float hardness, int atlasIndex, string? textureName = null, bool visible = true, bool customModel = false)
    {
        Id = id;
        Name = name;
        IsSolid = solid;
        IsTransparent = transparent;
        Hardness = hardness;
        AtlasIndex = atlasIndex;
        TextureName = textureName;
        IsVisibleInInventory = visible;
        HasCustomModel = customModel;
    }
}

public static class BlockRegistry
{
    private static readonly BlockDef?[] Defs = new BlockDef?[256];
    private static readonly List<BlockDef> AllDefs = new();

    static BlockRegistry()
    {
        Register(new BlockDef(BlockId.Air, "Air", solid: false, transparent: true, hardness: 0f, atlasIndex: 0));
        Register(new BlockDef(BlockId.Grass, "Grass", solid: true, transparent: false, hardness: 0.6f, atlasIndex: 1));
        Register(new BlockDef(BlockId.Dirt, "Dirt", solid: true, transparent: false, hardness: 0.5f, atlasIndex: 2));
        Register(new BlockDef(BlockId.Stone, "Stone", solid: true, transparent: false, hardness: 1.5f, atlasIndex: 3));
        Register(new BlockDef(BlockId.Water, "Water", solid: false, transparent: true, hardness: 0f, atlasIndex: 4, visible: false));
        Register(new BlockDef(BlockId.Sand, "Sand", solid: true, transparent: false, hardness: 0.4f, atlasIndex: 5));
        Register(new BlockDef(BlockId.Wood, "Wood", solid: true, transparent: false, hardness: 1.0f, atlasIndex: 6));
        Register(new BlockDef(BlockId.Leaves, "Leaves", solid: true, transparent: true, hardness: 0.2f, atlasIndex: 7));
        Register(new BlockDef(BlockId.Chest, "Chest", solid: true, transparent: true, hardness: 1.0f, atlasIndex: 10));
        Register(new BlockDef(BlockId.Coal, "Coal", solid: true, transparent: false, hardness: 2.0f, atlasIndex: 11));
        Register(new BlockDef(BlockId.Iron, "Iron", solid: true, transparent: false, hardness: 3.0f, atlasIndex: 12));
        Register(new BlockDef(BlockId.ArtificerBench, "Artificer Bench", solid: true, transparent: false, hardness: 1.0f, atlasIndex: 13));
        Register(new BlockDef(BlockId.Glass, "Glass", solid: true, transparent: true, hardness: 0.3f, atlasIndex: 14));
        Register(new BlockDef(BlockId.Nullrock, "Nullrock", solid: true, transparent: false, hardness: -1.0f, atlasIndex: 15));
        Register(new BlockDef(BlockId.Gravel, "Gravel", solid: true, transparent: false, hardness: 0.6f, atlasIndex: 16));
        Register(new BlockDef(BlockId.Plank, "Plank", solid: true, transparent: false, hardness: 0.8f, atlasIndex: 17));
        Register(new BlockDef(BlockId.Gold, "Gold", solid: true, transparent: false, hardness: 3.0f, atlasIndex: 18));
        Register(new BlockDef(BlockId.Diamond, "Diamond", solid: true, transparent: false, hardness: 4.0f, atlasIndex: 19));
        Register(new BlockDef(BlockId.Runestone, "Runestone", solid: true, transparent: false, hardness: 1.2f, atlasIndex: 20, textureName: "runestone"));
        Register(new BlockDef(BlockId.Veinstone, "Veinstone", solid: true, transparent: false, hardness: 1.6f, atlasIndex: 21, textureName: "veinstone"));
        Register(new BlockDef(BlockId.Veilglass, "Veilglass", solid: true, transparent: true, hardness: 0.3f, atlasIndex: 22, textureName: "veilglass"));
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

        AllDefs.Sort((a, b) => a.AtlasIndex.CompareTo(b.AtlasIndex));
    }

    private static void Register(BlockDef def)
    {
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

    public static IReadOnlyList<BlockDef> All => AllDefs;

    public static int AtlasRegionCount => AllDefs.Count == 0 ? 0 : AllDefs.Max(d => d.AtlasIndex) + 1;
}
