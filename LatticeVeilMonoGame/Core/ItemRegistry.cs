using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LatticeVeilMonoGame.Core;

public enum ItemInventoryModelKind
{
    BlockIcon = 0,
    FlatSprite = 1,
    CustomModel = 2
}

public enum ItemHandModelKind
{
    None = 0,
    Block = 1,
    FlatSprite = 2,
    Tool = 3,
    CustomModel = 4
}

public enum LightMode
{
    None = 0,
    InHand = 1,
    InWorld = 2,
    Both = 3
}

[Flags]
public enum ItemTag
{
    None = 0,
    Block = 1 << 0,
    Food = 1 << 1,
    Tool = 1 << 2,
    Weapon = 1 << 3,
    Wood = 1 << 4,
    WoodLog = 1 << 5,
    WoodPlank = 1 << 6,
    Ore = 1 << 7,
    Mineral = 1 << 8,
    Mattock = 1 << 9,
    Hatchet = 1 << 10,
    Spade = 1 << 11,
    Blade = 1 << 12,
    Plow = 1 << 13,
    Javelin = 1 << 14,
    Potion = 1 << 15,
    Plant = 1 << 16,
    LightSource = 1 << 17,
    CraftingMaterial = 1 << 18,
    Container = 1 << 19,
    Fuel = 1 << 20,
    Bucket = 1 << 21,
    Consumable = Food | Potion,
    Wearable = 1 << 22
}

public enum ItemMaterialType
{
    None = 0,
    Wood = 1,
    Stone = 2,
    Iron = 3,
    Gold = 4,
    Diamond = 5,
    Copper = 6
}

public sealed class ItemDef
{
    private readonly HashSet<string> _stringTags = new(StringComparer.OrdinalIgnoreCase);

    // Light metadata
    public float LightLevel { get; set; } = 0f;
    public LightMode LightMode { get; set; } = LightMode.None;
    public bool IsWearable { get; set; } = false;

    public ItemDef(
        ItemId id,
        string name,
        int maxStack = 60,
        BlockId? placesBlock = null,
        string? textureName = null,
        bool visibleInCatalog = true,
        ItemInventoryModelKind inventoryModel = ItemInventoryModelKind.BlockIcon,
        ItemHandModelKind handModel = ItemHandModelKind.Block,
        string? key = null,
        ItemMaterialType materialType = ItemMaterialType.None,
        ItemTag tags = ItemTag.None,
        LootRegistry.HarvestToolTier toolTier = LootRegistry.HarvestToolTier.None,
        bool isFood = false,
        int hungerRestore = 0,
        int healthRestore = 0,
        IEnumerable<string>? customTags = null,
        string? itemType = null,
        float lightLevel = 0f,
        LightMode lightMode = LightMode.None,
        bool isWearable = false)
    {
        Id = id;
        Key = ItemRegistry.NormalizeKey(string.IsNullOrWhiteSpace(key)
            ? ItemRegistry.GetDefaultBaseKey(id)
            : key);
        Name = name;
        MaxStack = Math.Max(1, maxStack);
        PlacesBlock = placesBlock;
        TextureName = textureName;
        IsVisibleInCatalog = visibleInCatalog;
        InventoryModel = inventoryModel;
        HandModel = handModel;
        MaterialType = materialType;
        ToolTier = toolTier;
        LightLevel = lightLevel;
        LightMode = lightMode;
        IsWearable = isWearable;

        // Populate initial custom tags
        if (customTags != null)
        {
            foreach (var tag in customTags)
            {
                if (!string.IsNullOrWhiteSpace(tag))
                    _stringTags.Add(tag.Trim().ToLowerInvariant());
            }
        }

        if (!string.IsNullOrWhiteSpace(itemType))
        {
            var cleanType = itemType.Trim().ToLowerInvariant();
            _stringTags.Add(cleanType);
            if (cleanType is "food" or "edible" or "consumable")
                tags |= ItemTag.Food;
            else if (cleanType is "potion" or "drink")
                tags |= ItemTag.Potion;
            else if (cleanType is "weapon" or "blade" or "sword" or "javelin" or "spear")
                tags |= ItemTag.Weapon;
            else if (cleanType is "tool" or "mattock" or "pickaxe" or "hatchet" or "axe" or "spade" or "shovel" or "plow" or "hoe")
                tags |= ItemTag.Tool;
        }

        // Automatic Tag & Flag Inferencing
        if (placesBlock.HasValue && placesBlock.Value != BlockId.Air)
        {
            tags |= ItemTag.Block;
            _stringTags.Add("block");
        }

        if (tags.HasFlag(ItemTag.WoodLog))
        {
            tags |= ItemTag.Wood | ItemTag.Fuel;
            _stringTags.Add("wood_log");
            _stringTags.Add("log");
            _stringTags.Add("wood");
            _stringTags.Add("fuel");
        }

        if (tags.HasFlag(ItemTag.WoodPlank))
        {
            tags |= ItemTag.Wood | ItemTag.Fuel;
            _stringTags.Add("wood_plank");
            _stringTags.Add("plank");
            _stringTags.Add("planks");
            _stringTags.Add("wood");
            _stringTags.Add("fuel");
        }

        if (tags.HasFlag(ItemTag.Ore))
        {
            _stringTags.Add("ore");
            _stringTags.Add("mineral");
        }

        if (tags.HasFlag(ItemTag.Mineral))
        {
            _stringTags.Add("mineral");
        }

        if (tags.HasFlag(ItemTag.Mattock))
        {
            tags |= ItemTag.Tool;
            _stringTags.Add("mattock");
            _stringTags.Add("pickaxe");
            _stringTags.Add("tool");
            _stringTags.Add("mining");
        }

        if (tags.HasFlag(ItemTag.Hatchet))
        {
            tags |= ItemTag.Tool | ItemTag.Weapon;
            _stringTags.Add("hatchet");
            _stringTags.Add("axe");
            _stringTags.Add("tool");
            _stringTags.Add("weapon");
            _stringTags.Add("chopping");
        }

        if (tags.HasFlag(ItemTag.Spade))
        {
            tags |= ItemTag.Tool;
            _stringTags.Add("spade");
            _stringTags.Add("shovel");
            _stringTags.Add("tool");
            _stringTags.Add("digging");
        }

        if (tags.HasFlag(ItemTag.Blade))
        {
            tags |= ItemTag.Tool | ItemTag.Weapon;
            _stringTags.Add("blade");
            _stringTags.Add("sword");
            _stringTags.Add("tool");
            _stringTags.Add("weapon");
            _stringTags.Add("melee");
        }

        if (tags.HasFlag(ItemTag.Plow))
        {
            tags |= ItemTag.Tool;
            _stringTags.Add("plow");
            _stringTags.Add("hoe");
            _stringTags.Add("tool");
            _stringTags.Add("farming");
        }

        if (tags.HasFlag(ItemTag.Javelin))
        {
            tags |= ItemTag.Tool | ItemTag.Weapon;
            _stringTags.Add("javelin");
            _stringTags.Add("spear");
            _stringTags.Add("tool");
            _stringTags.Add("weapon");
            _stringTags.Add("ranged");
            _stringTags.Add("thrown");
        }

        if (tags.HasFlag(ItemTag.Potion))
        {
            _stringTags.Add("potion");
            _stringTags.Add("consumable");
            _stringTags.Add("drink");
        }

        if (tags.HasFlag(ItemTag.Plant))
        {
            _stringTags.Add("plant");
            _stringTags.Add("flora");
        }

        if (tags.HasFlag(ItemTag.LightSource))
        {
            _stringTags.Add("light");
            _stringTags.Add("torch");
        }

        if (tags.HasFlag(ItemTag.CraftingMaterial))
        {
            _stringTags.Add("material");
            _stringTags.Add("crafting");
        }

        if (tags.HasFlag(ItemTag.Fuel))
        {
            _stringTags.Add("fuel");
        }

        if (tags.HasFlag(ItemTag.Wearable))
        {
            _stringTags.Add("wearable");
            IsWearable = true;
        }

        if (tags.HasFlag(ItemTag.Bucket))
        {
            tags |= ItemTag.Tool;
            _stringTags.Add("bucket");
            _stringTags.Add("tool");
        }

        // --- AUTOMATIC FOOD DETECTION & DEFAULTS ---
        // If it has Food tag or string tag "food" / "edible" / "consumable", it becomes edible!
        var hasFoodTag = tags.HasFlag(ItemTag.Food) 
            || _stringTags.Contains("food") 
            || _stringTags.Contains("edible") 
            || _stringTags.Contains("consumable");

        if (isFood || hasFoodTag)
        {
            tags |= ItemTag.Food;
            _stringTags.Add("food");
            _stringTags.Add("edible");
            _stringTags.Add("consumable");
            IsFood = true;
            // Provide sensible baseline nutrition if none specified so custom food is instantly functional
            HungerRestore = hungerRestore > 0 ? hungerRestore : (healthRestore > 0 ? 0 : 3);
            HealthRestore = healthRestore;
        }
        else
        {
            IsFood = false;
            HungerRestore = hungerRestore;
            HealthRestore = healthRestore;
        }

        Tags = tags;
    }

