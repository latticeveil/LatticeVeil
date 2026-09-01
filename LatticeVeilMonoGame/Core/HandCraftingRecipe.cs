using System.Collections.Generic;

namespace LatticeVeilMonoGame.Core;

public enum HandCraftingIngredientKind
{
    Item = 0,
    Tag = 1
}

public sealed record HandCraftingIngredient(
    HandCraftingIngredientKind Kind,
    BlockId ItemId,
    ItemTag Tag,
    int Count,
    string DisplayName)
{
    public static HandCraftingIngredient Item(BlockId itemId, int count) =>
        new(HandCraftingIngredientKind.Item, itemId, ItemTag.None, count, ItemRegistry.Get((byte)itemId).Name);

    public static HandCraftingIngredient Tagged(ItemTag tag, int count, string displayName) =>
        new(HandCraftingIngredientKind.Tag, BlockId.Air, tag, count, displayName);

    public bool Matches(BlockId id) =>
        Kind == HandCraftingIngredientKind.Item
            ? id == ItemId
            : ItemRegistry.HasTag((ItemId)(byte)id, Tag);
}

public sealed record HandCraftingRecipe(
    string Id,
    string Name,
    IReadOnlyList<HandCraftingIngredient> Ingredients,
    BlockId OutputId,
    int OutputCount);

public static class HandCraftingRecipes
{
    public static readonly HandCraftingRecipe OakPlanksFromOakLog = new(
        "oak_planks_from_oak_log",
        "Oak Planks",
        new[] { HandCraftingIngredient.Item(BlockId.OakLog, 1) },
        BlockId.OakPlanks,
        4);

    public static readonly HandCraftingRecipe SticksFromOakPlanks = new(
        "sticks_from_oak_planks",
        "Sticks",
        new[] { HandCraftingIngredient.Tagged(ItemTag.WoodPlank, 2, "Any wood planks") },
        BlockId.Stick,
        4);

    public static readonly HandCraftingRecipe ArtificerBenchFromWoodPlanks = new(
        "artificer_bench_from_wood_planks",
        "Artificer Bench",
        new[] { HandCraftingIngredient.Tagged(ItemTag.WoodPlank, 4, "Any wood planks") },
        BlockId.ArtificerBench,
        1);

    public static readonly HandCraftingRecipe TorchesFromCoalChunkAndStick = new(
        "torches_from_coal_chunk_and_stick",
        "Torches",
        new[]
        {
            HandCraftingIngredient.Item(BlockId.CoalChunk, 1),
            HandCraftingIngredient.Item(BlockId.Stick, 1)
        },
        BlockId.Torch,
        4);

    public static readonly HandCraftingRecipe FiberWrappedTorchesFromFiberCoalChunkAndStick = new(
        "fiber_wrapped_torches_from_fiber_coal_chunk_and_stick",
        "Fiber-Wrapped Torches",
        new[]
        {
            HandCraftingIngredient.Item(BlockId.PlantFiber, 1),
            HandCraftingIngredient.Item(BlockId.CoalChunk, 1),
            HandCraftingIngredient.Item(BlockId.Stick, 1)
        },
        BlockId.FiberWrappedTorch,
        4);

    public static readonly HandCraftingRecipe BasicKilnFromStoneAndCoalChunk = new(
        "basic_kiln_from_stone_and_coal_chunk",
        "Basic Kiln",
        new[]
        {
            HandCraftingIngredient.Item(BlockId.Stone, 6),
            HandCraftingIngredient.Item(BlockId.CoalChunk, 1)
        },
        BlockId.BasicKiln,
        1);

    public static readonly HandCraftingRecipe AdvancedKilnFromBasicKilnAndBillets = new(
        "advanced_kiln_from_basic_kiln_and_billets",
        "Advanced Kiln",
        new[]
        {
            HandCraftingIngredient.Item(BlockId.BasicKiln, 1),
            HandCraftingIngredient.Item(BlockId.IronBillet, 4),
            HandCraftingIngredient.Item(BlockId.GoldBillet, 1)
        },
        BlockId.AdvancedKiln,
        1);

    public static readonly HandCraftingRecipe FieldOvenFromBasicKilnStoneAndIronBillet = new(
        "field_oven_from_basic_kiln_stone_and_iron_billet",
        "Field Oven",
        new[]
        {
            HandCraftingIngredient.Item(BlockId.BasicKiln, 1),
            HandCraftingIngredient.Item(BlockId.Stone, 4),
            HandCraftingIngredient.Item(BlockId.IronBillet, 1)
        },
        BlockId.FieldOven,
        1);

