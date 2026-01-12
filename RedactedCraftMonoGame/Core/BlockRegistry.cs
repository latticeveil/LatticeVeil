using System;
using System.Collections.Generic;
using System.Linq;

namespace RedactedCraftMonoGame.Core;

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
    private static readonly BlockDef[] Defs = new BlockDef[256];
    private static readonly List<BlockDef> AllDefs = new();

    static BlockRegistry()
    {
        Register(new BlockDef(BlockId.Air, "Air", solid: false, transparent: true, hardness: 0f, atlasIndex: 0));
        Register(new BlockDef(BlockId.Grass, "Grass", solid: true, transparent: false, hardness: 0.6f, atlasIndex: 1));
        Register(new BlockDef(BlockId.Dirt, "Dirt", solid: true, transparent: false, hardness: 0.5f, atlasIndex: 2));
        Register(new BlockDef(BlockId.Stone, "Stone", solid: true, transparent: false, hardness: 1.5f, atlasIndex: 3));
        Register(new BlockDef(BlockId.Water, "Water", solid: false, transparent: true, hardness: 0f, atlasIndex: 4));
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

        AllDefs.Sort((a, b) => a.AtlasIndex.CompareTo(b.AtlasIndex));
    }

    private static void Register(BlockDef def)
    {
        var idx = (byte)def.Id;
        
        // Replace in array
        Defs[idx] = def;
        
        // Replace or add in list
        var existingIdx = AllDefs.FindIndex(d => d.Id == def.Id);
        if (existingIdx >= 0)
            AllDefs[existingIdx] = def;
        else
            AllDefs.Add(def);
    }

    public static BlockDef Get(BlockId id) => Defs[(byte)id] ?? Defs[(byte)BlockId.Air];

    public static BlockDef Get(byte id) => Defs[id] ?? Defs[(byte)BlockId.Air];

    public static bool IsSolid(byte id) => Get(id).IsSolid;

    public static bool IsTransparent(byte id) => Get(id).IsTransparent;

    public static IReadOnlyList<BlockDef> All => AllDefs;

    public static int AtlasRegionCount => AllDefs.Count == 0 ? 0 : AllDefs.Max(d => d.AtlasIndex) + 1;
}
