using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace LatticeVeilMonoGame.Core;

// Asset layout plan (blocks):
// 1) Prefer per-block cube-net PNGs from Assets/textures/blocks/*.png.
// 2) If missing, return a checkerboard pattern.
// 3) Support dynamic face sizes (16x, 32x, 64x, 128x etc).
// 4) Enable Mipmaps for quality scaling.

public readonly struct UvRect
{
    public readonly float U0;
    public readonly float V0;
    public readonly float U1;
    public readonly float V1;

    public UvRect(float u0, float v0, float u1, float v1)
    {
        U0 = u0;
        V0 = v0;
        U1 = u1;
        V1 = v1;
    }
}

public sealed class CubeNetAtlas
{
    private int _faceSize = 16;
    private int NetWidth => _faceSize * 3;
    private int NetHeight => _faceSize * 2;

    private static readonly (int x, int y)[] FaceTiles =
    {
        (1, 0), // PosX (East)
        (1, 1), // NegX (West)
        (0, 0), // PosY (Up)
        (2, 0), // NegY (Down)
        (0, 1), // PosZ (South)
        (2, 1)  // NegZ (North)
    };

    private readonly int _regionsPerRow;
    private readonly int _regionsPerCol;
    private readonly UvRect[] _faceUvs;

    public Texture2D Texture { get; }
    public bool WasGenerated { get; }

    private CubeNetAtlas(Texture2D texture, int regionsPerRow, int regionsPerCol, bool generated)
    {
        Texture = texture;
        _regionsPerRow = regionsPerRow;
        _regionsPerCol = regionsPerCol;
        WasGenerated = generated;

        _faceSize = (texture.Width / regionsPerRow) / 3;

        _faceUvs = new UvRect[256 * 6];
        FillAllWithAtlasIndex(0);
        foreach (var def in BlockRegistry.All)
            FillUvs(def.Id, def.AtlasIndex);
    }

    public bool IsTransparent(byte blockId) => BlockRegistry.IsTransparent(blockId);

    public UvRect GetFaceUv(byte blockId, FaceDirection face)
    {
        var index = blockId * 6 + (int)face;
        return _faceUvs[index];
    }

    public void GetFaceUvRect(byte blockId, FaceDirection face, out Vector2 uv00, out Vector2 uv10, out Vector2 uv11, out Vector2 uv01)
    {
        var uv = GetFaceUv(blockId, face);
        
        // Standard winding for the greedy mesher (axis aligned quads)
        uv00 = new Vector2(uv.U0, uv.V0);
        uv10 = new Vector2(uv.U1, uv.V0);
        uv11 = new Vector2(uv.U1, uv.V1);
        uv01 = new Vector2(uv.U0, uv.V1);
    }

    public Rectangle GetFaceSourceRect(byte blockId, FaceDirection face)
    {
        var def = BlockRegistry.Get(blockId);
        var regionX = (def.AtlasIndex % _regionsPerRow) * NetWidth;
        var regionY = (def.AtlasIndex / _regionsPerRow) * NetHeight;
        var tile = FaceTiles[(int)face];
        return new Rectangle(regionX + tile.x * _faceSize, regionY + tile.y * _faceSize, _faceSize, _faceSize);
    }

    public static CubeNetAtlas Build(AssetLoader assets, Logger log, string? quality = null)
    {
        bool isLow = quality == "LOW";
        var blocksDir = isLow && Directory.Exists(Paths.LowQualityBlocksTexturesDir) ? Paths.LowQualityBlocksTexturesDir : Paths.BlocksTexturesDir;

        if (TryBuildAtlasFromBlockNets(assets, log, blocksDir, out var atlasTex, out var cols, out var rows, out var anyGenerated))
        {
            return new CubeNetAtlas(atlasTex, cols, rows, generated: anyGenerated);
        }

        var generated = GenerateAtlas(assets.GraphicsDevice, log, out var regionsPerRow, out var regionsPerCol);
        return new CubeNetAtlas(generated, regionsPerRow, regionsPerCol, generated: true);
    }

    public bool TryExportPng(string fullPath, Logger log)
    {
        try
        {
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            using var fs = File.Create(fullPath);
            Texture.SaveAsPng(fs, Texture.Width, Texture.Height);
            return true;
        }
        catch (Exception ex)
        {
            log.Warn($"Atlas export failed: {ex.Message}");
            return false;
        }
    }

