using System;

namespace LatticeVeilMonoGame.Core;

public readonly struct ChunkCoord : IEquatable<ChunkCoord>
{
    public readonly int X;
    public readonly int Y;
    public readonly int Z;

    public ChunkCoord(int x, int y, int z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public bool Equals(ChunkCoord other) => X == other.X && Y == other.Y && Z == other.Z;

    public override bool Equals(object? obj) => obj is ChunkCoord other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Z);

    public override string ToString() => $"{X},{Y},{Z}";

    public static bool operator ==(ChunkCoord left, ChunkCoord right) => left.Equals(right);

    public static bool operator !=(ChunkCoord left, ChunkCoord right) => !left.Equals(right);
}
