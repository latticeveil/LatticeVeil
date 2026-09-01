namespace LatticeVeilMonoGame.Core;

public static class BlockIds
{
    public const byte Air = 0;
    public const byte Grass = 1;
    public const byte Dirt = 2;
    public const byte Stone = 3;
    public const byte Water = 4;
    public const byte Sand = 5;
    public const byte OakLog = 6;
    public const byte Leaves = 7;
    public const byte Chest = 10;
    public const byte CoalOre = 11;
    public const byte Coal = CoalOre;
    public const byte IronOre = 12;
    public const byte Iron = IronOre;
    public const byte ArtificerBench = 13;
    public const byte ArtificersWorkbench = ArtificerBench;
    public const byte CraftingTable = ArtificerBench;
    public const byte Glass = 14;
    public const byte Nullrock = 15;
    // Back-compat alias (older code uses different casing)
    public const byte NullRock = Nullrock;
    public const byte Nullblock = Nullrock;
    public const byte Corestone = Nullrock;
    public const byte Gravel = 16;
    public const byte OakPlanks = 17;
    public const byte Gold = 18;
    public const byte GoldOre = Gold;
    public const byte Diamond = 19;
    public const byte Runestone = 20;
    public const byte Veinstone = 21;
    public const byte Veilglass = 22;
    public const byte Gravestone = 23;
    public const byte InscribedTablet = 24;
    public const byte FragmentScrap = 25;
    public const byte VeilSealStone = 26;
    public const byte EchoBloom = 27;
    public const byte GraveSilt = 28;
    public const byte RunestoneDust = 29;
    public const byte VeinstoneCrystal = 30;
    public const byte AnchoringSalt = 31;
    public const byte StabilizedAsh = 32;
    public const byte EchoIconShard = 33;
    public const byte CopperWiring = 34;
    public const byte LimiterSigil = 35;
    public const byte AlignmentMatrixFragment = 36;
    public const byte AttunedKeystoneFragment = 37;
    public const byte RegulatorComponent = 38;
    public const byte TransitRegulatorPart = 39;
    public const byte WayfinderPlinthPart = 40;
    public const byte ResonanceCore = 41;
    public const byte AxiomFulgrite = 42;
    public const byte AnchoringAlloy = 43;
    public const byte CleanPhial = 44;
    public const byte Swiftleaf = 45;
    public const byte Driftcap = 46;
    public const byte Embermoss = 47;
    public const byte SigilLoom = 48;
    public const byte QuietAlembic = 49;
    public const byte RunicAnvil = 50;
    public const byte CinderbranchStaff = 51;
    public const byte StormreedStaff = 52;
    public const byte WayboundFrame = 53;
    public const byte AttunedKeystone = 54;
    public const byte TimedLimiter = 55;
    public const byte TransitRegulator = 56;
    public const byte WaygatePlinth = 57;
    public const byte WaygateRune = 58;
    public const byte EvergateCore = 59;
    public const byte EvergateCoreUnstable = 60;
    public const byte FleetstepDraught = 61;
    public const byte SkyboundPhilter = 62;
    public const byte PyroskinTonic = 63;
    public const byte BrineveilElixir = 64;
    public const byte WaterBucket = 65;
    public const byte PlantFiber = 66;
    public const byte CoalChunk = 67;
    public const byte IronBlock = 68;
    public const byte GoldBlock = 69;
    public const byte DiamondBlock = 70;
    public const byte EmptyBucket = 71;
    public const byte Stick = 72;
    public const byte Torch = 73;
    public const byte FiberWrappedTorch = 74;
    public const byte BasicKiln = 75;
    public const byte AdvancedKiln = 76;
    public const byte FieldOven = 77;
    public const byte IronBillet = 78;
    public const byte GoldBillet = 79;
    public const byte FlowingWater1 = 80;
    public const byte FlowingWater2 = 81;
    public const byte FlowingWater3 = 82;
    public const byte FlowingWater4 = 83;
    public const byte FlowingWater5 = 84;
    public const byte FlowingWater6 = 85;
    public const byte FlowingWater7 = 86;
    public const byte DiamondGem = 87;
    public const byte Pebbles = 88;
    public const byte CopperOre = 89;
    public const byte IronCluster = 90;
    public const byte GoldCluster = 91;
    public const byte CopperCluster = 92;
    public const byte CopperBillet = 93;
    public const byte EmeraldOre = 94;
    public const byte RubyOre = 95;
    public const byte SapphireOre = 96;
    public const byte AmethystOre = 97;
    public const byte Emerald = 98;
    public const byte RubyStone = 99;
    public const byte SapphireGem = 100;
    public const byte AmethystShard = 101;
    public const byte BasicKilnLit = 102;
    public const byte AdvancedKilnLit = 103;
    public const byte FieldOvenLit = 104;

    public static bool IsWater(byte id) => id == Water || IsFlowingWater(id);

    public static bool IsWaterSource(byte id) => id == Water;

    public static bool IsFlowingWater(byte id) => id >= FlowingWater1 && id <= FlowingWater7;

    public static int GetFlowingWaterLevel(byte id) => IsFlowingWater(id) ? id - FlowingWater1 + 1 : 0;

    public static byte FlowingWaterForLevel(int level)
    {
        level = Math.Clamp(level, 1, 7);
        return (byte)(FlowingWater1 + level - 1);
    }
}
