using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.UI.Screens;

public sealed class MultiplayerBackgroundScreen : IScreen
{
    private readonly Texture2D _panel;
    private Rectangle _panelRect;
    private readonly Logger _log;

    public MultiplayerBackgroundScreen(AssetLoader assets, Logger log, Rectangle viewport)
    {
        _log = log;
        try
        {
            _panel = assets.LoadTexture("textures/menu/GUIS/Singleplayer_GUI.png");
        }
        catch
        {
            _log.Warn("Failed to load Singleplayer_GUI.png");
        }

        // Use same size/position as SingleplayerScreen
        var panelW = Math.Min(1300, viewport.Width - 20);
        var panelH = Math.Min(700, viewport.Height - 30);
        var panelX = viewport.X + (viewport.Width - panelW) / 2;
        var panelY = viewport.Y + (viewport.Height - panelH) / 2;
        _panelRect = new Rectangle(panelX, panelY, panelW, panelH);
    }

    public void Update(GameTime gameTime, InputState input) { }

    public void OnResize(Rectangle viewport)
    {
        // Update panel rect to match new viewport
        var panelW = Math.Min(1300, viewport.Width - 20);
        var panelH = Math.Min(700, viewport.Height - 30);
        var panelX = viewport.X + (viewport.Width - panelW) / 2;
        var panelY = viewport.Y + (viewport.Height - panelH) / 2;
        _panelRect = new Rectangle(panelX, panelY, panelW, panelH);
    }

    public void Draw(SpriteBatch sb, Rectangle viewport)
    {
        if (_panel != null)
        {
            DrawNinePatch(sb, _panel, _panelRect);
        }
    }

    private void DrawNinePatch(SpriteBatch sb, Texture2D texture, Rectangle destination)
    {
        if (texture == null) return;
        
        const int borderSize = 16;
        var source = new Rectangle(0, 0, texture.Width, texture.Height);
        
        var patches = new[]
        {
            new Rectangle(source.X, source.Y, borderSize, borderSize),
            new Rectangle(source.X + borderSize, source.Y, source.Width - borderSize * 2, borderSize),
            new Rectangle(source.Right - borderSize, source.Y, borderSize, borderSize),
            new Rectangle(source.X, source.Y + borderSize, borderSize, source.Height - borderSize * 2),
            new Rectangle(source.X + borderSize, source.Y + borderSize, source.Width - borderSize * 2, source.Height - borderSize * 2),
            new Rectangle(source.Right - borderSize, source.Y + borderSize, borderSize, source.Height - borderSize * 2),
            new Rectangle(source.X, source.Bottom - borderSize, borderSize, borderSize),
            new Rectangle(source.X + borderSize, source.Bottom - borderSize, source.Width - borderSize * 2, borderSize),
            new Rectangle(source.Right - borderSize, source.Bottom - borderSize, borderSize, borderSize)
        };
        
        var destPatches = new[]
        {
            new Rectangle(destination.X, destination.Y, borderSize, borderSize),
            new Rectangle(destination.X + borderSize, destination.Y, destination.Width - borderSize * 2, borderSize),
            new Rectangle(destination.Right - borderSize, destination.Y, borderSize, borderSize),
            new Rectangle(destination.X, destination.Y + borderSize, borderSize, destination.Height - borderSize * 2),
            new Rectangle(destination.X + borderSize, destination.Y + borderSize, destination.Width - borderSize * 2, destination.Height - borderSize * 2),
            new Rectangle(destination.Right - borderSize, destination.Y + borderSize, borderSize, destination.Height - borderSize * 2),
            new Rectangle(destination.X, destination.Bottom - borderSize, borderSize, borderSize),
            new Rectangle(destination.X + borderSize, destination.Bottom - borderSize, destination.Width - borderSize * 2, borderSize),
            new Rectangle(destination.Right - borderSize, destination.Bottom - borderSize, borderSize, borderSize)
        };
        
        for (int i = 0; i < 9; i++)
        {
            if (destPatches[i].Width > 0 && destPatches[i].Height > 0)
                sb.Draw(texture, destPatches[i], patches[i], Color.White);
        }
    }
}
