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
    Door = 8,
    DoorOpen = 9,
    Chest = 10,
    Coal = 11,
    Iron = 12,
    CraftingTable = 13,
    Glass = 14,
    Corestone = 15,
    Gravel = 16,
    Plank = 17,
    Gold = 18,
    Diamond = 19
}
