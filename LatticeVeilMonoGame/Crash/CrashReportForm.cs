using System;
using System.Drawing;
using System.Windows.Forms;

// To avoid ambiguity with MonoGame global aliases
using GColor = System.Drawing.Color;
using GPoint = System.Drawing.Point;

namespace LatticeVeilMonoGame.Crash;

public sealed class CrashReportForm : Form
{
    private readonly TextBox _logBox;
    private readonly Label _codeLabel;
    private readonly Label _descLabel;
    private readonly Label _tipLabel;

    public CrashReportForm(Exception exception, string? logFilePath)
    {
        Text = "LatticeVeil - Game Crash";
        Width = 700;
        Height = 500;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = GColor.FromArgb(30, 30, 30);
        ForeColor = GColor.White;

        var (code, desc, tip) = CrashAnalyzer.Analyze(exception);

        // Header
        var header = new Label
        {
            Text = "OOPS! THE GAME CRASHED.",
            Font = new Font("Segoe UI", 16, FontStyle.Bold),
            ForeColor = GColor.FromArgb(255, 80, 80),
            AutoSize = true,
            Location = new GPoint(20, 20)
        };

        // Error Code (The "Fun" part)
        _codeLabel = new Label
        {
            Text = $"ERROR CODE: {code}",
            Font = new Font("Consolas", 14, FontStyle.Bold),
            ForeColor = GColor.Yellow,
            AutoSize = true,
            Location = new GPoint(20, 60)
        };

        // Description
        _descLabel = new Label
        {
            Text = desc,
            Font = new Font("Segoe UI", 11, FontStyle.Regular),
            AutoSize = true,
            Location = new GPoint(20, 95)
        };

        // Tip
        _tipLabel = new Label
        {
            Text = $"Tip: {tip}",
            Font = new Font("Segoe UI", 10, FontStyle.Italic),
            ForeColor = GColor.LightGray,
            AutoSize = true,
            Location = new GPoint(20, 125)
        };

        // Log Box
        _logBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 9),
            BackColor = GColor.FromArgb(50, 50, 50),
            ForeColor = GColor.LightGray,
            Text = exception.ToString(),
            Location = new GPoint(20, 160),
            Width = 640,
            Height = 240,
            BorderStyle = BorderStyle.FixedSingle
        };

        if (!string.IsNullOrWhiteSpace(logFilePath))
        {
            _logBox.Text += Environment.NewLine + Environment.NewLine + $"Log file: {logFilePath}";
        }

        // Buttons
        var copyBtn = new Button
        {
            Text = "Copy Error",
            Location = new GPoint(20, 420),
            Width = 120,
            Height = 30,
            BackColor = GColor.FromArgb(60, 60, 60),
            FlatStyle = FlatStyle.Flat
        };
        copyBtn.FlatAppearance.BorderColor = GColor.Gray;
        copyBtn.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(_logBox.Text);
                MessageBox.Show("Error details copied to clipboard.", "Copied");
            }
            catch { }
        };

        var closeBtn = new Button
        {
            Text = "Close",
            Location = new GPoint(540, 420),
            Width = 120,
            Height = 30,
            BackColor = GColor.FromArgb(60, 60, 60),
            FlatStyle = FlatStyle.Flat
        };
        closeBtn.FlatAppearance.BorderColor = GColor.Gray;
        closeBtn.Click += (_, _) => Close();

        Controls.Add(header);
        Controls.Add(_codeLabel);
        Controls.Add(_descLabel);
        Controls.Add(_tipLabel);
        Controls.Add(_logBox);
        Controls.Add(copyBtn);
        Controls.Add(closeBtn);
    }
}
