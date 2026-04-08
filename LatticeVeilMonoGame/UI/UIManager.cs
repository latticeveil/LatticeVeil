using Microsoft.Xna.Framework;

namespace LatticeVeilMonoGame.UI;

public class UIManager
{
    private readonly Rectangle _uiViewport;
    private readonly Dictionary<string, UIManagerElement> _elements = new();
    
    public UIManager(Rectangle uiViewport)
    {
        _uiViewport = uiViewport;
    }
    
    public void AddButton(string name, Rectangle bounds, string? description = null)
    {
        _elements[name] = new UIManagerElement
        {
            Name = name,
            Bounds = bounds,
            Description = description
        };
    }
    
    public void UpdateButton(string name, Rectangle bounds)
    {
        if (_elements.TryGetValue(name, out var element))
        {
            element.Bounds = bounds;
        }
    }
    
    public Rectangle GetButtonBounds(string name)
    {
        if (_elements.TryGetValue(name, out var element))
            return element.Bounds;
        return Rectangle.Empty;
    }
    
    // Layout helpers for easy positioning
    public Rectangle CenterButton(Rectangle buttonSize, int yOffset)
    {
        var centerX = _uiViewport.X + _uiViewport.Width / 2;
        var centerY = _uiViewport.Y + _uiViewport.Height / 2;
        return new Rectangle(
            centerX - buttonSize.Width / 2,
            centerY - buttonSize.Height / 2 + yOffset,
            buttonSize.Width,
            buttonSize.Height
        );
    }
    
    public Rectangle StackButtonsVertically(Rectangle buttonSize, int startIndex, int spacing, Rectangle? reference = null)
    {
        var refRect = reference ?? CenterButton(buttonSize, 0);
        var yOffset = (startIndex - 1) * (buttonSize.Height + spacing);
        return new Rectangle(
            refRect.X,
            refRect.Y + yOffset,
            buttonSize.Width,
            buttonSize.Height
        );
    }
    
    public void CreateMainMenuLayout()
    {
        var width = Math.Max(1, _uiViewport.Width);
        var height = Math.Max(1, _uiViewport.Height);
        var centerX = _uiViewport.X + width / 2;

        var footerMarginX = Math.Clamp(width / 48, 14, 28);
        var footerMarginY = Math.Clamp(height / 40, 14, 24);
        var footerButtonSizeValue = Math.Clamp(Math.Min(width, height) / 10, 72, 108);
        var footerButtonSize = new Rectangle(0, 0, footerButtonSizeValue, footerButtonSizeValue);

        var topMargin = Math.Clamp(height / 10, 56, 112);
        var gapToFooter = Math.Clamp(height / 24, 24, 42);
        var spacing = Math.Clamp(height / 70, 10, 18);
        var buttonsCount = 4;
        var bottomReserved = footerButtonSizeValue + footerMarginY + gapToFooter;
        var availableHeight = Math.Max(280, height - topMargin - bottomReserved);
        var buttonHeight = Math.Clamp((availableHeight - spacing * (buttonsCount - 1)) / buttonsCount, 78, 120);
        var preferredButtonWidth = (int)Math.Round(buttonHeight * 3.0f);
        var maxButtonWidth = Math.Max(240, Math.Min(460, width - 140));
        var minButtonWidth = Math.Min(300, maxButtonWidth);
        var buttonWidth = Math.Clamp(preferredButtonWidth, minButtonWidth, maxButtonWidth);
        var totalHeight = buttonHeight * buttonsCount + spacing * (buttonsCount - 1);
        var startY = _uiViewport.Y + Math.Max(topMargin, (height - bottomReserved - totalHeight) / 2);

        var buttonSize = new Rectangle(0, 0, buttonWidth, buttonHeight);

        AddButton("singleplayer", new Rectangle(
            centerX - buttonSize.Width / 2,
            startY,
            buttonSize.Width,
            buttonSize.Height
        ), "Singleplayer button - top center");
        
        AddButton("multiplayer", new Rectangle(
            centerX - buttonSize.Width / 2,
            startY + (buttonSize.Height + spacing),
            buttonSize.Width,
            buttonSize.Height
        ), "Multiplayer button - below singleplayer");
        
        AddButton("options", new Rectangle(
            centerX - buttonSize.Width / 2,
            startY + 2 * (buttonSize.Height + spacing),
            buttonSize.Width,
            buttonSize.Height
        ), "Options button - below multiplayer");
        
        AddButton("quit", new Rectangle(
            centerX - buttonSize.Width / 2,
            startY + 3 * (buttonSize.Height + spacing),
            buttonSize.Width,
            buttonSize.Height
        ), "Quit button - bottom of main buttons");
        
        AddButton("profile", new Rectangle(
            footerMarginX,
            _uiViewport.Bottom - footerButtonSize.Height - footerMarginY,
            footerButtonSize.Width,
            footerButtonSize.Height
        ), "Profile button - bottom left corner");
        
        AddButton("screenshots", new Rectangle(
            _uiViewport.Right - footerButtonSize.Width - footerMarginX,
            _uiViewport.Bottom - footerButtonSize.Height - footerMarginY,
            footerButtonSize.Width,
            footerButtonSize.Height
        ), "Screenshots button - bottom right corner");
    }
    
    public string GetLayoutInfo()
    {
        var info = "Current UI Layout:\n";
        foreach (var element in _elements)
        {
            info += $"• {element.Key}: {element.Value.Description}\n";
            info += $"  Position: ({element.Value.Bounds.X}, {element.Value.Bounds.Y}) Size: {element.Value.Bounds.Width}x{element.Value.Bounds.Height}\n";
        }
        return info;
    }
}

public class UIManagerElement
{
    public string Name { get; set; } = "";
    public Rectangle Bounds { get; set; }
    public string? Description { get; set; }
}
