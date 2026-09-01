using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using LatticeVeilMonoGame.Core;
using LatticeVeilMonoGame.UI;

namespace LatticeVeilMonoGame.UI.Screens;

public sealed class DeletedWorldsScreen : IScreen
{
    private const int ScrollStep = 40;

    private readonly MenuStack _menus;
    private readonly AssetLoader _assets;
    private readonly PixelFont _font;
    private readonly Texture2D _pixel;
    private readonly Logger _log;
    private readonly Action? _onWorldsChanged;
    private readonly Button _restoreBtn;
    private readonly Button _deleteBtn;
    private readonly Button _backBtn;
    private readonly Button _confirmBtn;
    private readonly Button _cancelBtn;

    private Rectangle _viewport;
    private Rectangle _panelRect;
    private Rectangle _listRect;
    private Rectangle _confirmRect;
    private List<DeletedWorldEntry> _worlds = new();
    private int _selectedIndex = -1;
    private float _scroll;
    private int _rowHeight = 96;
    private double _now;
    private string? _status;
    private double _statusUntil;
    private ConfirmAction _confirmAction = ConfirmAction.None;

    public DeletedWorldsScreen(
        MenuStack menus,
        AssetLoader assets,
        PixelFont font,
        Texture2D pixel,
        Logger log,
        Action? onWorldsChanged = null)
    {
        _menus = menus;
        _assets = assets;
        _font = font;
        _pixel = pixel;
        _log = log;
        _onWorldsChanged = onWorldsChanged;
        _restoreBtn = new Button("RESTORE", RequestRestore) { BoldText = true };
        _deleteBtn = new Button("FULLY DELETE", RequestPermanentDelete) { BoldText = true, BackgroundColor = new Color(100, 26, 26) };
        _backBtn = new Button("BACK", () => _menus.Pop()) { BoldText = true };
        _confirmBtn = new Button("CONFIRM", ConfirmPendingAction) { BoldText = true, BackgroundColor = new Color(100, 26, 26) };
        _cancelBtn = new Button("CANCEL", CancelConfirm) { BoldText = true };
        Refresh();
    }

    public void OnResize(Rectangle viewport)
    {
        _viewport = viewport;
        var panelW = Math.Min(1248, viewport.Width - 20);
        var panelH = Math.Min(680, viewport.Height - 30);
        _panelRect = new Rectangle(
            viewport.X + (viewport.Width - panelW) / 2,
            viewport.Y + (viewport.Height - panelH) / 2,
            panelW,
            panelH);

        var margin = 34;
        var footerH = 76;
        _listRect = new Rectangle(
            _panelRect.X + margin,
            _panelRect.Y + 112,
            _panelRect.Width - margin * 2,
            _panelRect.Height - 112 - footerH - 24);
        _rowHeight = Math.Max(86, _font.LineHeight * 4 + 20);

        var gap = 16;
        var buttonH = 46;
        var buttonW = Math.Clamp((_panelRect.Width - margin * 2 - gap * 3) / 4, 150, 260);
        var y = _panelRect.Bottom - 24 - buttonH;
        _backBtn.Bounds = new Rectangle(_panelRect.X + margin, y, buttonW, buttonH);
        _restoreBtn.Bounds = new Rectangle(_panelRect.Center.X - buttonW - gap / 2, y, buttonW, buttonH);
        _deleteBtn.Bounds = new Rectangle(_panelRect.Center.X + gap / 2, y, buttonW, buttonH);

        var modalW = Math.Clamp(_panelRect.Width - 180, 360, 620);
        var modalH = 190;
        _confirmRect = new Rectangle(_panelRect.Center.X - modalW / 2, _panelRect.Center.Y - modalH / 2, modalW, modalH);
        var confirmButtonW = Math.Clamp((_confirmRect.Width - 54) / 2, 130, 230);
        var confirmY = _confirmRect.Bottom - 24 - 44;
        _confirmBtn.Bounds = new Rectangle(_confirmRect.Center.X - confirmButtonW - gap / 2, confirmY, confirmButtonW, 44);
        _cancelBtn.Bounds = new Rectangle(_confirmRect.Center.X + gap / 2, confirmY, confirmButtonW, 44);

        ClampScroll();
    }

    public void Update(GameTime gameTime, InputState input)
    {
        _now = gameTime.TotalGameTime.TotalSeconds;
        if (_status != null && _now > _statusUntil)
            _status = null;

        if (input.IsNewKeyPress(Keys.Escape))
        {
            if (_confirmAction != ConfirmAction.None)
            {
                CancelConfirm();
                return;
            }

            _menus.Pop();
            return;
        }

        if (_confirmAction != ConfirmAction.None)
        {
            _confirmBtn.Update(input);
            _cancelBtn.Update(input);
            return;
        }

        _backBtn.Update(input);
        _restoreBtn.Update(input);
        _deleteBtn.Update(input);
        HandleListInput(input);
    }