    public ItemId Id { get; }
    public string Key { get; }
    public string Name { get; }
    public int MaxStack { get; }
    public BlockId? PlacesBlock { get; }
    public string? TextureName { get; }
    public bool IsVisibleInCatalog { get; }
    public ItemInventoryModelKind InventoryModel { get; }
    public ItemHandModelKind HandModel { get; }
    public ItemMaterialType MaterialType { get; }
    public ItemTag Tags { get; }
    public IReadOnlySet<string> StringTags => _stringTags;
    public LootRegistry.HarvestToolTier ToolTier { get; }
    public bool IsFood { get; }
    public int HungerRestore { get; }
    public int HealthRestore { get; }
    public bool CanPlaceBlock => PlacesBlock.HasValue && PlacesBlock.Value != BlockId.Air;

    public bool HasTag(ItemTag tag) => tag != ItemTag.None && (Tags & tag) == tag;

    public bool HasTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        var clean = tag.Trim().ToLowerInvariant();
        if (clean.StartsWith("tag:", StringComparison.Ordinal))
            clean = clean[4..];

        if (_stringTags.Contains(clean))
            return true;

        return clean switch
        {
            "food" or "edible" => IsFood || HasTag(ItemTag.Food),
            "tool" or "tools" => HasTag(ItemTag.Tool) || ToolTier != LootRegistry.HarvestToolTier.None,
            "weapon" or "weapons" => HasTag(ItemTag.Weapon),
            "wood" => HasTag(ItemTag.Wood) || MaterialType == ItemMaterialType.Wood,
            "log" or "logs" => HasTag(ItemTag.WoodLog),
            "plank" or "planks" => HasTag(ItemTag.WoodPlank),
            "ore" or "ores" => HasTag(ItemTag.Ore),
            "mineral" or "minerals" => HasTag(ItemTag.Mineral),
            "mattock" or "pickaxe" => HasTag(ItemTag.Mattock),
            "hatchet" or "axe" => HasTag(ItemTag.Hatchet),
            "spade" or "shovel" => HasTag(ItemTag.Spade),
            "blade" or "sword" => HasTag(ItemTag.Blade),
            "plow" or "hoe" => HasTag(ItemTag.Plow),
            "javelin" or "spear" => HasTag(ItemTag.Javelin),
            "potion" or "potions" or "drink" => HasTag(ItemTag.Potion),
            "block" or "blocks" => HasTag(ItemTag.Block) || CanPlaceBlock,
            "fuel" => HasTag(ItemTag.Fuel),
            "plant" or "flora" => HasTag(ItemTag.Plant),
            "light" or "torch" => HasTag(ItemTag.LightSource),
            "material" or "crafting" => HasTag(ItemTag.CraftingMaterial),
            _ => false
        };
    }
}

public static class ItemRegistry
{
    public const string BaseNamespace = "lv";

    private static readonly ItemDef?[] Defs = new ItemDef?[256];
    private static readonly Dictionary<string, ItemDef> DefsByKey = new(StringComparer.Ordinal);
    private static readonly List<ItemDef> AllDefs = new();

    public static readonly ItemDef Empty = new(
        ItemId.None,
        "Air",
        maxStack: 0,
        visibleInCatalog: false,
        key: "lv:none");