    public void ExportBlockNetPngs(string blocksDir, Logger log)
    {
        try
        {
            Directory.CreateDirectory(blocksDir);
            var atlasData = new Color[Texture.Width * Texture.Height];
            Texture.GetData(atlasData);

            foreach (var def in BlockRegistry.All)
            {
                var name = GetBlockFileName(def);
                var path = Path.Combine(blocksDir, name);
                var net = ExtractBlockNet(atlasData, Texture.Width, def.AtlasIndex);
                using var tex = new Texture2D(Texture.GraphicsDevice, NetWidth, NetHeight);
                tex.SetData(net);
                using var fs = File.Create(path);
                tex.SaveAsPng(fs, NetWidth, NetHeight);
            }
        }
        catch (Exception ex)
        {
            log.Warn($"Block net export failed: {ex.Message}");
        }
    }

    private void FillAllWithAtlasIndex(int atlasIndex)
    {
        for (var id = 0; id < 256; id++)
            FillUvs((BlockId)id, atlasIndex);
    }

    private void FillUvs(BlockId id, int atlasIndex)
    {
        var regionX = (atlasIndex % _regionsPerRow) * NetWidth;
        var regionY = (atlasIndex / _regionsPerRow) * NetHeight;

        for (var face = 0; face < 6; face++)
        {
            var tile = FaceTiles[face];
            var px0 = regionX + tile.x * _faceSize;
            var py0 = regionY + tile.y * _faceSize;
            var px1 = px0 + _faceSize;
            var py1 = py0 + _faceSize;

            var u0 = (px0 + 0.5f) / Texture.Width;
            var v0 = (py0 + 0.5f) / Texture.Height;
            var u1 = (px1 - 0.5f) / Texture.Width;
            var v1 = (py1 - 0.5f) / Texture.Height;

            _faceUvs[(byte)id * 6 + face] = new UvRect(u0, v0, u1, v1);
        }
    }

    private static bool TryGetAtlasGrid(Texture2D texture, out int columns, out int rows)
    {
        columns = 0; rows = 0;
        if (texture.Width % 3 != 0 || texture.Height % 2 != 0) return false;
        var count = BlockRegistry.AtlasRegionCount;
        columns = Math.Min(4, count);
        rows = (int)Math.Ceiling(count / (float)columns);
        return true;
    }

    private static Texture2D GenerateAtlas(GraphicsDevice device, Logger log, out int regionsPerRow, out int regionsPerCol)
    {
        var count = Math.Max(1, BlockRegistry.AtlasRegionCount);
        GetAtlasGrid(count, out regionsPerRow, out regionsPerCol);
        var faceSize = 16;
        var width = regionsPerRow * faceSize * 3;
        var height = regionsPerCol * faceSize * 2;
        
        var atlas = new Texture2D(device, width, height, true, SurfaceFormat.Color);
        var data = new Color[width * height];
        for (int i = 0; i < data.Length; i++) {
            int x = i % width; int y = i / width;
            bool even = ((x / 4) + (y / 4)) % 2 == 0;
            data[i] = even ? new Color(255, 0, 255) : new Color(20, 20, 20);
        }
        atlas.SetData(data);
        return atlas;
    }

    private static void BlitNet(Color[] atlas, int atlasWidth, int dstX, int dstY, Color[] net, int netW, int netH)
    {
        for (var y = 0; y < netH; y++)
        {
            var srcRow = y * netW;
            var dstRow = (dstY + y) * atlasWidth + dstX;
            Array.Copy(net, srcRow, atlas, dstRow, netW);
        }
    }

    private static string GetBlockFileName(BlockDef def)
    {
        if (!string.IsNullOrEmpty(def.TextureName))
            return def.TextureName.EndsWith(".png") ? def.TextureName : $"{def.TextureName}.png";
        return $"{def.Name.Trim().ToLowerInvariant().Replace(' ', '_')}.png";
    }

    private Color[] ExtractBlockNet(Color[] atlas, int atlasWidth, int atlasIndex)
    {
        var net = new Color[NetWidth * NetHeight];
        var regionX = (atlasIndex % _regionsPerRow) * NetWidth;
        var regionY = (atlasIndex / _regionsPerRow) * NetHeight;
        for (var y = 0; y < NetHeight; y++)
        {
            var srcRow = (regionY + y) * atlasWidth + regionX;
            var dstRow = y * NetWidth;
            Array.Copy(atlas, srcRow, net, dstRow, NetWidth);
        }
        return net;
    }

