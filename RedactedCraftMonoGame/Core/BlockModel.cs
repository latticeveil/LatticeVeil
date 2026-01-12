using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace RedactedCraftMonoGame.Core;

public enum BlockModelContext
{
    Gui,
    FirstPersonRightHand,
    FirstPersonLeftHand,
    ThirdPersonRightHand,
    ThirdPersonLeftHand,
    Ground,
    Fixed,
    Head
}

public sealed class BlockModel
{
    private readonly List<BlockModelElement> _elements;
    private readonly Dictionary<BlockModelContext, BlockModelTransform> _display;
    private readonly Vector2 _textureSize;

    private static readonly Dictionary<BlockId, BlockModel> Cache = new();

    public static string ConfigPath => Path.Combine(Paths.ConfigDir, "custom_model.json");

    private BlockModel(List<BlockModelElement> elements, Dictionary<BlockModelContext, BlockModelTransform> display, Vector2 textureSize)
    {
        _elements = elements;
        _display = display;
        _textureSize = textureSize;
    }

    public static BlockModel GetModel(BlockId id, Logger log)
    {
        if (Cache.TryGetValue(id, out var cached))
            return cached;

        var name = BlockRegistry.Get(id).Name.Trim().ToLowerInvariant().Replace(' ', '_');
        var path = Path.Combine(Paths.AssetsDir, "models", $"{name}.json");
        
        BlockModel? model = null;
        if (File.Exists(path))
        {
            model = LoadModel(path, log);
        }

        if (model == null)
        {
            model = CreateDefaultCube();
        }

        Cache[id] = model;
        return model;
    }