    static ItemRegistry()
    {
        // --- BASE BUILDING BLOCKS ---
        RegisterBlockItem(ItemId.GrassBlock, BlockId.Grass, ItemTag.Block);
        RegisterBlockItem(ItemId.DirtBlock, BlockId.Dirt, ItemTag.Block);
        RegisterBlockItem(ItemId.StoneBlock, BlockId.Stone, ItemTag.Block);
        RegisterBlockItem(ItemId.SandBlock, BlockId.Sand, ItemTag.Block);
        RegisterBlockItem(ItemId.OakLog, BlockId.OakLog, ItemTag.Block | ItemTag.WoodLog, ItemMaterialType.Wood);
        RegisterBlockItem(ItemId.LeavesBlock, BlockId.Leaves, ItemTag.Block | ItemTag.Plant);
        RegisterBlockItem(ItemId.Chest, BlockId.Chest, ItemTag.Block | ItemTag.Container);
        RegisterBlockItem(ItemId.CoalOreBlock, BlockId.CoalOre, ItemTag.Block | ItemTag.Ore);
        RegisterBlockItem(ItemId.IronOreBlock, BlockId.IronOre, ItemTag.Block | ItemTag.Ore);
        RegisterBlockItem(ItemId.ArtificerBench, BlockId.ArtificerBench, ItemTag.Block | ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.GlassBlock, BlockId.Glass, ItemTag.Block);
        RegisterBlockItem(ItemId.NullrockBlock, BlockId.Nullrock, ItemTag.Block);
        RegisterBlockItem(ItemId.GravelBlock, BlockId.Gravel, ItemTag.Block);
        RegisterBlockItem(ItemId.OakPlanks, BlockId.OakPlanks, ItemTag.Block | ItemTag.WoodPlank, ItemMaterialType.Wood);
        RegisterBlockItem(ItemId.GoldOreBlock, BlockId.GoldOre, ItemTag.Block | ItemTag.Ore);
        RegisterBlockItem(ItemId.DiamondBlock, BlockId.Diamond, ItemTag.Block | ItemTag.Ore);
        RegisterBlockItem(ItemId.CopperOreBlock, BlockId.CopperOre, ItemTag.Block | ItemTag.Ore);
        RegisterBlockItem(ItemId.EmeraldOreBlock, BlockId.EmeraldOre, ItemTag.Block | ItemTag.Ore);
        RegisterBlockItem(ItemId.RubyOreBlock, BlockId.RubyOre, ItemTag.Block | ItemTag.Ore);
        RegisterBlockItem(ItemId.SapphireOreBlock, BlockId.SapphireOre, ItemTag.Block | ItemTag.Ore);
        RegisterBlockItem(ItemId.AmethystOreBlock, BlockId.AmethystOre, ItemTag.Block | ItemTag.Ore);
        RegisterBlockItem(ItemId.IronBlock, BlockId.IronBlock, ItemTag.Block | ItemTag.Mineral);
        RegisterBlockItem(ItemId.GoldBlock, BlockId.GoldBlock, ItemTag.Block | ItemTag.Mineral);
        RegisterBlockItem(ItemId.DiamondBuildingBlock, BlockId.DiamondBlock, ItemTag.Block | ItemTag.Mineral);
        RegisterBlockItem(ItemId.BasicKiln, BlockId.BasicKiln, ItemTag.Block | ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.AdvancedKiln, BlockId.AdvancedKiln, ItemTag.Block | ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.FieldOven, BlockId.FieldOven, ItemTag.Block | ItemTag.CraftingMaterial);

        // --- RUNIC & ARCANE BLOCKS / ITEMS ---
        RegisterBlockItem(ItemId.Runestone, BlockId.Runestone, ItemTag.Block | ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.Veinstone, BlockId.Veinstone, ItemTag.Block | ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.Veilglass, BlockId.Veilglass, ItemTag.Block);
        RegisterBlockItem(ItemId.Gravestone, BlockId.Gravestone, ItemTag.Block);
        RegisterBlockItem(ItemId.InscribedTablet, BlockId.InscribedTablet, ItemTag.Block | ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.FragmentScrap, BlockId.FragmentScrap, ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.VeilSealStone, BlockId.VeilSealStone, ItemTag.Block | ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.GraveSilt, BlockId.GraveSilt, ItemTag.Block);
        RegisterBlockItem(ItemId.RunestoneDust, BlockId.RunestoneDust, ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.VeinstoneCrystal, BlockId.VeinstoneCrystal, ItemTag.CraftingMaterial | ItemTag.Mineral);
        RegisterBlockItem(ItemId.AnchoringSalt, BlockId.AnchoringSalt, ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.StabilizedAsh, BlockId.StabilizedAsh, ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.EchoIconShard, BlockId.EchoIconShard, ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.CopperWiring, BlockId.CopperWiring, ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.LimiterSigil, BlockId.LimiterSigil, ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.AlignmentMatrixFragment, BlockId.AlignmentMatrixFragment, ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.AttunedKeystoneFragment, BlockId.AttunedKeystoneFragment, ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.RegulatorComponent, BlockId.RegulatorComponent, ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.TransitRegulatorPart, BlockId.TransitRegulatorPart, ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.WayfinderPlinthPart, BlockId.WayfinderPlinthPart, ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.ResonanceCore, BlockId.ResonanceCore, ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.AxiomFulgrite, BlockId.AxiomFulgrite, ItemTag.CraftingMaterial | ItemTag.Mineral);
        RegisterBlockItem(ItemId.AnchoringAlloy, BlockId.AnchoringAlloy, ItemTag.CraftingMaterial | ItemTag.Mineral);
        RegisterBlockItem(ItemId.SigilLoom, BlockId.SigilLoom, ItemTag.Block | ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.QuietAlembic, BlockId.QuietAlembic, ItemTag.Block | ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.RunicAnvil, BlockId.RunicAnvil, ItemTag.Block | ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.CinderbranchStaff, BlockId.CinderbranchStaff, ItemTag.Weapon | ItemTag.Tool);
        RegisterBlockItem(ItemId.StormreedStaff, BlockId.StormreedStaff, ItemTag.Weapon | ItemTag.Tool);
        RegisterBlockItem(ItemId.WayboundFrame, BlockId.WayboundFrame, ItemTag.Block | ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.AttunedKeystone, BlockId.AttunedKeystone, ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.TimedLimiter, BlockId.TimedLimiter, ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.TransitRegulator, BlockId.TransitRegulator, ItemTag.Block | ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.WaygatePlinth, BlockId.WaygatePlinth, ItemTag.Block | ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.WaygateRune, BlockId.WaygateRune, ItemTag.Block | ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.EvergateCore, BlockId.EvergateCore, ItemTag.Block | ItemTag.CraftingMaterial);
        RegisterBlockItem(ItemId.EvergateCoreUnstable, BlockId.EvergateCoreUnstable, ItemTag.Block | ItemTag.CraftingMaterial);

        // --- POTIONS & ELIXIRS ---
        Register(new ItemDef(
            ItemId.CleanPhial,
            "Clean Phial",
            maxStack: 20,
            textureName: "clean_phial",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.CraftingMaterial | ItemTag.Container,
            key: "LV:clean_phial"));

        Register(new ItemDef(
            ItemId.FleetstepDraught,
            "Fleetstep Draught",
            maxStack: 10,
            textureName: "fleetstep_draught",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.Potion | ItemTag.Consumable,
            hungerRestore: 3,
            healthRestore: 1,
            key: "LV:fleetstep_draught"));

        Register(new ItemDef(
            ItemId.SkyboundPhilter,
            "Skybound Philter",
            maxStack: 10,
            textureName: "skybound_philter",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.Potion | ItemTag.Consumable,
            hungerRestore: 2,
            healthRestore: 2,
            key: "LV:skybound_philter"));

        Register(new ItemDef(
            ItemId.PyroskinTonic,
            "Pyroskin Tonic",
            maxStack: 10,
            textureName: "pyroskin_tonic",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.Potion | ItemTag.Consumable,
            hungerRestore: 1,
            healthRestore: 3,
            key: "LV:pyroskin_tonic"));

        Register(new ItemDef(
            ItemId.BrineveilElixir,
            "Brineveil Elixir",
            maxStack: 10,
            textureName: "brineveil_elixir",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.Potion | ItemTag.Consumable,
            hungerRestore: 0,
            healthRestore: 5,
            key: "LV:brineveil_elixir"));

        // --- ITEMS & MATERIALS ---
        Register(new ItemDef(
            ItemId.EmptyBucket,
            "Empty Bucket",
            maxStack: 1,
            textureName: "empty_bucket",
            inventoryModel: ItemInventoryModelKind.CustomModel,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Bucket | ItemTag.Tool,
            key: "LV:empty_bucket"));

        Register(new ItemDef(
            ItemId.WaterBucket,
            "Water Bucket",
            maxStack: 1,
            textureName: "water_bucket",
            inventoryModel: ItemInventoryModelKind.CustomModel,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Bucket | ItemTag.Tool,
            key: "LV:water_bucket"));

        Register(new ItemDef(
            ItemId.PlantFiber,
            "Plant Fiber",
            maxStack: 20,
            textureName: "plant_fiber",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.CraftingMaterial | ItemTag.Plant | ItemTag.Fuel,
            key: "LV:plant_fiber"));

        Register(new ItemDef(
            ItemId.CoalChunk,
            "Coal Chunk",
            maxStack: 60,
            textureName: "coal_chunk",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.CraftingMaterial | ItemTag.Fuel | ItemTag.Mineral,
            key: "LV:coal_chunk"));

        Register(new ItemDef(
            ItemId.Stick,
            "Stick",
            maxStack: 60,
            textureName: "stick",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.CraftingMaterial | ItemTag.Wood | ItemTag.Fuel,
            materialType: ItemMaterialType.Wood,
            key: "LV:stick"));

        var torchDef = new ItemDef(
            ItemId.Torch,
            "Torch",
            maxStack: 60,
            placesBlock: BlockId.Torch,
            textureName: "torch",
            inventoryModel: ItemInventoryModelKind.CustomModel,
            handModel: ItemHandModelKind.CustomModel,
            tags: ItemTag.Block | ItemTag.LightSource,
            key: "LV:torch");
        torchDef.LightLevel = 14f;
        torchDef.LightMode = LightMode.Both;
        Register(torchDef);
        System.Diagnostics.Debug.WriteLine($"[LIGHTING DIAGNOSTIC] ItemRegistry: Torch registered - LightLevel={torchDef.LightLevel}, LightMode={torchDef.LightMode}, HasLightSourceTag={torchDef.HasTag(ItemTag.LightSource)}");

        var fiberTorchDef = new ItemDef(
            ItemId.FiberWrappedTorch,
            "Fiber-Wrapped Torch",
            maxStack: 60,
            placesBlock: BlockId.FiberWrappedTorch,
            textureName: "fiber_wrapped_torch",
            inventoryModel: ItemInventoryModelKind.CustomModel,
            handModel: ItemHandModelKind.CustomModel,
            tags: ItemTag.Block | ItemTag.LightSource,
            key: "LV:fiber_wrapped_torch");
        fiberTorchDef.LightLevel = 14f;
        fiberTorchDef.LightMode = LightMode.Both;
        Register(fiberTorchDef);
        System.Diagnostics.Debug.WriteLine($"[LIGHTING DIAGNOSTIC] ItemRegistry: FiberWrappedTorch registered - LightLevel={fiberTorchDef.LightLevel}, LightMode={fiberTorchDef.LightMode}, HasLightSourceTag={fiberTorchDef.HasTag(ItemTag.LightSource)}");

        Register(new ItemDef(
            ItemId.IronBillet,
            "Iron Billet",
            maxStack: 60,
            textureName: "iron_billet",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.CraftingMaterial | ItemTag.Mineral,
            materialType: ItemMaterialType.Iron,
            key: "LV:iron_billet"));

        Register(new ItemDef(
            ItemId.GoldBillet,
            "Gold Billet",
            maxStack: 60,
            textureName: "gold_billet",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.CraftingMaterial | ItemTag.Mineral,
            materialType: ItemMaterialType.Gold,
            key: "LV:gold_billet"));

        Register(new ItemDef(
            ItemId.DiamondGem,
            "Diamond",
            maxStack: 60,
            textureName: "diamond_gem",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.CraftingMaterial | ItemTag.Mineral,
            materialType: ItemMaterialType.Diamond,
            key: "LV:diamond"));

        Register(new ItemDef(
            ItemId.Pebbles,
            "Pebbles",
            maxStack: 60,
            textureName: "pebbles",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.CraftingMaterial | ItemTag.Weapon,
            key: "LV:pebbles"));

        Register(new ItemDef(
            ItemId.CopperCluster,
            "Copper Cluster",
            maxStack: 60,
            textureName: "copper_cluster",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.CraftingMaterial | ItemTag.Mineral,
            materialType: ItemMaterialType.Copper,
            key: "LV:copper_cluster"));

        Register(new ItemDef(
            ItemId.CopperBillet,
            "Copper Billet",
            maxStack: 60,
            textureName: "copper_billet",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.CraftingMaterial | ItemTag.Mineral,
            materialType: ItemMaterialType.Copper,
            key: "LV:copper_billet"));

        Register(new ItemDef(
            ItemId.IronCluster,
            "Iron Cluster",
            maxStack: 60,
            textureName: "iron_cluster",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.CraftingMaterial | ItemTag.Mineral,
            materialType: ItemMaterialType.Iron,
            key: "LV:iron_cluster"));

        Register(new ItemDef(
            ItemId.GoldCluster,
            "Gold Cluster",
            maxStack: 60,
            textureName: "gold_cluster",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.CraftingMaterial | ItemTag.Mineral,
            materialType: ItemMaterialType.Gold,
            key: "LV:gold_cluster"));

        Register(new ItemDef(
            ItemId.Emerald,
            "Emerald",
            maxStack: 60,
            textureName: "emerald_gem",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.CraftingMaterial | ItemTag.Mineral,
            key: "LV:emerald"));

        Register(new ItemDef(
            ItemId.RubyStone,
            "Ruby Stone",
            maxStack: 60,
            textureName: "ruby_stone",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.CraftingMaterial | ItemTag.Mineral,
            key: "LV:ruby_stone"));

        Register(new ItemDef(
            ItemId.SapphireGem,
            "Sapphire Gem",
            maxStack: 60,
            textureName: "sapphire_gem",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.CraftingMaterial | ItemTag.Mineral,
            key: "LV:sapphire_gem"));

        Register(new ItemDef(
            ItemId.AmethystShard,
            "Amethyst Shard",
            maxStack: 60,
            textureName: "amethyst_shard",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.CraftingMaterial | ItemTag.Mineral,
            key: "LV:amethyst_shard"));

        Register(new ItemDef(
            ItemId.Embermoss,
            "Embermoss",
            maxStack: 60,
            textureName: "embermoss",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.CraftingMaterial | ItemTag.Plant,
            key: "LV:embermoss"));

        // --- FOODS & CONSUMABLES ---
        Register(new ItemDef(
            ItemId.EchoBloom,
            "Echo Bloom",
            maxStack: 60,
            textureName: "echo_bloom",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.Food | ItemTag.Plant | ItemTag.Consumable,
            hungerRestore: 2,
            healthRestore: 1,
            key: "LV:echo_bloom"));

        Register(new ItemDef(
            ItemId.Swiftleaf,
            "Swiftleaf",
            maxStack: 60,
            textureName: "swiftleaf",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.Food | ItemTag.Plant | ItemTag.Consumable,
            hungerRestore: 3,
            healthRestore: 0,
            key: "LV:swiftleaf"));

        Register(new ItemDef(
            ItemId.Driftcap,
            "Driftcap",
            maxStack: 60,
            textureName: "driftcap",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.Food | ItemTag.Plant | ItemTag.Consumable,
            hungerRestore: 4,
            healthRestore: 0,
            key: "LV:driftcap"));

        Register(new ItemDef(
            ItemId.ForestBerries,
            "Forest Berries",
            maxStack: 60,
            textureName: "forest_berries",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.Food | ItemTag.Plant | ItemTag.Consumable,
            hungerRestore: 2,
            healthRestore: 0,
            key: "LV:forest_berries"));

        Register(new ItemDef(
            ItemId.PlantRation,
            "Plant Ration",
            maxStack: 60,
            textureName: "plant_ration",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.Food | ItemTag.Consumable,
            hungerRestore: 4,
            healthRestore: 0,
            key: "LV:plant_ration"));

        Register(new ItemDef(
            ItemId.CookedRation,
            "Cooked Ration",
            maxStack: 60,
            textureName: "cooked_ration",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.FlatSprite,
            tags: ItemTag.Food | ItemTag.Consumable,
            hungerRestore: 8,
            healthRestore: 3,
            key: "LV:cooked_ration"));

        Register(new ItemDef(
            ItemId.SleepingBag,
            "Sleeping Bag",
            maxStack: 1,
            placesBlock: BlockId.SleepingBag,
            textureName: "sleeping_bag",
            inventoryModel: ItemInventoryModelKind.CustomModel,
            handModel: ItemHandModelKind.Block,
            tags: ItemTag.Block,
            key: "LV:sleeping_bag"));

        // --- MATTOCKS (Pickaxes) ---
        Register(new ItemDef(
            ItemId.WoodMattock,
            "Wood Mattock",
            maxStack: 1,
            textureName: "wood_mattock",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Tool | ItemTag.Mattock,
            materialType: ItemMaterialType.Wood,
            toolTier: LootRegistry.HarvestToolTier.Wood,
            key: "LV:wood_mattock"));

        Register(new ItemDef(
            ItemId.StoneMattock,
            "Stone Mattock",
            maxStack: 1,
            textureName: "stone_mattock",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Tool | ItemTag.Mattock,
            materialType: ItemMaterialType.Stone,
            toolTier: LootRegistry.HarvestToolTier.Stone,
            key: "LV:stone_mattock"));

        Register(new ItemDef(
            ItemId.IronMattock,
            "Iron Mattock",
            maxStack: 1,
            textureName: "iron_mattock",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Tool | ItemTag.Mattock,
            materialType: ItemMaterialType.Iron,
            toolTier: LootRegistry.HarvestToolTier.Iron,
            key: "LV:iron_mattock"));

        Register(new ItemDef(
            ItemId.DiamondMattock,
            "Diamond Mattock",
            maxStack: 1,
            textureName: "diamond_mattock",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Tool | ItemTag.Mattock,
            materialType: ItemMaterialType.Diamond,
            toolTier: LootRegistry.HarvestToolTier.Diamond,
            key: "LV:diamond_mattock"));

        // --- HATCHETS (Axes) ---
        Register(new ItemDef(
            ItemId.WoodHatchet,
            "Wood Hatchet",
            maxStack: 1,
            textureName: "wood_hatchet",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Tool | ItemTag.Weapon | ItemTag.Hatchet,
            materialType: ItemMaterialType.Wood,
            toolTier: LootRegistry.HarvestToolTier.Wood,
            key: "LV:wood_hatchet"));

        Register(new ItemDef(
            ItemId.StoneHatchet,
            "Stone Hatchet",
            maxStack: 1,
            textureName: "stone_hatchet",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Tool | ItemTag.Weapon | ItemTag.Hatchet,
            materialType: ItemMaterialType.Stone,
            toolTier: LootRegistry.HarvestToolTier.Stone,
            key: "LV:stone_hatchet"));

        Register(new ItemDef(
            ItemId.IronHatchet,
            "Iron Hatchet",
            maxStack: 1,
            textureName: "iron_hatchet",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Tool | ItemTag.Weapon | ItemTag.Hatchet,
            materialType: ItemMaterialType.Iron,
            toolTier: LootRegistry.HarvestToolTier.Iron,
            key: "LV:iron_hatchet"));

        Register(new ItemDef(
            ItemId.DiamondHatchet,
            "Diamond Hatchet",
            maxStack: 1,
            textureName: "diamond_hatchet",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Tool | ItemTag.Weapon | ItemTag.Hatchet,
            materialType: ItemMaterialType.Diamond,
            toolTier: LootRegistry.HarvestToolTier.Diamond,
            key: "LV:diamond_hatchet"));

        // --- SPADES (Shovels) ---
        Register(new ItemDef(
            ItemId.WoodSpade,
            "Wood Spade",
            maxStack: 1,
            textureName: "wood_spade",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Tool | ItemTag.Spade,
            materialType: ItemMaterialType.Wood,
            toolTier: LootRegistry.HarvestToolTier.Wood,
            key: "LV:wood_spade"));

        Register(new ItemDef(
            ItemId.StoneSpade,
            "Stone Spade",
            maxStack: 1,
            textureName: "stone_spade",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Tool | ItemTag.Spade,
            materialType: ItemMaterialType.Stone,
            toolTier: LootRegistry.HarvestToolTier.Stone,
            key: "LV:stone_spade"));

        Register(new ItemDef(
            ItemId.IronSpade,
            "Iron Spade",
            maxStack: 1,
            textureName: "iron_spade",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Tool | ItemTag.Spade,
            materialType: ItemMaterialType.Iron,
            toolTier: LootRegistry.HarvestToolTier.Iron,
            key: "LV:iron_spade"));

        Register(new ItemDef(
            ItemId.DiamondSpade,
            "Diamond Spade",
            maxStack: 1,
            textureName: "diamond_spade",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Tool | ItemTag.Spade,
            materialType: ItemMaterialType.Diamond,
            toolTier: LootRegistry.HarvestToolTier.Diamond,
            key: "LV:diamond_spade"));

        // --- BLADES (Swords) ---
        Register(new ItemDef(
            ItemId.WoodBlade,
            "Wood Blade",
            maxStack: 1,
            textureName: "wood_blade",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Tool | ItemTag.Weapon | ItemTag.Blade,
            materialType: ItemMaterialType.Wood,
            toolTier: LootRegistry.HarvestToolTier.Wood,
            key: "LV:wood_blade"));

        Register(new ItemDef(
            ItemId.StoneBlade,
            "Stone Blade",
            maxStack: 1,
            textureName: "stone_blade",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Tool | ItemTag.Weapon | ItemTag.Blade,
            materialType: ItemMaterialType.Stone,
            toolTier: LootRegistry.HarvestToolTier.Stone,
            key: "LV:stone_blade"));

        Register(new ItemDef(
            ItemId.IronBlade,
            "Iron Blade",
            maxStack: 1,
            textureName: "iron_blade",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Tool | ItemTag.Weapon | ItemTag.Blade,
            materialType: ItemMaterialType.Iron,
            toolTier: LootRegistry.HarvestToolTier.Iron,
            key: "LV:iron_blade"));

        Register(new ItemDef(
            ItemId.DiamondBlade,
            "Diamond Blade",
            maxStack: 1,
            textureName: "diamond_blade",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Tool | ItemTag.Weapon | ItemTag.Blade,
            materialType: ItemMaterialType.Diamond,
            toolTier: LootRegistry.HarvestToolTier.Diamond,
            key: "LV:diamond_blade"));

        // --- PLOWS (Hoes) ---
        Register(new ItemDef(
            ItemId.WoodPlow,
            "Wood Plow",
            maxStack: 1,
            textureName: "wood_plow",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Tool | ItemTag.Plow,
            materialType: ItemMaterialType.Wood,
            toolTier: LootRegistry.HarvestToolTier.Wood,
            key: "LV:wood_plow"));

        Register(new ItemDef(
            ItemId.StonePlow,
            "Stone Plow",
            maxStack: 1,
            textureName: "stone_plow",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Tool | ItemTag.Plow,
            materialType: ItemMaterialType.Stone,
            toolTier: LootRegistry.HarvestToolTier.Stone,
            key: "LV:stone_plow"));

        Register(new ItemDef(
            ItemId.IronPlow,
            "Iron Plow",
            maxStack: 1,
            textureName: "iron_plow",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Tool | ItemTag.Plow,
            materialType: ItemMaterialType.Iron,
            toolTier: LootRegistry.HarvestToolTier.Iron,
            key: "LV:iron_plow"));

        Register(new ItemDef(
            ItemId.DiamondPlow,
            "Diamond Plow",
            maxStack: 1,
            textureName: "diamond_plow",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Tool | ItemTag.Plow,
            materialType: ItemMaterialType.Diamond,
            toolTier: LootRegistry.HarvestToolTier.Diamond,
            key: "LV:diamond_plow"));

        // --- JAVELINS (Spears) ---
        Register(new ItemDef(
            ItemId.WoodJavelin,
            "Wood Javelin",
            maxStack: 1,
            textureName: "wood_javelin",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Tool | ItemTag.Weapon | ItemTag.Javelin,
            materialType: ItemMaterialType.Wood,
            toolTier: LootRegistry.HarvestToolTier.Wood,
            key: "LV:wood_javelin"));

        Register(new ItemDef(
            ItemId.StoneJavelin,
            "Stone Javelin",
            maxStack: 1,
            textureName: "stone_javelin",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Tool | ItemTag.Weapon | ItemTag.Javelin,
            materialType: ItemMaterialType.Stone,
            toolTier: LootRegistry.HarvestToolTier.Stone,
            key: "LV:stone_javelin"));

        Register(new ItemDef(
            ItemId.IronJavelin,
            "Iron Javelin",
            maxStack: 1,
            textureName: "iron_javelin",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Tool | ItemTag.Weapon | ItemTag.Javelin,
            materialType: ItemMaterialType.Iron,
            toolTier: LootRegistry.HarvestToolTier.Iron,
            key: "LV:iron_javelin"));

        Register(new ItemDef(
            ItemId.DiamondJavelin,
            "Diamond Javelin",
            maxStack: 1,
            textureName: "diamond_javelin",
            inventoryModel: ItemInventoryModelKind.FlatSprite,
            handModel: ItemHandModelKind.Tool,
            tags: ItemTag.Tool | ItemTag.Weapon | ItemTag.Javelin,
            materialType: ItemMaterialType.Diamond,
            toolTier: LootRegistry.HarvestToolTier.Diamond,
            key: "LV:diamond_javelin"));

        AllDefs.Sort((a, b) => ((byte)a.Id).CompareTo((byte)b.Id));
    }