    public void Draw(SpriteBatch sb, Rectangle viewport)
    {
        if (viewport != _viewport)
            OnResize(viewport);

        sb.Begin(samplerState: SamplerState.PointClamp);
        sb.Draw(_pixel, UiLayout.WindowViewport, new Color(0, 0, 0, 210));
        sb.End();

        sb.Begin(samplerState: SamplerState.PointClamp, transformMatrix: UiLayout.Transform);
        sb.Draw(_pixel, _panelRect, new Color(20, 20, 20, 240));
        DrawBorder(sb, _panelRect, Color.White);

        var title = "DELETED WORLDS";
        var titleSize = _font.MeasureString(title);
        _font.DrawString(sb, title, new Vector2(_panelRect.Center.X - titleSize.X / 2f, _panelRect.Y + 24), Color.White);

        var subtitle = $"AUTO DELETE AFTER {DeletedWorldStore.RetentionPeriod.TotalDays:0} DAYS";
        var subtitleSize = _font.MeasureString(subtitle);
        _font.DrawString(sb, subtitle, new Vector2(_panelRect.Center.X - subtitleSize.X / 2f, _panelRect.Y + 24 + _font.LineHeight + 8), new Color(220, 220, 220));

        DrawList(sb);
        _backBtn.Draw(sb, _pixel, _font);
        _restoreBtn.Draw(sb, _pixel, _font);
        _deleteBtn.Draw(sb, _pixel, _font);

        if (!string.IsNullOrWhiteSpace(_status))
        {
            var size = _font.MeasureString(_status);
            _font.DrawString(sb, _status, new Vector2(_panelRect.Center.X - size.X / 2f, _listRect.Bottom + 8), Color.White);
        }

        if (_confirmAction != ConfirmAction.None)
            DrawConfirm(sb);

        sb.End();
    }

    private void DrawList(SpriteBatch sb)
    {
        sb.Draw(_pixel, _listRect, new Color(8, 8, 8, 150));
        DrawBorder(sb, _listRect, new Color(160, 160, 160));

        if (_worlds.Count == 0)
        {
            var msg = "NO DELETED WORLDS";
            var size = _font.MeasureString(msg);
            _font.DrawString(sb, msg, new Vector2(_listRect.Center.X - size.X / 2f, _listRect.Center.Y - size.Y / 2f), Color.White);
            return;
        }

        for (var i = 0; i < _worlds.Count; i++)
        {
            var y = _listRect.Y + i * _rowHeight - (int)MathF.Round(_scroll);
            var row = new Rectangle(_listRect.X + 8, y, _listRect.Width - 16, _rowHeight - 6);
            if (row.Bottom < _listRect.Y || row.Y > _listRect.Bottom)
                continue;

            var selected = i == _selectedIndex;
            sb.Draw(_pixel, row, selected ? new Color(70, 70, 70, 225) : new Color(26, 26, 26, 210));
            DrawBorder(sb, row, selected ? new Color(235, 235, 235) : new Color(150, 150, 150));

            var entry = _worlds[i];
            var name = TruncateToWidth(entry.WorldName, row.Width - 24);
            _font.DrawString(sb, name, new Vector2(row.X + 12, row.Y + 8), Color.White);
            _font.DrawString(sb, $"MODE: {entry.CurrentMode.ToString().ToUpperInvariant()} | ID: {ShortId(entry.WorldId, entry.DeleteId)}", new Vector2(row.X + 12, row.Y + 8 + _font.LineHeight), new Color(220, 220, 220));
            _font.DrawString(sb, $"CREATED: {FormatDate(entry.CreatedAt)}", new Vector2(row.X + 12, row.Y + 8 + _font.LineHeight * 2), new Color(205, 205, 205));
            _font.DrawString(sb, $"DELETED: {FormatDate(entry.DeletedAt)} | EXPIRES: {FormatRemaining(entry.ExpiresUtc)}", new Vector2(row.X + 12, row.Y + 8 + _font.LineHeight * 3), new Color(255, 220, 180));
        }
    }

    private void DrawConfirm(SpriteBatch sb)
    {
        sb.Draw(_pixel, _viewport, new Color(0, 0, 0, 180));
        sb.Draw(_pixel, _confirmRect, new Color(18, 18, 18, 240));
        DrawBorder(sb, _confirmRect, Color.White);

        var title = _confirmAction == ConfirmAction.Restore ? "RESTORE WORLD?" : "FULLY DELETE WORLD?";
        var titleSize = _font.MeasureString(title);
        _font.DrawString(sb, title, new Vector2(_confirmRect.Center.X - titleSize.X / 2f, _confirmRect.Y + 18), Color.White);

        var entry = GetSelected();
        var name = entry == null ? "UNKNOWN WORLD" : TruncateToWidth(entry.WorldName, _confirmRect.Width - 36);
        var nameSize = _font.MeasureString(name);
        _font.DrawString(sb, name, new Vector2(_confirmRect.Center.X - nameSize.X / 2f, _confirmRect.Y + 18 + _font.LineHeight + 10), new Color(230, 230, 230));

        var body = _confirmAction == ConfirmAction.Restore
            ? "MOVES THIS WORLD BACK INTO WORLDS."
            : "THIS CANNOT BE UNDONE.";
        var bodySize = _font.MeasureString(body);
        _font.DrawString(sb, body, new Vector2(_confirmRect.Center.X - bodySize.X / 2f, _confirmRect.Y + 18 + _font.LineHeight * 2 + 18), new Color(220, 220, 220));

        _confirmBtn.Draw(sb, _pixel, _font);
        _cancelBtn.Draw(sb, _pixel, _font);
    }

