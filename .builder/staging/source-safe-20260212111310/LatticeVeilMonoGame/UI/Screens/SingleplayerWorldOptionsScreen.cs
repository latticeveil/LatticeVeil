using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.Online.Eos;
using LatticeVeilMonoGame.Online.Gate;
using LatticeVeilMonoGame.UI;

namespace LatticeVeilMonoGame.UI.Screens;

public sealed class SingleplayerWorldOptionsScreen : IScreen
{
    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;
    private readonly PlayerProfile _profile;
    private readonly global::Microsoft.Xna.Framework.GraphicsDeviceManager _graphics;
    private readonly WorldListEntry _worldEntry;

    private readonly Button _playBtn;
    private readonly Button _openLanBtn;
    private readonly Button _hostOnlineBtn;
    private readonly Button _backBtn;

    private Rectangle _viewport;
    private Rectangle _panelRect;
    private string _status = string.Empty;
    private double _statusUntil;
    private double _now;

    public SingleplayerWorldOptionsScreen(
        MenuStack menus,
        AssetLoader assets,
        PixelFont font,
        Texture2D pixel,
        Logger log,
        PlayerProfile profile,
        global::Microsoft.Xna.Framework.GraphicsDeviceManager graphics,
        WorldListEntry worldEntry)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _profile = profile;
        _graphics = graphics;
        _worldEntry = worldEntry;

        _playBtn = new Button("PLAY", PlayWorld) { BoldText = true };
        _openLanBtn = new Button("OPEN TO LAN", OpenToLan) { BoldText = true };
        _hostOnlineBtn = new Button("HOST ONLINE", HostOnline) { BoldText = true };
        _backBtn = new Button("BACK", () => _menus.Pop()) { BoldText = true };
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;
        var panelW = Math.Clamp((int)(viewport.Width * 0.55f), 420, viewport.Width - 40);
        var panelH = Math.Clamp((int)(viewport.Height * 0.5f), 260, viewport.Height - 40);
        _panelRect = new Rectangle(
            viewport.X + (viewport.Width - panelW) / 2,
            viewport.Y + (viewport.Height - panelH) / 2,
            panelW,
            panelH);

        var margin = 20;
        var buttonGap = 12;
        var buttonH = Math.Max(40, _font.LineHeight + 16);
        var buttonW = _panelRect.Width - margin * 2;
        var firstY = _panelRect.Y + 70;

        _playBtn.Bounds = new Rectangle(_panelRect.X + margin, firstY, buttonW, buttonH);
        _openLanBtn.Bounds = new Rectangle(_panelRect.X + margin, _playBtn.Bounds.Bottom + buttonGap, buttonW, buttonH);
        _hostOnlineBtn.Bounds = new Rectangle(_panelRect.X + margin, _openLanBtn.Bounds.Bottom + buttonGap, buttonW, buttonH);
        _backBtn.Bounds = new Rectangle(_panelRect.X + margin, _hostOnlineBtn.Bounds.Bottom + buttonGap, buttonW, buttonH);
    }

    public void Update(GameTime gameTime, InputState input)
    {
        _now = gameTime.TotalGameTime.TotalSeconds;
        if (!string.IsNullOrWhiteSpace(_status) && _now > _statusUntil)
            _status = string.Empty;

        if (input.IsNewKeyPress(Keys.Escape))
        {
            _menus.Pop();
            return;
        }

        _playBtn.Update(input);
        _openLanBtn.Update(input);
        _hostOnlineBtn.Update(input);
        _backBtn.Update(input);
    }

    public void Draw(SpriteBatch sb, Rectangle viewport)
    {
        if (viewport != _viewport)
            OnResize(viewport);

        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(_pixel, UiLayout.WindowViewport, new Color(0, 0, 0, 180));
        sb.End();

        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform);
        sb.Draw(_pixel, _panelRect, new Color(20, 20, 20, 240));
        DrawBorder(sb, _panelRect, Color.White);

        var title = "WORLD OPTIONS";
        var titleSize = _font.MeasureString(title);
        _font.DrawString(sb, title, new Vector2(_panelRect.Center.X - titleSize.X / 2f, _panelRect.Y + 14), Color.White);

        var worldLabel = WorldListService.BuildDisplayTitle(_worldEntry);
        var worldSize = _font.MeasureString(worldLabel);
        _font.DrawString(sb, worldLabel, new Vector2(_panelRect.Center.X - worldSize.X / 2f, _panelRect.Y + 14 + _font.LineHeight + 4), new Color(220, 220, 220));

        _playBtn.Draw(sb, _pixel, _font);
        _openLanBtn.Draw(sb, _pixel, _font);
        _hostOnlineBtn.Draw(sb, _pixel, _font);
        _backBtn.Draw(sb, _pixel, _font);

        if (!string.IsNullOrWhiteSpace(_status))
            _font.DrawString(sb, _status, new Vector2(_panelRect.X + 20, _panelRect.Bottom - _font.LineHeight - 10), new Color(220, 220, 220));

        sb.End();
    }

    private void PlayWorld()
    {
        if (!EnsureWorldPaths())
            return;

        _menus.Push(
            new GameWorldScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, _worldEntry.WorldPath, _worldEntry.MetaPath),
            _viewport);
    }

    private void OpenToLan()
    {
        if (!EnsureWorldPaths())
            return;

        var result = WorldHostBootstrap.TryStartLanHost(_log, _profile, _worldEntry.WorldPath, _worldEntry.MetaPath);
        if (!result.Success || result.Session == null || result.World == null)
        {
            SetStatus(result.Error);
            return;
        }

        _menus.Push(
            new GameWorldScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, _worldEntry.WorldPath, _worldEntry.MetaPath, result.Session, result.World),
            _viewport);
    }

    private void HostOnline()
    {
        if (!EnsureWorldPaths())
            return;

        var gate = OnlineGateClient.GetOrCreate();
        if (!gate.CanUseOfficialOnline(_log, out var gateDenied))
        {
            SetStatus(gateDenied);
            return;
        }

        var eos = EosClientProvider.GetOrCreate(_log, "deviceid", allowRetry: true);
        if (eos == null)
        {
            SetStatus("EOS CLIENT UNAVAILABLE");
            return;
        }
        var snapshot = EosRuntimeStatus.Evaluate(eos);
        if (snapshot.Reason != EosRuntimeReason.Ready)
        {
            if (snapshot.Reason == EosRuntimeReason.Connecting)
                eos.StartLogin();
            SetStatus(snapshot.StatusText);
            return;
        }

        var result = WorldHostBootstrap.TryStartEosHost(_log, _profile, eos, _worldEntry.WorldPath, _worldEntry.MetaPath);
        if (!result.Success || result.Session == null || result.World == null)
        {
            SetStatus(result.Error);
            return;
        }

        _menus.Push(
            new GameWorldScreen(_menus, _assets, _font, _pixel, _log, _profile, _graphics, _worldEntry.WorldPath, _worldEntry.MetaPath, result.Session, result.World),
            _viewport);
    }

    private bool EnsureWorldPaths()
    {
        if (Directory.Exists(_worldEntry.WorldPath) && File.Exists(_worldEntry.MetaPath))
            return true;

        SetStatus("WORLD DATA MISSING");
        return false;
    }

    private void SetStatus(string status)
    {
        _status = status;
        _statusUntil = _now + 3.0;
    }

    private void DrawBorder(SpriteBatch sb, Rectangle rect, Color color)
    {
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Bottom - 2, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, 2, rect.Height), color);
        sb.Draw(_pixel, new Rectangle(rect.Right - 2, rect.Y, 2, rect.Height), color);
    }
}