    private static void RegisterBlockItem(
        ItemId itemId,
        BlockId blockId,
        ItemTag tags = ItemTag.Block,
        ItemMaterialType materialType = ItemMaterialType.None)
    {
        var block = BlockRegistry.Get(blockId);
        Register(new ItemDef(
            itemId,
            block.Name,
            placesBlock: blockId,
            textureName: block.TextureName,
            visibleInCatalog: block.IsVisibleInInventory,
            inventoryModel: ItemInventoryModelKind.BlockIcon,
            handModel: ItemHandModelKind.Block,
            key: GetDefaultBaseKey(itemId),
            materialType: materialType,
            tags: tags | ItemTag.Block));
    }

    public static void Register(ItemDef def)
    {
        var idx = (byte)def.Id;
        if (Defs[idx] != null)
            throw new InvalidOperationException($"Duplicate ItemId {idx} ({def.Id}).");
        if (DefsByKey.ContainsKey(def.Key))
            throw new InvalidOperationException($"Duplicate item key '{def.Key}'.");

        Defs[idx] = def;
        DefsByKey[def.Key] = def;
        AllDefs.Add(def);
    }

    public static ItemDef Get(ItemId id) => Defs[(byte)id] ?? Empty;

    public static ItemDef Get(byte id) => Defs[id] ?? Empty;