    public static readonly HandCraftingRecipe PlantRationFromPlantFiber = new(
        "plant_ration_from_plant_fiber",
        "Plant Ration",
        new[] { HandCraftingIngredient.Item(BlockId.PlantFiber, 3) },
        BlockId.PlantRation,
        1);

    public static readonly HandCraftingRecipe WoodPickaxeFromPlanksAndSticks = new(
        "wood_mattock_from_planks_and_sticks",
        "Wood Mattock",
        new[]
        {
            HandCraftingIngredient.Tagged(ItemTag.WoodPlank, 3, "Any wood planks"),
            HandCraftingIngredient.Item(BlockId.Stick, 2)
        },
        BlockId.WoodMattock,
        1);

    public static readonly HandCraftingRecipe StonePickaxeFromStoneAndSticks = new(
        "stone_mattock_from_stone_and_sticks",
        "Stone Mattock",
        new[]
        {
            HandCraftingIngredient.Item(BlockId.Stone, 3),
            HandCraftingIngredient.Item(BlockId.Stick, 2)
        },
        BlockId.StoneMattock,
        1);

    public static readonly HandCraftingRecipe IronPickaxeFromBilletsAndSticks = new(
        "iron_mattock_from_billets_and_sticks",
        "Iron Mattock",
        new[]
        {
            HandCraftingIngredient.Item(BlockId.IronBillet, 3),
            HandCraftingIngredient.Item(BlockId.Stick, 2)
        },
        BlockId.IronMattock,
        1);

    public static readonly HandCraftingRecipe DiamondPickaxeFromGemsAndSticks = new(
        "diamond_mattock_from_gems_and_sticks",
        "Diamond Mattock",
        new[]
        {
            HandCraftingIngredient.Item(BlockId.DiamondGem, 3),
            HandCraftingIngredient.Item(BlockId.Stick, 2)
        },
        BlockId.DiamondMattock,
        1);

    public static readonly HandCraftingRecipe WoodAxeFromPlanksAndSticks = new(
        "wood_hatchet_from_planks_and_sticks",
        "Wood Hatchet",
        new[]
        {
            HandCraftingIngredient.Tagged(ItemTag.WoodPlank, 3, "Any wood planks"),
            HandCraftingIngredient.Item(BlockId.Stick, 2)
        },
        BlockId.WoodHatchet,
        1);

    public static readonly HandCraftingRecipe StoneAxeFromStoneAndSticks = new(
        "stone_hatchet_from_stone_and_sticks",
        "Stone Hatchet",
        new[]
        {
            HandCraftingIngredient.Item(BlockId.Stone, 3),
            HandCraftingIngredient.Item(BlockId.Stick, 2)
        },
        BlockId.StoneHatchet,
        1);

    public static readonly HandCraftingRecipe IronAxeFromBilletsAndSticks = new(
        "iron_hatchet_from_billets_and_sticks",
        "Iron Hatchet",
        new[]
        {
            HandCraftingIngredient.Item(BlockId.IronBillet, 3),
            HandCraftingIngredient.Item(BlockId.Stick, 2)
        },
        BlockId.IronHatchet,
        1);

    public static readonly HandCraftingRecipe WoodShovelFromPlanksAndSticks = new(
        "wood_spade_from_planks_and_sticks",
        "Wood Spade",
        new[]
        {
            HandCraftingIngredient.Tagged(ItemTag.WoodPlank, 1, "Any wood planks"),
            HandCraftingIngredient.Item(BlockId.Stick, 2)
        },
        BlockId.WoodSpade,
        1);

    public static readonly HandCraftingRecipe StoneShovelFromStoneAndSticks = new(
        "stone_spade_from_stone_and_sticks",
        "Stone Spade",
        new[]
        {
            HandCraftingIngredient.Item(BlockId.Stone, 1),
            HandCraftingIngredient.Item(BlockId.Stick, 2)
        },
        BlockId.StoneSpade,
        1);

