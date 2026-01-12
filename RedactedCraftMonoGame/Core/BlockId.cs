namespace RedactedCraftMonoGame.Core;

// Block IDs are stored as bytes in chunk data.
// Note: Water remains at 4 for backward compatibility with existing worlds.
public enum BlockId : byte
{
    Air = 0,
    Grass = 1,
    Dirt = 2,
    Stone = 3,
    Water = 4,
    Sand = 5,
    Wood = 6,
    Leaves = 7,
    // 8-9 reserved
    Chest = 10,
    Coal = 11,
    Iron = 12,
    ArtificerBench = 13,
    Glass = 14,
    Nullrock = 15,
    Gravel = 16,
    Plank = 17,
    Gold = 18,
    Diamond = 19
}