    public static ItemDef Get(string key) => TryGet(key, out var def) ? def : Empty;

    public static bool TryGet(string key, out ItemDef def)
    {
        if (TryNormalizeKey(key, out var normalized) && DefsByKey.TryGetValue(normalized, out var found))
        {
            def = found;
            return true;
        }

        def = Empty;
        return false;
    }

    public static IReadOnlyList<ItemDef> All => AllDefs;

    public static bool TryGetPlaceBlock(ItemId id, out BlockId blockId)
    {
        var def = Get(id);
        if (def.PlacesBlock.HasValue && def.PlacesBlock.Value != BlockId.Air)
        {
            blockId = def.PlacesBlock.Value;
            return true;
        }

        blockId = BlockId.Air;
        return false;
    }

    public static ItemId FromLegacyBlockId(BlockId id) => (ItemId)(byte)id;

    public static BlockId ToLegacyBlockId(ItemId id) => (BlockId)(byte)id;

    public static bool IsKnown(ItemId id) => Defs[(byte)id] != null;

    public static bool IsKnown(string key) => TryGet(key, out var def) && def.Id != ItemId.None;

    public static bool HasTag(ItemId id, ItemTag tag) => Get(id).HasTag(tag);

    public static bool HasTag(ItemId id, string tag) => Get(id).HasTag(tag);