    private static BlockModel? LoadModel(string path, Logger log)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var doc = JsonDocument.Parse(fs);
            return ParseModel(doc.RootElement);
        }
        catch (Exception ex)
        {
            log.Warn($"Failed to load model from {path}: {ex.Message}");
            return null;
        }
    }

    private static BlockModel? ParseModel(JsonElement root)
    {
        if (!root.TryGetProperty("elements", out var elementsEl))
            return null;

        var textureSize = new Vector2(16, 16);
        if (root.TryGetProperty("texture_size", out var texSizeEl) && texSizeEl.GetArrayLength() >= 2)
        {
            textureSize = new Vector2(ReadFloat(texSizeEl, 0, 16), ReadFloat(texSizeEl, 1, 16));
        }

        var elements = new List<BlockModelElement>();
        foreach (var el in elementsEl.EnumerateArray())
        {
            var element = new BlockModelElement
            {
                From = ReadVec3(el.GetProperty("from"), Vector3.Zero),
                To = ReadVec3(el.GetProperty("to"), new Vector3(16, 16, 16))
            };

            if (el.TryGetProperty("faces", out var facesEl))
            {
                foreach (var prop in facesEl.EnumerateObject())
                {
                    if (TryMapFace(prop.Name, out var dir))
                    {
                        var faceEl = prop.Value;
                        var face = new BlockModelFace();
                        if (faceEl.TryGetProperty("uv", out var uvEl))
                        {
                            face.Uv = ReadUv(uvEl, textureSize);
                        }
                        element.Faces[dir] = face;
                    }
                }
            }
            elements.Add(element);
        }

        var display = new Dictionary<BlockModelContext, BlockModelTransform>();
        if (root.TryGetProperty("display", out var displayEl))
        {
            foreach (var prop in displayEl.EnumerateObject())
            {
                if (TryMapContext(prop.Name, out var ctx))
                {
                    var transEl = prop.Value;
                    var transform = new BlockModelTransform
                    {
                        Rotation = ReadVec3(transEl.TryGetProperty("rotation", out var r) ? r : default, Vector3.Zero),
                        Translation = ReadVec3(transEl.TryGetProperty("translation", out var t) ? t : default, Vector3.Zero),
                        ScaleFactor = ReadVec3(transEl.TryGetProperty("scale", out var s) ? s : default, Vector3.One)
                    };
                    display[ctx] = transform;
                }
            }
        }

        return new BlockModel(elements, display, textureSize);
    }

    public bool TryGetDisplayTransform(BlockModelContext context, out Matrix transform)
    {
        if (_display.TryGetValue(context, out var t))
        {
            transform = t.ToMatrix();
            return true;
        }

        transform = Matrix.Identity;
        return false;
    }

    public VertexPositionTexture[] BuildMesh(CubeNetAtlas atlas, BlockId id)
    {
        var verts = new List<VertexPositionTexture>();
        for (var i = 0; i < _elements.Count; i++)
        {
            var element = _elements[i];
            var min = Scale.MU(element.From) - new Vector3(0.5f, 0.5f, 0.5f);
            var max = Scale.MU(element.To) - new Vector3(0.5f, 0.5f, 0.5f);

            var hasFaces = element.Faces.Count > 0;
            AddElementFace(verts, atlas, id, FaceDirection.PosX, min, max, hasFaces ? element.GetFace(FaceDirection.PosX) : null, hasFaces);
            AddElementFace(verts, atlas, id, FaceDirection.NegX, min, max, hasFaces ? element.GetFace(FaceDirection.NegX) : null, hasFaces);
            AddElementFace(verts, atlas, id, FaceDirection.PosY, min, max, hasFaces ? element.GetFace(FaceDirection.PosY) : null, hasFaces);
            AddElementFace(verts, atlas, id, FaceDirection.NegY, min, max, hasFaces ? element.GetFace(FaceDirection.NegY) : null, hasFaces);
            AddElementFace(verts, atlas, id, FaceDirection.PosZ, min, max, hasFaces ? element.GetFace(FaceDirection.PosZ) : null, hasFaces);
            AddElementFace(verts, atlas, id, FaceDirection.NegZ, min, max, hasFaces ? element.GetFace(FaceDirection.NegZ) : null, hasFaces);
        }

        if (verts.Count == 0)
            return BuildCubeMesh(atlas, id);

        return verts.ToArray();
    }

    public static VertexPositionTexture[] BuildCubeMesh(CubeNetAtlas atlas, BlockId id)
    {
        var verts = new List<VertexPositionTexture>();
        var min = new Vector3(-0.5f, -0.5f, -0.5f);
        var max = new Vector3(0.5f, 0.5f, 0.5f);

        AddFace(verts, atlas, id, FaceDirection.PosX,
            new Vector3(max.X, min.Y, min.Z),
            new Vector3(max.X, min.Y, max.Z),
            new Vector3(max.X, max.Y, max.Z),
            new Vector3(max.X, max.Y, min.Z));

        AddFace(verts, atlas, id, FaceDirection.NegX,
            new Vector3(min.X, min.Y, max.Z),
            new Vector3(min.X, min.Y, min.Z),
            new Vector3(min.X, max.Y, min.Z),
            new Vector3(min.X, max.Y, max.Z));

        AddFace(verts, atlas, id, FaceDirection.PosY,
            new Vector3(min.X, max.Y, max.Z),
            new Vector3(max.X, max.Y, max.Z),
            new Vector3(max.X, max.Y, min.Z),
            new Vector3(min.X, max.Y, min.Z));

        AddFace(verts, atlas, id, FaceDirection.NegY,
            new Vector3(min.X, min.Y, min.Z),
            new Vector3(max.X, min.Y, min.Z),
            new Vector3(max.X, min.Y, max.Z),
            new Vector3(min.X, min.Y, max.Z));

        AddFace(verts, atlas, id, FaceDirection.PosZ,
            new Vector3(min.X, min.Y, max.Z),
            new Vector3(max.X, min.Y, max.Z),
            new Vector3(max.X, max.Y, max.Z),
            new Vector3(min.X, max.Y, max.Z));

        AddFace(verts, atlas, id, FaceDirection.NegZ,
            new Vector3(max.X, min.Y, min.Z),
            new Vector3(min.X, min.Y, min.Z),
            new Vector3(min.X, max.Y, min.Z),
            new Vector3(max.X, max.Y, min.Z));

        return verts.ToArray();
    }

    private static BlockModel CreateDefaultCube()
    {
        var element = new BlockModelElement
        {
            From = Vector3.Zero,
            To = new Vector3(16f, 16f, 16f)
        };

        var display = new Dictionary<BlockModelContext, BlockModelTransform>
        {
            [BlockModelContext.Gui] = new() { Rotation = new Vector3(25.5f, -43.25f, 0f), ScaleFactor = new Vector3(0.49414f, 0.49414f, 0.49414f) },
            [BlockModelContext.FirstPersonRightHand] = new() { Rotation = new Vector3(-6.05f, 6.72f, 0.7f), ScaleFactor = new Vector3(0.375f, 0.375f, 0.375f) },
            [BlockModelContext.FirstPersonLeftHand] = new() { Rotation = new Vector3(-6.05f, 6.72f, 0.7f), ScaleFactor = new Vector3(0.375f, 0.375f, 0.375f) },
            [BlockModelContext.ThirdPersonRightHand] = new() { Rotation = new Vector3(14f, 0f, 0f), ScaleFactor = new Vector3(0.375f, 0.375f, 0.375f) },
            [BlockModelContext.ThirdPersonLeftHand] = new() { Rotation = new Vector3(14f, 0f, 0f), ScaleFactor = new Vector3(0.375f, 0.375f, 0.375f) },
            [BlockModelContext.Ground] = new() { Rotation = new Vector3(-6.05f, 6.72f, 0.7f), Translation = new Vector3(0f, 3.25f, 0f), ScaleFactor = new Vector3(0.375f, 0.375f, 0.375f) },
            [BlockModelContext.Fixed] = new() { ScaleFactor = new Vector3(0.7207f, 0.7207f, 0.7207f) },
            [BlockModelContext.Head] = new() { ScaleFactor = new Vector3(0.80859f, 0.80859f, 0.80859f) }
        };

        return new BlockModel(new List<BlockModelElement> { element }, display, new Vector2(16f, 16f));
    }

    public static BlockModel LoadOrDefault(Logger log)
    {
        return CreateDefault();
    }

    private static BlockModel CreateDefault()
    {
        return CreateDefaultCube();
    }

    private static void AddElementFace(List<VertexPositionTexture> verts, CubeNetAtlas atlas, BlockId id, FaceDirection face, Vector3 min, Vector3 max, BlockModelFace? modelFace, bool hasFaces)
    {
        if (hasFaces && modelFace == null)
            return;

        var uv = modelFace?.Uv;
        var p0 = Vector3.Zero;
        var p1 = Vector3.Zero;
        var p2 = Vector3.Zero;
        var p3 = Vector3.Zero;

        switch (face)
        {
            case FaceDirection.PosX:
                p0 = new Vector3(max.X, min.Y, min.Z);
                p1 = new Vector3(max.X, min.Y, max.Z);
                p2 = new Vector3(max.X, max.Y, max.Z);
                p3 = new Vector3(max.X, max.Y, min.Z);
                break;
            case FaceDirection.NegX:
                p0 = new Vector3(min.X, min.Y, max.Z);
                p1 = new Vector3(min.X, min.Y, min.Z);
                p2 = new Vector3(min.X, max.Y, min.Z);
                p3 = new Vector3(min.X, max.Y, max.Z);
                break;
            case FaceDirection.PosY:
                p0 = new Vector3(min.X, max.Y, max.Z);
                p1 = new Vector3(max.X, max.Y, max.Z);
                p2 = new Vector3(max.X, max.Y, min.Z);
                p3 = new Vector3(min.X, max.Y, min.Z);
                break;
            case FaceDirection.NegY:
                p0 = new Vector3(min.X, min.Y, min.Z);
                p1 = new Vector3(max.X, min.Y, min.Z);
                p2 = new Vector3(max.X, min.Y, max.Z);
                p3 = new Vector3(min.X, min.Y, max.Z);
                break;
            case FaceDirection.PosZ:
                p0 = new Vector3(min.X, min.Y, max.Z);
                p1 = new Vector3(max.X, min.Y, max.Z);
                p2 = new Vector3(max.X, max.Y, max.Z);
                p3 = new Vector3(min.X, max.Y, max.Z);
                break;
            case FaceDirection.NegZ:
                p0 = new Vector3(max.X, min.Y, min.Z);
                p1 = new Vector3(min.X, min.Y, min.Z);
                p2 = new Vector3(min.X, max.Y, min.Z);
                p3 = new Vector3(max.X, max.Y, min.Z);
                break;
        }

        GetFaceUvs(atlas, (byte)id, face, uv, out var uv00, out var uv10, out var uv11, out var uv01);
        if (uv == null)
            AdjustCubeFaceUvs(face, ref uv00, ref uv10, ref uv11, ref uv01);
        AddFace(verts, face, p0, p1, p2, p3, uv00, uv10, uv11, uv01);
    }

    private static void AddFace(List<VertexPositionTexture> verts, FaceDirection face, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector2 uv00, Vector2 uv10, Vector2 uv11, Vector2 uv01)
    {
        var flip = face == FaceDirection.NegX || face == FaceDirection.NegY || face == FaceDirection.NegZ;
        if (!flip)
        {
            verts.Add(new VertexPositionTexture(p0, uv00));
            verts.Add(new VertexPositionTexture(p1, uv10));
            verts.Add(new VertexPositionTexture(p2, uv11));
            verts.Add(new VertexPositionTexture(p0, uv00));
            verts.Add(new VertexPositionTexture(p2, uv11));
            verts.Add(new VertexPositionTexture(p3, uv01));
        }
        else
        {
            verts.Add(new VertexPositionTexture(p0, uv00));
            verts.Add(new VertexPositionTexture(p2, uv11));
            verts.Add(new VertexPositionTexture(p1, uv10));
            verts.Add(new VertexPositionTexture(p0, uv00));
            verts.Add(new VertexPositionTexture(p3, uv01));
            verts.Add(new VertexPositionTexture(p2, uv11));
        }
    }

    private static void AddFace(List<VertexPositionTexture> verts, CubeNetAtlas atlas, BlockId id, FaceDirection face, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
    {
        atlas.GetFaceUvRect((byte)id, face, out var uv00, out var uv10, out var uv11, out var uv01);
        AdjustCubeFaceUvs(face, ref uv00, ref uv10, ref uv11, ref uv01);
        AddFace(verts, face, p0, p1, p2, p3, uv00, uv10, uv11, uv01);
    }

    private static void AdjustCubeFaceUvs(FaceDirection face, ref Vector2 uv00, ref Vector2 uv10, ref Vector2 uv11, ref Vector2 uv01)
    {
        switch (face)
        {
            case FaceDirection.PosX:
            case FaceDirection.NegX:
            case FaceDirection.PosZ:
            case FaceDirection.NegZ:
                FlipV(ref uv00, ref uv10, ref uv11, ref uv01);
                break;
        }
    }

    private static void FlipV(ref Vector2 uv00, ref Vector2 uv10, ref Vector2 uv11, ref Vector2 uv01)
    {
        var tmp = uv00;
        uv00 = uv01;
        uv01 = tmp;
        tmp = uv10;
        uv10 = uv11;
        uv11 = tmp;
    }

    private static void GetFaceUvs(CubeNetAtlas atlas, byte id, FaceDirection face, BlockModelUv? uv, out Vector2 uv00, out Vector2 uv10, out Vector2 uv11, out Vector2 uv01)
    {
        if (uv == null)
        {
            atlas.GetFaceUvRect(id, face, out uv00, out uv10, out uv11, out uv01);
            return;
        }

        // The model's UVs are already normalized to 0..1 within the block's local 48x32 texture.
        // We need to map these to the global atlas.
        // The CubeNetAtlas uses a grid of 48x32 regions.
        
        var def = BlockRegistry.Get(id);
        var atlasWidth = atlas.Texture.Width;
        var atlasHeight = atlas.Texture.Height;
        
        // Find the top-left pixel of this block's 48x32 region in the atlas.
        // regionsPerRow is 4.
        var regionX = (def.AtlasIndex % 4) * 48; 
        var regionY = (def.AtlasIndex / 4) * 32;

        // Map the 0..1 UVs from the JSON model to pixels within the 48x32 region, 
        // then normalize to the full atlas size.
        float MapU(float u) => (regionX + u * 48f) / atlasWidth;
        float MapV(float v) => (regionY + v * 32f) / atlasHeight;

        uv00 = new Vector2(MapU(uv.Value.U0), MapV(uv.Value.V1));
        uv10 = new Vector2(MapU(uv.Value.U1), MapV(uv.Value.V1));
        uv11 = new Vector2(MapU(uv.Value.U1), MapV(uv.Value.V0));
        uv01 = new Vector2(MapU(uv.Value.U0), MapV(uv.Value.V0));
    }

    private static Vector3 ReadVec3(JsonElement element, Vector3 fallback)
    {
        if (element.ValueKind != JsonValueKind.Array)
            return fallback;

        var x = ReadFloat(element, 0, fallback.X);
        var y = ReadFloat(element, 1, fallback.Y);
        var z = ReadFloat(element, 2, fallback.Z);
        return new Vector3(x, y, z);
    }

    private static float ReadFloat(JsonElement array, int index, float fallback)
    {
        if (array.ValueKind != JsonValueKind.Array)
            return fallback;
        if (array.GetArrayLength() <= index)
            return fallback;

        var v = array[index];
        if (v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out var f))
            return f;

        return fallback;
    }

    private static BlockModelUv? ReadUv(JsonElement uvEl, Vector2 textureSize)
    {
        var u0 = ReadFloat(uvEl, 0, 0f);
        var v0 = ReadFloat(uvEl, 1, 0f);
        var u1 = ReadFloat(uvEl, 2, textureSize.X);
        var v1 = ReadFloat(uvEl, 3, textureSize.Y);

        var minU = MathF.Min(u0, u1) / textureSize.X;
        var maxU = MathF.Max(u0, u1) / textureSize.X;
        var minV = MathF.Min(v0, v1) / textureSize.Y;
        var maxV = MathF.Max(v0, v1) / textureSize.Y;

        return new BlockModelUv
        {
            U0 = Math.Clamp(minU, 0f, 1f),
            U1 = Math.Clamp(maxU, 0f, 1f),
            V0 = Math.Clamp(minV, 0f, 1f),
            V1 = Math.Clamp(maxV, 0f, 1f)
        };
    }

    private static bool TryMapContext(string name, out BlockModelContext ctx)
    {
        switch (name)
        {
            case "gui":
                ctx = BlockModelContext.Gui;
                return true;
            case "firstperson_righthand":
                ctx = BlockModelContext.FirstPersonRightHand;
                return true;
            case "firstperson_lefthand":
                ctx = BlockModelContext.FirstPersonLeftHand;
                return true;
            case "thirdperson_righthand":
                ctx = BlockModelContext.ThirdPersonRightHand;
                return true;
            case "thirdperson_lefthand":
                ctx = BlockModelContext.ThirdPersonLeftHand;
                return true;
            case "ground":
                ctx = BlockModelContext.Ground;
                return true;
            case "fixed":
                ctx = BlockModelContext.Fixed;
                return true;
            case "head":
                ctx = BlockModelContext.Head;
                return true;
            default:
                ctx = BlockModelContext.Gui;
                return false;
        }
    }

    private static bool TryMapFace(string name, out FaceDirection dir)
    {
        switch (name)
        {
            case "north":
                dir = FaceDirection.NegZ;
                return true;
            case "south":
                dir = FaceDirection.PosZ;
                return true;
            case "east":
                dir = FaceDirection.PosX;
                return true;
            case "west":
                dir = FaceDirection.NegX;
                return true;
            case "up":
                dir = FaceDirection.PosY;
                return true;
            case "down":
                dir = FaceDirection.NegY;
                return true;
            default:
                dir = FaceDirection.PosY;
                return false;
        }
    }

    private sealed class BlockModelElement
    {
        public Vector3 From { get; set; } = Vector3.Zero;
        public Vector3 To { get; set; } = new Vector3(16f, 16f, 16f);
        public Dictionary<FaceDirection, BlockModelFace> Faces { get; } = new();

        public BlockModelFace? GetFace(FaceDirection dir)
        {
            return Faces.TryGetValue(dir, out var face) ? face : null;
        }
    }

    private sealed class BlockModelFace
    {
        public BlockModelUv? Uv { get; set; }
    }

    private sealed class BlockModelTransform
    {
        public Vector3 Rotation { get; set; } = Vector3.Zero;
        public Vector3 Translation { get; set; } = Vector3.Zero;
        public Vector3 ScaleFactor { get; set; } = Vector3.One;

        public Matrix ToMatrix()
        {
            var scale = Matrix.CreateScale(ScaleFactor);
            var rx = MathHelper.ToRadians(Rotation.X);
            var ry = MathHelper.ToRadians(Rotation.Y);
            var rz = MathHelper.ToRadians(Rotation.Z);
            var rotation = Matrix.CreateRotationX(rx) * Matrix.CreateRotationY(ry) * Matrix.CreateRotationZ(rz);
            var translation = Matrix.CreateTranslation(Scale.MU(Translation));
            return scale * rotation * translation;
        }
    }

    private readonly struct BlockModelUv
    {
        public float U0 { get; init; }
        public float V0 { get; init; }
        public float U1 { get; init; }
        public float V1 { get; init; }
    }
}
