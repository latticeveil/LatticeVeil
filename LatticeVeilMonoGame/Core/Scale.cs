using Microsoft.Xna.Framework;

namespace LatticeVeilMonoGame.Core;

public static class Scale
{
    // Blockbench model units (0..16) map to 1 block in world units.
    public const float BlockSize = 1f;
    public const float ModelUnitsPerBlock = 16f;
    public const float ModelToWorld = 1f / ModelUnitsPerBlock;

    public static float MU(float v) => v * ModelToWorld;
    public static Vector3 MU(Vector3 v) => v * ModelToWorld;

    // Player dimensions in block units.
    public const float PlayerWidth = 0.6f;
    public const float PlayerHeight = 1.8f;
    public const float PlayerHeadHeight = 1.6f;

    // Interaction range in blocks.
    public const float InteractionRange = 5f;

    // First-person overlay scales (in blocks).
    public const float HandScale = 0.25f;
    public const float HeldBlockScale = 0.4f;
}