    public static IEnumerable<ItemDef> GetByTag(ItemTag tag) => AllDefs.Where(d => d.HasTag(tag));

    public static IEnumerable<ItemDef> GetByTag(string tag) => AllDefs.Where(d => d.HasTag(tag));

    public static LootRegistry.HarvestToolTier GetHarvestToolTier(ItemId id) => Get(id).ToolTier;

    public static bool IsTool(ItemId id) => Get(id).HasTag(ItemTag.Tool) || Get(id).ToolTier != LootRegistry.HarvestToolTier.None;

    public static bool IsFood(ItemId id) => Get(id).IsFood || Get(id).HasTag(ItemTag.Food);

    public static bool MatchesCategoryOrTag(ItemId id, string token) => Get(id).HasTag(token);

    public static bool IsMattock(ItemId id) => Get(id).HasTag(ItemTag.Mattock) || id is ItemId.WoodMattock or ItemId.StoneMattock or ItemId.IronMattock or ItemId.DiamondMattock;

    public static bool IsPickaxe(ItemId id) => IsMattock(id);

    public static bool IsHatchet(ItemId id) => Get(id).HasTag(ItemTag.Hatchet) || id is ItemId.WoodHatchet or ItemId.StoneHatchet or ItemId.IronHatchet or ItemId.DiamondHatchet;