    public static readonly HandCraftingRecipe IronShovelFromBilletsAndSticks = new(
        "iron_spade_from_billets_and_sticks",
        "Iron Spade",
        new[]
        {
            HandCraftingIngredient.Item(BlockId.IronBillet, 1),
            HandCraftingIngredient.Item(BlockId.Stick, 2)
        },
        BlockId.IronSpade,
        1);

    public static readonly HandCraftingRecipe DiamondAxeFromGemsAndSticks = new(
        "diamond_hatchet_from_gems_and_sticks",
        "Diamond Hatchet",
        new[]
        {
            HandCraftingIngredient.Item(BlockId.DiamondGem, 3),
            HandCraftingIngredient.Item(BlockId.Stick, 2)
        },
        BlockId.DiamondHatchet,
        1);

    public static readonly HandCraftingRecipe DiamondShovelFromGemsAndSticks = new(
        "diamond_spade_from_gems_and_sticks",
        "Diamond Spade",
        new[]
        {
            HandCraftingIngredient.Item(BlockId.DiamondGem, 1),
            HandCraftingIngredient.Item(BlockId.Stick, 2)
        },
        BlockId.DiamondSpade,
        1);

    // --- Blades ---
    public static readonly HandCraftingRecipe WoodBladeFromPlanksAndStick = new(
        "wood_blade_from_planks_and_stick",
        "Wood Blade",
        new[]
        {
            HandCraftingIngredient.Tagged(ItemTag.WoodPlank, 2, "Any wood planks"),
            HandCraftingIngredient.Item(BlockId.Stick, 1)
        },
        BlockId.WoodBlade,
        1);

    public static readonly HandCraftingRecipe StoneBladeFromStoneAndStick = new(
        "stone_blade_from_stone_and_stick",
        "Stone Blade",
        new[]
        {
            HandCraftingIngredient.Item(BlockId.Stone, 2),
            HandCraftingIngredient.Item(BlockId.Stick, 1)
        },
        BlockId.StoneBlade,
        1);

    public static readonly HandCraftingRecipe IronBladeFromBilletsAndStick = new(
        "iron_blade_from_billets_and_stick",
        "Iron Blade",
        new[]
        {
            HandCraftingIngredient.Item(BlockId.IronBillet, 2),
            HandCraftingIngredient.Item(BlockId.Stick, 1)
        },
        BlockId.IronBlade,
        1);

    public static readonly HandCraftingRecipe DiamondBladeFromGemsAndStick = new(
        "diamond_blade_from_gems_and_stick",
        "Diamond Blade",
        new[]
        {
            HandCraftingIngredient.Item(BlockId.DiamondGem, 2),
            HandCraftingIngredient.Item(BlockId.Stick, 1)
        },
        BlockId.DiamondBlade,
        1);

    // --- Plows ---
    public static readonly HandCraftingRecipe WoodPlowFromPlanksAndSticks = new(
        "wood_plow_from_planks_and_sticks",
        "Wood Plow",
        new[]
        {
            HandCraftingIngredient.Tagged(ItemTag.WoodPlank, 2, "Any wood planks"),
            HandCraftingIngredient.Item(BlockId.Stick, 2)
        },
        BlockId.WoodPlow,
        1);

    public static readonly HandCraftingRecipe StonePlowFromStoneAndSticks = new(
        "stone_plow_from_stone_and_sticks",
        "Stone Plow",
        new[]
        {
            HandCraftingIngredient.Item(BlockId.Stone, 2),
            HandCraftingIngredient.Item(BlockId.Stick, 2)
        },
        BlockId.StonePlow,
        1);

    public static readonly HandCraftingRecipe IronPlowFromBilletsAndSticks = new(
        "iron_plow_from_billets_and_sticks",
        "Iron Plow",
        new[]
        {
            HandCraftingIngredient.Item(BlockId.IronBillet, 2),
            HandCraftingIngredient.Item(BlockId.Stick, 2)
        },
        BlockId.IronPlow,
        1);

    public static readonly HandCraftingRecipe DiamondPlowFromGemsAndSticks = new(
        "diamond_plow_from_gems_and_sticks",
        "Diamond Plow",
        new[]
        {
            HandCraftingIngredient.Item(BlockId.DiamondGem, 2),
            HandCraftingIngredient.Item(BlockId.Stick, 2)
        },
        BlockId.DiamondPlow,
        1);