    private void HandleListInput(InputState input)
    {
        if (_listRect.Contains(input.MousePosition) && input.ScrollDelta != 0)
        {
            _scroll -= Math.Sign(input.ScrollDelta) * ScrollStep;
            ClampScroll();
        }

        if (!input.IsNewLeftClick() || !_listRect.Contains(input.MousePosition))
            return;

        var index = (int)((input.MousePosition.Y - _listRect.Y + _scroll) / _rowHeight);
        if (index >= 0 && index < _worlds.Count)
            _selectedIndex = index;
    }

    private void Refresh()
    {
        var purged = DeletedWorldStore.PurgeExpired(_log);
        _worlds = DeletedWorldStore.LoadDeletedWorlds(_log);
        if (purged > 0)
            SetStatus($"AUTO DELETED {purged} EXPIRED WORLD{(purged == 1 ? string.Empty : "S")}");

        _selectedIndex = _worlds.Count == 0 ? -1 : Math.Clamp(_selectedIndex, 0, _worlds.Count - 1);
        ClampScroll();
    }

    private void RequestRestore()
    {
        if (GetSelected() == null)
        {
            SetStatus("SELECT A DELETED WORLD");
            return;
        }

        _confirmAction = ConfirmAction.Restore;
    }

    private void RequestPermanentDelete()
    {
        if (GetSelected() == null)
        {
            SetStatus("SELECT A DELETED WORLD");
            return;
        }

        _confirmAction = ConfirmAction.PermanentDelete;
    }

    private void ConfirmPendingAction()
    {
        var entry = GetSelected();
        var action = _confirmAction;
        CancelConfirm();
        if (entry == null)
            return;

        if (action == ConfirmAction.Restore)
        {
            if (DeletedWorldStore.Restore(entry, _log, out _, out var error))
            {
                SetStatus("WORLD RESTORED");
                _onWorldsChanged?.Invoke();
            }
            else
            {
                SetStatus(error ?? "RESTORE FAILED");
            }
        }
        else if (action == ConfirmAction.PermanentDelete)
        {
            if (DeletedWorldStore.PermanentlyDelete(entry, _log, out var error))
                SetStatus("WORLD FULLY DELETED");
            else
                SetStatus(error ?? "DELETE FAILED");
        }

        Refresh();
    }

    private void CancelConfirm()
    {
        _confirmAction = ConfirmAction.None;
    }

    private DeletedWorldEntry? GetSelected()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _worlds.Count)
            return null;
        return _worlds[_selectedIndex];
    }

    private void ClampScroll()
    {
        var max = Math.Max(0, _worlds.Count * _rowHeight - _listRect.Height);
        _scroll = Math.Clamp(_scroll, 0, max);
    }

    private void SetStatus(string message)
    {
        _status = message;
        _statusUntil = _now + 3.0;
    }

    private string FormatRemaining(DateTimeOffset expiresUtc)
    {
        var remaining = expiresUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
            return "SOON";
        if (remaining.TotalDays >= 1)
            return $"{Math.Ceiling(remaining.TotalDays):0}D";
        if (remaining.TotalHours >= 1)
            return $"{Math.Ceiling(remaining.TotalHours):0}H";
        return $"{Math.Max(1, Math.Ceiling(remaining.TotalMinutes)):0}M";
    }

    private static string FormatDate(string raw)
    {
        if (!DateTimeOffset.TryParse(raw, out var value))
            return "UNKNOWN";
        return value.ToLocalTime().ToString("yyyy-MM-dd h:mm tt");
    }

    private static string ShortId(string worldId, string deleteId)
    {
        var value = !string.IsNullOrWhiteSpace(worldId) ? worldId : deleteId;
        if (value.Length <= 8)
            return value;
        return value[..8].ToUpperInvariant();
    }

    private string TruncateToWidth(string value, int maxWidth)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        if (_font.MeasureString(value).X <= maxWidth)
            return value;

        const string ellipsis = "...";
        for (var chars = value.Length - 1; chars > 1; chars--)
        {
            var candidate = value[..chars].TrimEnd() + ellipsis;
            if (_font.MeasureString(candidate).X <= maxWidth)
                return candidate;
        }

        return ellipsis;
    }

    private void DrawBorder(SpriteBatch sb, Rectangle rect, Color color)
    {
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Bottom - 2, rect.Width, 2), color);
        sb.Draw(_pixel, new Rectangle(rect.X, rect.Y, 2, rect.Height), color);
        sb.Draw(_pixel, new Rectangle(rect.Right - 2, rect.Y, 2, rect.Height), color);
    }

    private enum ConfirmAction
    {
        None,
        Restore,
        PermanentDelete
    }
}
