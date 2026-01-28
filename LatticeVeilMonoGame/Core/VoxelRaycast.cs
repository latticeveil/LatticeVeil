using Microsoft.Xna.Framework;

namespace LatticeVeilMonoGame.Core;

public readonly struct VoxelRaycastHit
{
    public readonly int X;
    public readonly int Y;
    public readonly int Z;
    public readonly int PrevX;
    public readonly int PrevY;
    public readonly int PrevZ;
    public readonly FaceDirection Face;

    public VoxelRaycastHit(int x, int y, int z, int prevX, int prevY, int prevZ, FaceDirection face)
    {
        X = x;
        Y = y;
        Z = z;
        PrevX = prevX;
        PrevY = prevY;
        PrevZ = prevZ;
        Face = face;
    }
}

public static class VoxelRaycast
{
    public static bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, System.Func<int, int, int, byte> getBlock, out VoxelRaycastHit hit)
    {
        hit = default;
        if (maxDistance <= 0f)
            return false;

        var dir = direction;
        if (dir.LengthSquared() <= 0.0001f)
            return false;
        dir.Normalize();

        var x = (int)System.Math.Floor(origin.X);
        var y = (int)System.Math.Floor(origin.Y);
        var z = (int)System.Math.Floor(origin.Z);

        var stepX = dir.X >= 0f ? 1 : -1;
        var stepY = dir.Y >= 0f ? 1 : -1;
        var stepZ = dir.Z >= 0f ? 1 : -1;

        var tDeltaX = dir.X == 0f ? float.PositiveInfinity : System.Math.Abs(1f / dir.X);
        var tDeltaY = dir.Y == 0f ? float.PositiveInfinity : System.Math.Abs(1f / dir.Y);
        var tDeltaZ = dir.Z == 0f ? float.PositiveInfinity : System.Math.Abs(1f / dir.Z);

        var nextVoxelBoundaryX = x + (stepX > 0 ? 1f : 0f);
        var nextVoxelBoundaryY = y + (stepY > 0 ? 1f : 0f);
        var nextVoxelBoundaryZ = z + (stepZ > 0 ? 1f : 0f);

        var tMaxX = dir.X == 0f ? float.PositiveInfinity : (nextVoxelBoundaryX - origin.X) / dir.X;
        var tMaxY = dir.Y == 0f ? float.PositiveInfinity : (nextVoxelBoundaryY - origin.Y) / dir.Y;
        var tMaxZ = dir.Z == 0f ? float.PositiveInfinity : (nextVoxelBoundaryZ - origin.Z) / dir.Z;

        var prevX = x;
        var prevY = y;
        var prevZ = z;

        if (getBlock(x, y, z) != BlockIds.Air)
        {
            hit = new VoxelRaycastHit(x, y, z, prevX, prevY, prevZ, FaceDirection.PosY);
            return true;
        }

        while (true)
        {
            FaceDirection face;

            if (tMaxX < tMaxY)
            {
                if (tMaxX < tMaxZ)
                {
                    prevX = x; prevY = y; prevZ = z;
                    x += stepX;
                    face = stepX > 0 ? FaceDirection.NegX : FaceDirection.PosX;
                    if (tMaxX > maxDistance) break;
                    tMaxX += tDeltaX;
                }
                else
                {
                    prevX = x; prevY = y; prevZ = z;
                    z += stepZ;
                    face = stepZ > 0 ? FaceDirection.NegZ : FaceDirection.PosZ;
                    if (tMaxZ > maxDistance) break;
                    tMaxZ += tDeltaZ;
                }
            }
            else
            {
                if (tMaxY < tMaxZ)
                {
                    prevX = x; prevY = y; prevZ = z;
                    y += stepY;
                    face = stepY > 0 ? FaceDirection.NegY : FaceDirection.PosY;
                    if (tMaxY > maxDistance) break;
                    tMaxY += tDeltaY;
                }
                else
                {
                    prevX = x; prevY = y; prevZ = z;
                    z += stepZ;
                    face = stepZ > 0 ? FaceDirection.NegZ : FaceDirection.PosZ;
                    if (tMaxZ > maxDistance) break;
                    tMaxZ += tDeltaZ;
                }
            }

            if (getBlock(x, y, z) != BlockIds.Air)
            {
                hit = new VoxelRaycastHit(x, y, z, prevX, prevY, prevZ, face);
                return true;
            }
        }

        return false;
    }
}