    private static bool TryLoadAtlas(string path, AssetLoader assets, Logger log, out Texture2D texture, out int cols, out int rows, out bool invalidDimensions)
    {
        texture = null!; cols = 0; rows = 0; invalidDimensions = false;
        if (!File.Exists(path)) return false;
        try {
            using var fs = File.OpenRead(path);
            texture = Texture2D.FromStream(assets.GraphicsDevice, fs);
            if (!TryGetAtlasGrid(texture, out cols, out rows)) {
                invalidDimensions = true;
                texture.Dispose();
                texture = null!;
                return true;
            }
            return true;
        } catch { return false; }
    }

    private static Texture2D CreateErrorAtlas(GraphicsDevice device, out int regionsPerRow, out int regionsPerCol)
    {
        var count = Math.Max(1, BlockRegistry.AtlasRegionCount);
        GetAtlasGrid(count, out regionsPerRow, out regionsPerCol);
        var width = regionsPerRow * 16 * 3;
        var height = regionsPerCol * 16 * 2;
        var tex = new Texture2D(device, width, height, false, SurfaceFormat.Color);
        var data = new Color[width * height];
        for (int i = 0; i < data.Length; i++) {
            int x = i % width; int y = i / width;
            bool even = ((x / 4) + (y / 4)) % 2 == 0;
            data[i] = even ? new Color(255, 0, 255) : new Color(20, 20, 20);
        }
        tex.SetData(data);
        return tex;
    }

    private static bool TryBuildAtlasFromBlockNets(AssetLoader assets, Logger log, string blocksDir, out Texture2D texture, out int regionsPerRow, out int regionsPerCol, out bool anyGenerated)
    {
        texture = null!; anyGenerated = false;
        var count = Math.Max(1, BlockRegistry.AtlasRegionCount);
        GetAtlasGrid(count, out regionsPerRow, out regionsPerCol);
        int faceSize = 16;
        foreach (var def in BlockRegistry.All) {
            var blockPath = Path.Combine(blocksDir, GetBlockFileName(def));
            if (File.Exists(blockPath)) {
                using var fs = File.OpenRead(blockPath);
                using var peek = Texture2D.FromStream(assets.GraphicsDevice, fs);
                faceSize = peek.Width / 3;
                break;
            }
        }
        var netW = faceSize * 3; var netH = faceSize * 2;
        var width = regionsPerRow * netW; var height = regionsPerCol * netH;
        var data = new Color[width * height];
        foreach (var def in BlockRegistry.All) {
            var blockPath = Path.Combine(blocksDir, GetBlockFileName(def));
            if (!TryLoadBlockNetTexture(assets.GraphicsDevice, blockPath, log, out var net, netW, netH)) {
                net = new Color[netW * netH];
                for (int i = 0; i < net.Length; i++) {
                    int x = i % netW; int y = i / netW;
                    bool even = ((x / Math.Max(1, faceSize / 4)) + (y / Math.Max(1, faceSize / 4))) % 2 == 0;
                    net[i] = even ? new Color(255, 0, 255) : new Color(20, 20, 20);
                }
                anyGenerated = true;
            }
            var regionX = (def.AtlasIndex % regionsPerRow) * netW;
            var regionY = (def.AtlasIndex / regionsPerRow) * netH;
            BlitNet(data, width, regionX, regionY, net, netW, netH);
        }
        texture = new Texture2D(assets.GraphicsDevice, width, height, false, SurfaceFormat.Color);
        texture.SetData(data);
        return true;
    }

    private static bool TryLoadBlockNetTexture(GraphicsDevice device, string path, Logger log, out Color[] data, int expectedW, int expectedH)
    {
        data = Array.Empty<Color>();
        if (!File.Exists(path)) return false;
        try {
            using var fs = File.OpenRead(path);
            using var tex = Texture2D.FromStream(device, fs);
            if (tex.Width != expectedW || tex.Height != expectedH) {
                log.Warn($"Texture size mismatch: {Path.GetFileName(path)} expected {expectedW}x{expectedH}, actual {tex.Width}x{tex.Height}");
                return false;
            }
            data = new Color[expectedW * expectedH];
            tex.GetData(data);
            return true;
        } catch { return false; }
    }

    private static void GetAtlasGrid(int count, out int regionsPerRow, out int regionsPerCol)
    {
        regionsPerRow = Math.Min(4, count);
        regionsPerCol = (int)Math.Ceiling(count / (float)regionsPerRow);
    }
}