    public static bool IsAxe(ItemId id) => IsHatchet(id);

    public static bool IsSpade(ItemId id) => Get(id).HasTag(ItemTag.Spade) || id is ItemId.WoodSpade or ItemId.StoneSpade or ItemId.IronSpade or ItemId.DiamondSpade;

    public static bool IsShovel(ItemId id) => IsSpade(id);

    public static bool IsBlade(ItemId id) => Get(id).HasTag(ItemTag.Blade) || id is ItemId.WoodBlade or ItemId.StoneBlade or ItemId.IronBlade or ItemId.DiamondBlade;

    public static bool IsSword(ItemId id) => IsBlade(id);

    public static bool IsPlow(ItemId id) => Get(id).HasTag(ItemTag.Plow) || id is ItemId.WoodPlow or ItemId.StonePlow or ItemId.IronPlow or ItemId.DiamondPlow;

    public static bool IsHoe(ItemId id) => IsPlow(id);

    public static bool IsJavelin(ItemId id) => Get(id).HasTag(ItemTag.Javelin) || id is ItemId.WoodJavelin or ItemId.StoneJavelin or ItemId.IronJavelin or ItemId.DiamondJavelin;

    public static bool IsSpear(ItemId id) => IsJavelin(id);

    public static int GetWeaponMeleeDamage(ItemId id)
    {
        var tier = GetHarvestToolTier(id);
        if (IsBlade(id))
        {
            return tier switch
            {
                LootRegistry.HarvestToolTier.Wood => 4,
                LootRegistry.HarvestToolTier.Stone => 5,
                LootRegistry.HarvestToolTier.Iron => 6,
                LootRegistry.HarvestToolTier.Diamond => 8,
                _ => 4
            };
        }

        if (IsJavelin(id))
        {
            return tier switch
            {
                LootRegistry.HarvestToolTier.Wood => 2,
                LootRegistry.HarvestToolTier.Stone => 3,
                LootRegistry.HarvestToolTier.Iron => 4,
                LootRegistry.HarvestToolTier.Diamond => 5,
                _ => 2
            };
        }

        if (IsHatchet(id))
        {
            return tier switch
            {
                LootRegistry.HarvestToolTier.Wood => 3,
                LootRegistry.HarvestToolTier.Stone => 4,
                LootRegistry.HarvestToolTier.Iron => 5,
                LootRegistry.HarvestToolTier.Diamond => 7,
                _ => 3
            };
        }

        if (IsMattock(id))
        {
            return tier switch
            {
                LootRegistry.HarvestToolTier.Wood => 2,
                LootRegistry.HarvestToolTier.Stone => 3,
                LootRegistry.HarvestToolTier.Iron => 4,
                LootRegistry.HarvestToolTier.Diamond => 5,
                _ => 2
            };
        }

        return 1;
    }

    public static float GetToolSpeedMultiplier(ItemId id, BlockId targetBlock)
    {
        var tier = GetHarvestToolTier(id);
        if (tier == LootRegistry.HarvestToolTier.None)
            return 1.0f;

        var baseMultiplier = tier switch
        {
            LootRegistry.HarvestToolTier.Wood => 2.0f,
            LootRegistry.HarvestToolTier.Stone => 4.0f,
            LootRegistry.HarvestToolTier.Iron => 6.0f,
            LootRegistry.HarvestToolTier.Diamond => 8.0f,
            _ => 1.0f
        };

        if (IsMattock(id) && IsMattockEffective(targetBlock))
            return baseMultiplier;
        if (IsHatchet(id) && IsHatchetEffective(targetBlock))
            return baseMultiplier;
        if (IsSpade(id) && IsSpadeEffective(targetBlock))
            return baseMultiplier;
        if (IsBlade(id) && targetBlock is BlockId.Leaves or BlockId.EchoBloom or BlockId.Swiftleaf)
            return baseMultiplier * 1.5f;

        return 1.0f;
    }

    public static bool IsMattockEffective(BlockId blockId) =>
        blockId is BlockId.Stone
            or BlockId.CoalOre
            or BlockId.IronOre
            or BlockId.GoldOre
            or BlockId.Diamond
            or BlockId.CopperOre
            or BlockId.EmeraldOre
            or BlockId.RubyOre
            or BlockId.SapphireOre
            or BlockId.AmethystOre
            or BlockId.IronBlock
            or BlockId.GoldBlock
            or BlockId.DiamondBlock
            or BlockId.Nullrock
            or BlockId.Runestone
            or BlockId.Veinstone;

