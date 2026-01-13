using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using RedactedCraftMonoGame.Core;
using RedactedCraftMonoGame.UI;

namespace RedactedCraftMonoGame.UI.Screens;

public sealed class AssetViewerScreen : IScreen
{
    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;
    private readonly GraphicsDevice _device;

    private readonly BlockId _blockId = BlockId.Gravestone;
    private readonly CubeNetAtlas _atlas;
    private readonly BlockModel _model;
    private readonly VertexPositionTexture[] _mesh;
    private readonly BasicEffect _effect;

    private Rectangle _viewport;
    private float _yaw = 0.6f;
    private float _pitch = 0.2f;
    private float _distance = 2.4f;
    private bool _dragging;
    private Point _lastMouse;
    private bool _torchLighting;
    private bool _capturesWritten;

    public AssetViewerScreen(MenuStack menus, AssetLoader assets, PixelFont font, Texture2D pixel, Logger log)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _device = assets.GraphicsDevice;

        _atlas = CubeNetAtlas.Build(_assets, _log);
        _model = BlockModel.GetModel(_blockId, _log);
        _mesh = _model.BuildMesh(_atlas, _blockId);

        _effect = new BasicEffect(_device)
        {
            TextureEnabled = true,
            VertexColorEnabled = false,
            LightingEnabled = true,
            PreferPerPixelLighting = false
        };
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;
    }

    public void Update(GameTime gameTime, InputState input)
    {
        if (input.IsNewKeyPress(Keys.Escape))
        {
            _menus.Pop();
            return;
        }

        if (input.IsNewKeyPress(Keys.T))
            _torchLighting = !_torchLighting;

        if (input.IsLeftDown())
        {
            var pos = input.RawMousePosition;
            if (!_dragging)
            {
                _dragging = true;
                _lastMouse = pos;
            }
            else
            {
                var dx = pos.X - _lastMouse.X;
                var dy = pos.Y - _lastMouse.Y;
                _lastMouse = pos;
                _yaw += dx * 0.01f;
                _pitch += dy * 0.01f;
            }
        }
        else
        {
            _dragging = false;
        }

        if (input.IsKeyDown(Keys.Left)) _yaw -= 0.02f;
        if (input.IsKeyDown(Keys.Right)) _yaw += 0.02f;
        if (input.IsKeyDown(Keys.Up)) _pitch -= 0.02f;
        if (input.IsKeyDown(Keys.Down)) _pitch += 0.02f;

        if (input.ScrollDelta != 0)
            _distance = Math.Clamp(_distance - input.ScrollDelta * 0.0015f, 1.4f, 5.0f);

        if (input.IsKeyDown(Keys.OemPlus) || input.IsKeyDown(Keys.Add)) _distance = Math.Clamp(_distance - 0.03f, 1.4f, 5.0f);
        if (input.IsKeyDown(Keys.OemMinus) || input.IsKeyDown(Keys.Subtract)) _distance = Math.Clamp(_distance + 0.03f, 1.4f, 5.0f);

        if (!_capturesWritten)
            TryWriteCaptures();
    }

    public void Draw(SpriteBatch sb, Rectangle viewport)
    {
        if (viewport != _viewport)
            OnResize(viewport);

        DrawScene(_torchLighting);

        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform);

        var title = $"ASSET VIEWER - {_blockId}";
        _font.DrawString(sb, title, new Vector2(12, 10), Color.White);
        _font.DrawString(sb, "Drag to rotate | Wheel +/- to zoom | T = torch light | Esc = exit", new Vector2(12, 10 + _font.LineHeight + 6), Color.LightGray);

        sb.End();
    }

    private void DrawScene(bool torchLight)
    {
        _device.Clear(new Color(12, 12, 14));
        _device.DepthStencilState = DepthStencilState.Default;
        _device.RasterizerState = RasterizerState.CullNone;
        _device.BlendState = BlendState.Opaque;
        _device.SamplerStates[0] = SamplerState.PointClamp;

        var aspect = _viewport.Height == 0 ? 1f : _viewport.Width / (float)_viewport.Height;
        var view = Matrix.CreateLookAt(new Vector3(0f, 0f, _distance), Vector3.Zero, Vector3.Up);
        var projection = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(30f), aspect, 0.1f, 10f);
        var world = Matrix.CreateRotationX(_pitch) * Matrix.CreateRotationY(_yaw);

        ApplyLighting(torchLight);

        _effect.World = world;
        _effect.View = view;
        _effect.Projection = projection;
        _effect.Texture = _atlas.Texture;

        if (_mesh.Length >= 3)
        {
            foreach (var pass in _effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                _device.DrawUserPrimitives(PrimitiveType.TriangleList, _mesh, 0, _mesh.Length / 3);
            }
        }
    }

    private void ApplyLighting(bool torchLight)
    {
        _effect.LightingEnabled = true;
        _effect.AmbientLightColor = torchLight ? new Vector3(0.18f, 0.10f, 0.06f) : new Vector3(0.45f, 0.45f, 0.45f);

        _effect.DirectionalLight0.Enabled = true;
        _effect.DirectionalLight0.Direction = Vector3.Normalize(new Vector3(-0.6f, -1f, -0.4f));
        _effect.DirectionalLight0.DiffuseColor = torchLight ? new Vector3(1.0f, 0.6f, 0.25f) : new Vector3(0.9f, 0.9f, 0.9f);
        _effect.DirectionalLight0.SpecularColor = Vector3.Zero;

        _effect.DirectionalLight1.Enabled = false;
        _effect.DirectionalLight2.Enabled = false;
    }

    private void TryWriteCaptures()
    {
        _capturesWritten = true;
        try
        {
            Directory.CreateDirectory(Paths.ScreenshotsDir);
            var dayPath = Path.Combine(Paths.ScreenshotsDir, "assetview_gravestone_day.png");
            var torchPath = Path.Combine(Paths.ScreenshotsDir, "assetview_gravestone_torch.png");

            RenderCapture(dayPath, torchLight: false);
            RenderCapture(torchPath, torchLight: true);

            _log.Info($"Asset viewer captures saved: {dayPath}, {torchPath}");
        }
        catch (Exception ex)
        {
            _log.Warn($"Asset viewer capture failed: {ex.Message}");
        }
    }

    private void RenderCapture(string path, bool torchLight)
    {
        const int size = 512;
        using var target = new RenderTarget2D(_device, size, size, false, SurfaceFormat.Color, DepthFormat.Depth24);
        var prevTargets = _device.GetRenderTargets();
        var prevViewport = _device.Viewport;

        _device.SetRenderTarget(target);
        _device.Viewport = new Viewport(0, 0, size, size);
        _device.Clear(new Color(12, 12, 14));

        var view = Matrix.CreateLookAt(new Vector3(0f, 0f, _distance), Vector3.Zero, Vector3.Up);
        var projection = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(30f), 1f, 0.1f, 10f);
        var world = Matrix.CreateRotationX(_pitch) * Matrix.CreateRotationY(_yaw);

        ApplyLighting(torchLight);
        _effect.World = world;
        _effect.View = view;
        _effect.Projection = projection;
        _effect.Texture = _atlas.Texture;

        if (_mesh.Length >= 3)
        {
            foreach (var pass in _effect.CurrentTechnique.Passes)
            {
                pass.Apply();
                _device.DrawUserPrimitives(PrimitiveType.TriangleList, _mesh, 0, _mesh.Length / 3);
            }
        }

        _device.SetRenderTargets(prevTargets);
        _device.Viewport = prevViewport;

        using var fs = File.Create(path);
        target.SaveAsPng(fs, size, size);
    }
}
