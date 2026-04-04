using DColor = System.Drawing.Color;
using DFont = System.Drawing.Font;
using DRectangle = System.Drawing.Rectangle;
using DSolidBrush = System.Drawing.SolidBrush;
using DPen = System.Drawing.Pen;
using DStringFormat = System.Drawing.StringFormat;
using DStringAlignment = System.Drawing.StringAlignment;
using System.Windows.Forms;

namespace LatticeVeilMonoGame.Launcher;

public sealed class SliderToggleCheckBox : CheckBox
{
    private bool _darkTheme = true;

    public SliderToggleCheckBox()
    {
        AutoSize = false;
        Width = 250;
        Height = 30;
        Cursor = Cursors.Hand;
        Font = new DFont(SystemFonts.MessageBoxFont?.FontFamily ?? SystemFonts.DefaultFont.FontFamily, 9.5f, System.Drawing.FontStyle.Bold);
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint
            | ControlStyles.SupportsTransparentBackColor,
            true);
    }

    public void ApplyLauncherTheme(bool darkTheme)
    {
        _darkTheme = darkTheme;
        BackColor = DColor.Transparent;
        ForeColor = darkTheme ? DColor.White : DColor.Black;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);

        var trackRect = new DRectangle(0, (Height - 18) / 2, 42, 18);
        var labelRect = new DRectangle(trackRect.Right + 10, 0, Width - trackRect.Right - 10, Height);

        var borderColor = _darkTheme ? DColor.White : DColor.FromArgb(35, 35, 35);
        var offColor = _darkTheme ? DColor.FromArgb(20, 20, 20) : DColor.FromArgb(220, 220, 220);
        var onColor = _darkTheme ? DColor.FromArgb(88, 190, 125) : DColor.FromArgb(55, 145, 95);
        var thumbColor = _darkTheme ? DColor.White : DColor.FromArgb(25, 25, 25);
        var textColor = Enabled ? ForeColor : DColor.Gray;

        using var trackBrush = new DSolidBrush(Checked ? onColor : offColor);
        using var borderPen = new DPen(borderColor, 2f);
        using var thumbBrush = new DSolidBrush(thumbColor);
        using var textBrush = new DSolidBrush(textColor);
        using var textFormat = new DStringFormat { LineAlignment = DStringAlignment.Center };

        g.FillRectangle(trackBrush, trackRect);
        g.DrawRectangle(borderPen, trackRect);

        var thumbSize = 10;
        var thumbX = Checked ? trackRect.Right - 14 : trackRect.X + 4;
        var thumbRect = new DRectangle(thumbX, trackRect.Y + 4, thumbSize, thumbSize);
        g.FillRectangle(thumbBrush, thumbRect);

        g.DrawString(Text, Font, textBrush, labelRect, textFormat);

        if (Focused)
        {
            var focusRect = new DRectangle(labelRect.X, 3, labelRect.Width - 2, Height - 6);
            ControlPaint.DrawFocusRectangle(g, focusRect, textColor, Parent?.BackColor ?? BackColor);
        }
    }
}