    // --- Javelins ---
    public static readonly HandCraftingRecipe WoodJavelinFromPlanksSticksAndFiber = new(
        "wood_javelin_from_planks_sticks_and_fiber",
        "Wood Javelin",
        new[]
        {
            HandCraftingIngredient.Tagged(ItemTag.WoodPlank, 1, "Any wood planks"),
            HandCraftingIngredient.Item(BlockId.Stick, 2),
            HandCraftingIngredient.Item(BlockId.PlantFiber, 1)
        },
        BlockId.WoodJavelin,
        1);

    public static readonly HandCraftingRecipe StoneJavelinFromStoneSticksAndFiber = new(
        "stone_javelin_from_stone_sticks_and_fiber",
        "Stone Javelin",
        new[]
        {
            HandCraftingIngredient.Item(BlockId.Stone, 1),
            HandCraftingIngredient.Item(BlockId.Stick, 2),
            HandCraftingIngredient.Item(BlockId.PlantFiber, 1)
        },
        BlockId.StoneJavelin,
        1);

    public static readonly HandCraftingRecipe IronJavelinFromBilletsSticksAndFiber = new(
        "iron_javelin_from_billets_sticks_and_fiber",
        "Iron Javelin",
        new[]
        {
            HandCraftingIngredient.Item(BlockId.IronBillet, 1),
            HandCraftingIngredient.Item(BlockId.Stick, 2),
            HandCraftingIngredient.Item(BlockId.PlantFiber, 1)
        },
        BlockId.IronJavelin,
        1);

    public static readonly HandCraftingRecipe DiamondJavelinFromGemsSticksAndFiber = new(
        "diamond_javelin_from_gems_sticks_and_fiber",
        "Diamond Javelin",
        new[]
        {
            HandCraftingIngredient.Item(BlockId.DiamondGem, 1),
            HandCraftingIngredient.Item(BlockId.Stick, 2),
            HandCraftingIngredient.Item(BlockId.PlantFiber, 1)
        },
        BlockId.DiamondJavelin,
        1);

    public static readonly HandCraftingRecipe StoneFromPebbles = new(
        "stone_from_pebbles",
        "Stone",
        new[] { HandCraftingIngredient.Item(BlockId.Pebbles, 4) },
        BlockId.Stone,
        1);

    public static readonly HandCraftingRecipe SleepingBagFromPlantFiber = new(
        "sleeping_bag_from_plant_fiber",
        "Sleeping Bag",
        new[] { HandCraftingIngredient.Item(BlockId.PlantFiber, 6) },
        BlockId.SleepingBag,
        1);

    public static IReadOnlyList<HandCraftingRecipe> All { get; } = new[]
    {
        OakPlanksFromOakLog,
        SticksFromOakPlanks,
        PlantRationFromPlantFiber,
        StoneFromPebbles,
        SleepingBagFromPlantFiber,
        WoodPickaxeFromPlanksAndSticks,
        StonePickaxeFromStoneAndSticks,
        IronPickaxeFromBilletsAndSticks,
        DiamondPickaxeFromGemsAndSticks,
        WoodAxeFromPlanksAndSticks,
        StoneAxeFromStoneAndSticks,
        IronAxeFromBilletsAndSticks,
        DiamondAxeFromGemsAndSticks,
        WoodShovelFromPlanksAndSticks,
        StoneShovelFromStoneAndSticks,
        IronShovelFromBilletsAndSticks,
        DiamondShovelFromGemsAndSticks,
        WoodBladeFromPlanksAndStick,
        StoneBladeFromStoneAndStick,
        IronBladeFromBilletsAndStick,
        DiamondBladeFromGemsAndStick,
        WoodPlowFromPlanksAndSticks,
        StonePlowFromStoneAndSticks,
        IronPlowFromBilletsAndSticks,
        DiamondPlowFromGemsAndSticks,
        WoodJavelinFromPlanksSticksAndFiber,
        StoneJavelinFromStoneSticksAndFiber,
        IronJavelinFromBilletsSticksAndFiber,
        DiamondJavelinFromGemsSticksAndFiber,
        ArtificerBenchFromWoodPlanks,
        TorchesFromCoalChunkAndStick,
        FiberWrappedTorchesFromFiberCoalChunkAndStick,
        BasicKilnFromStoneAndCoalChunk,
        AdvancedKilnFromBasicKilnAndBillets,
        FieldOvenFromBasicKilnStoneAndIronBillet
    };
}