    public static bool IsPickaxeEffective(BlockId blockId) => IsMattockEffective(blockId);

    public static bool IsHatchetEffective(BlockId blockId) =>
        blockId is BlockId.OakLog
            or BlockId.OakPlanks
            or BlockId.Chest
            or BlockId.ArtificerBench
            or BlockId.BasicKiln
            or BlockId.AdvancedKiln
            or BlockId.FieldOven;

    public static bool IsAxeEffective(BlockId blockId) => IsHatchetEffective(blockId);

    public static bool IsSpadeEffective(BlockId blockId) =>
        blockId is BlockId.Dirt
            or BlockId.Grass
            or BlockId.Sand
            or BlockId.Gravel
            or BlockId.GraveSilt;

    public static bool IsShovelEffective(BlockId blockId) => IsSpadeEffective(blockId);

    public static bool TryGetConsumable(ItemId id, out int hungerRestore, out int healthRestore)
    {
        var def = Get(id);
        if (def.IsFood || def.HasTag("food") || def.HasTag("edible") || def.HasTag("potion"))
        {
            hungerRestore = def.HungerRestore;
            healthRestore = def.HealthRestore;
            return true;
        }

        hungerRestore = 0;
        healthRestore = 0;
        return false;
    }

    public static string GetDefaultBaseKey(ItemId id)
    {
        if (id == ItemId.None)
            return $"{BaseNamespace}:none";

        return $"{BaseNamespace}:{ToSnakeCase(id.ToString())}";
    }

    public static string NormalizeKey(string key)
    {
        if (!TryNormalizeKey(key, out var normalized))
            throw new ArgumentException($"Invalid item key '{key}'. Expected namespace:path, such as LV:coal_chunk.", nameof(key));

        return normalized;
    }

    public static bool TryNormalizeKey(string key, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var trimmed = key.Trim();
        var colon = trimmed.IndexOf(':');
        if (colon <= 0 || colon != trimmed.LastIndexOf(':') || colon >= trimmed.Length - 1)
            return false;

        var ns = trimmed[..colon].Trim().ToLowerInvariant();
        var path = trimmed[(colon + 1)..].Trim().ToLowerInvariant();
        if (!IsValidKeyPart(ns, allowSlash: false) || !IsValidKeyPart(path, allowSlash: true))
            return false;

        normalized = $"{ns}:{path}";
        return true;
    }

    private static bool IsValidKeyPart(string value, bool allowSlash)
    {
        if (value.Length == 0)
            return false;

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is >= 'a' and <= 'z'
                || c is >= '0' and <= '9'
                || c == '_'
                || c == '-'
                || (allowSlash && c == '/'))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static string ToSnakeCase(string str)
    {
        if (string.IsNullOrEmpty(str))
            return str;

        var result = new List<char>(str.Length + 4);
        for (var i = 0; i < str.Length; i++)
        {
            var c = str[i];
            if (char.IsUpper(c))
            {
                if (i > 0 && str[i - 1] != '_')
                    result.Add('_');
                result.Add(char.ToLowerInvariant(c));
            }
            else
            {
                result.Add(c);
            }
        }

        return new string(result.ToArray());
    }

    // --- JSON Item Loader for Modding & Data-driven content ---
    public static bool TryRegisterFromJson(string jsonText, byte dynamicItemId, out ItemDef? createdDef, Logger? log = null)
    {
        createdDef = null;
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            var idStr = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            var name = root.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : "Custom Item";
            var itemType = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
            var maxStack = root.TryGetProperty("max_stack", out var stackProp) ? stackProp.GetInt32() : 60;
            var textureName = root.TryGetProperty("texture", out var texProp) ? texProp.GetString() : null;
            var hunger = root.TryGetProperty("hunger_restore", out var hProp) ? hProp.GetInt32() : 0;
            var health = root.TryGetProperty("health_restore", out var hpProp) ? hpProp.GetInt32() : 0;
            var key = root.TryGetProperty("key", out var keyProp) ? keyProp.GetString() : (!string.IsNullOrWhiteSpace(idStr) ? $"mod:{idStr}" : null);

            var tagsList = new List<string>();
            if (root.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var tagEl in tagsProp.EnumerateArray())
                {
                    var t = tagEl.GetString();
                    if (!string.IsNullOrWhiteSpace(t))
                        tagsList.Add(t);
                }
            }

            var toolTier = LootRegistry.HarvestToolTier.None;
            if (root.TryGetProperty("tool_tier", out var tierProp))
            {
                var tierStr = tierProp.GetString()?.ToLowerInvariant();
                toolTier = tierStr switch
                {
                    "wood" => LootRegistry.HarvestToolTier.Wood,
                    "stone" => LootRegistry.HarvestToolTier.Stone,
                    "iron" => LootRegistry.HarvestToolTier.Iron,
                    "diamond" => LootRegistry.HarvestToolTier.Diamond,
                    _ => LootRegistry.HarvestToolTier.None
                };
            }

            var invModel = ItemInventoryModelKind.FlatSprite;
            if (root.TryGetProperty("inventory_model", out var invProp))
            {
                var invStr = invProp.GetString()?.ToLowerInvariant();
                invModel = invStr switch
                {
                    "block" or "block_icon" => ItemInventoryModelKind.BlockIcon,
                    "custom" or "model" => ItemInventoryModelKind.CustomModel,
                    _ => ItemInventoryModelKind.FlatSprite
                };
            }

            var handModel = ItemHandModelKind.FlatSprite;
            if (root.TryGetProperty("hand_model", out var handProp))
            {
                var handStr = handProp.GetString()?.ToLowerInvariant();
                handModel = handStr switch
                {
                    "tool" => ItemHandModelKind.Tool,
                    "block" => ItemHandModelKind.Block,
                    "custom" => ItemHandModelKind.CustomModel,
                    _ => ItemHandModelKind.FlatSprite
                };
            }

            var def = new ItemDef(
                (ItemId)dynamicItemId,
                name ?? "Custom Item",
                maxStack: maxStack,
                textureName: textureName,
                inventoryModel: invModel,
                handModel: handModel,
                key: key,
                toolTier: toolTier,
                hungerRestore: hunger,
                healthRestore: health,
                customTags: tagsList,
                itemType: itemType);

            Register(def);
            createdDef = def;
            log?.Info($"Successfully registered mod item '{def.Name}' ({def.Key}) with tags: {string.Join(", ", def.StringTags)}");
            return true;
        }
        catch (Exception ex)
        {
            log?.Error($"Failed to parse item JSON: {ex.Message}");
            return false;
        }
    }
}
