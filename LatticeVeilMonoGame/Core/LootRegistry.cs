using System;
using System.Collections.Generic;

namespace LatticeVeilMonoGame.Core;

public readonly struct ItemDrop
{
    public ItemDrop(ItemId id, int count)
    {
        Id = id;
        Count = count;
    }

    public ItemId Id { get; }
    public int Count { get; }
}

public static class LootRegistry
{
    public enum HarvestToolTier
    {
        None = 0,
        Wood = 1,
        Stone = 2,
        Iron = 3,
        Diamond = 4
    }

    public static IEnumerable<ItemDrop> RollBlockDrops(BlockId sourceBlock, Random random, HarvestToolTier toolTier = HarvestToolTier.None)
    {
        if (sourceBlock == BlockId.Leaves)
        {
            if (random.NextDouble() < 0.65)
            {
                var count = random.NextDouble() < 0.18 ? 2 : 1;
                yield return new ItemDrop(ItemId.PlantFiber, count);
            }

            if (random.NextDouble() < 0.35)
            {
                yield return new ItemDrop(random.NextDouble() < 0.25 ? ItemId.Swiftleaf : ItemId.ForestBerries, 1);
            }

            yield break;
        }

        if (sourceBlock == BlockId.Grass)
        {
            yield return new ItemDrop(ItemId.DirtBlock, 1);
            if (random.NextDouble() < 0.20)
                yield return new ItemDrop(ItemId.PlantFiber, 1);
            yield break;
        }

        if (sourceBlock == BlockId.Stone)
        {
            if (toolTier < HarvestToolTier.Wood)
                yield break;

            yield return new ItemDrop(ItemId.StoneBlock, 1);
            if (random.NextDouble() < 0.15)
                yield return new ItemDrop(ItemId.Pebbles, 1);
            yield break;
        }

        if (sourceBlock == BlockId.CoalOre)
        {
            if (toolTier < HarvestToolTier.Wood)
                yield break;

            yield return new ItemDrop(ItemId.CoalChunk, random.NextDouble() < 0.30 ? 2 : 1);
            yield return new ItemDrop(ItemId.Pebbles, RollPebbleCount(random, 0.45, 0.12));
            yield break;
        }

        if (sourceBlock == BlockId.IronOre)
        {
            if (toolTier < HarvestToolTier.Stone)
                yield break;

            yield return new ItemDrop(ItemId.IronCluster, 1);
            yield return new ItemDrop(ItemId.Pebbles, RollPebbleCount(random, 0.38, 0.08));
            yield break;
        }

        if (sourceBlock == BlockId.CopperOre)
        {
            if (toolTier < HarvestToolTier.Stone)
                yield break;

            yield return new ItemDrop(ItemId.CopperCluster, 1);
            yield return new ItemDrop(ItemId.Pebbles, RollPebbleCount(random, 0.40, 0.10));
            yield break;
        }

        if (sourceBlock == BlockId.GoldOre)
        {
            if (toolTier < HarvestToolTier.Iron)
                yield break;

            yield return new ItemDrop(ItemId.GoldCluster, 1);
            yield return new ItemDrop(ItemId.Pebbles, RollPebbleCount(random, 0.32, 0.06));
            yield break;
        }

        if (sourceBlock == BlockId.Diamond)
        {
            if (toolTier < HarvestToolTier.Iron)
                yield break;

            yield return new ItemDrop(ItemId.DiamondGem, 1);
            yield return new ItemDrop(ItemId.Pebbles, RollPebbleCount(random, 0.24, 0.04));
            yield break;
        }

        if (sourceBlock == BlockId.EmeraldOre)
        {
            if (toolTier < HarvestToolTier.Iron)
                yield break;

            yield return new ItemDrop(ItemId.Emerald, 1);
            yield return new ItemDrop(ItemId.Pebbles, RollPebbleCount(random, 0.24, 0.04));
            yield break;
        }

        if (sourceBlock == BlockId.RubyOre)
        {
            if (toolTier < HarvestToolTier.Iron)
                yield break;

            yield return new ItemDrop(ItemId.RubyStone, 1);
            yield return new ItemDrop(ItemId.Pebbles, RollPebbleCount(random, 0.24, 0.04));
            yield break;
        }

        if (sourceBlock == BlockId.SapphireOre)
        {
            if (toolTier < HarvestToolTier.Iron)
                yield break;

            yield return new ItemDrop(ItemId.SapphireGem, 1);
            yield return new ItemDrop(ItemId.Pebbles, RollPebbleCount(random, 0.24, 0.04));
            yield break;
        }

        if (sourceBlock == BlockId.AmethystOre)
        {
            if (toolTier < HarvestToolTier.Iron)
                yield break;

            yield return new ItemDrop(ItemId.AmethystShard, random.NextDouble() < 0.25 ? 2 : 1);
            yield return new ItemDrop(ItemId.Pebbles, RollPebbleCount(random, 0.24, 0.04));
            yield break;
        }

        if (sourceBlock is BlockId.BasicKilnLit or BlockId.AdvancedKilnLit or BlockId.FieldOvenLit)
        {
            yield return new ItemDrop(sourceBlock switch
            {
                BlockId.AdvancedKilnLit => ItemId.AdvancedKiln,
                BlockId.FieldOvenLit => ItemId.FieldOven,
                _ => ItemId.BasicKiln
            }, 1);
            yield break;
        }

        if (!CanHarvestByHand(sourceBlock))
            yield break;

        var defaultDrop = ItemRegistry.FromLegacyBlockId(sourceBlock);
        if (defaultDrop != ItemId.None && ItemRegistry.IsKnown(defaultDrop))
            yield return new ItemDrop(defaultDrop, 1);
    }

    private static bool CanHarvestByHand(BlockId sourceBlock)
    {
        return sourceBlock is BlockId.Dirt
            or BlockId.Sand
            or BlockId.Gravel
            or BlockId.OakLog
            or BlockId.OakPlanks
            or BlockId.PlantFiber
            or BlockId.Stick
            or BlockId.Torch
            or BlockId.FiberWrappedTorch
            or BlockId.ArtificerBench
            or BlockId.BasicKiln
            or BlockId.AdvancedKiln
            or BlockId.FieldOven
            or BlockId.BasicKilnLit
            or BlockId.AdvancedKilnLit
            or BlockId.FieldOvenLit;
    }

    private static int RollPebbleCount(Random random, double secondChance, double thirdChance)
    {
        var count = 1;
        if (random.NextDouble() < secondChance)
            count++;
        if (random.NextDouble() < thirdChance)
            count++;
        return count;
    }
}
