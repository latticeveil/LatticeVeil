using Microsoft.Xna.Framework;

namespace LatticeVeilMonoGame.UI;

public class UIManager
{
    private readonly Rectangle _virtualViewport;
    private readonly Dictionary<string, UIManagerElement> _elements = new();
    
    public UIManager(Rectangle actualViewport)
    {
        // Convert actual viewport to virtual viewport
        _virtualViewport = VirtualResolution.ToVirtual(actualViewport);
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
        {
            // Convert virtual coordinates to screen coordinates
            return VirtualResolution.ToScreen(element.Bounds);
        }
        return Rectangle.Empty;
    }
    
    // Layout helpers for easy positioning
    public Rectangle CenterButton(Rectangle buttonSize, int yOffset)
    {
        var centerX = _virtualViewport.X + _virtualViewport.Width / 2;
        var centerY = _virtualViewport.Y + _virtualViewport.Height / 2;
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
        var buttonSize = new Rectangle(0, 0, 500, 180);
        var spacing = 20; // Positive spacing to prevent button overlap
        
        // Use virtual resolution coordinates (1920x1080)
        var centerX = VirtualResolution.VirtualWidth / 2;
        var buttonsCount = 4;
        var totalHeight = buttonSize.Height * buttonsCount + spacing * (buttonsCount - 1);
        var startY = (VirtualResolution.VirtualHeight - totalHeight) / 2;
        
        // Main menu buttons - properly spaced in virtual coordinates
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
        
        // Profile button - bottom left in virtual coordinates (perfect square, bigger)
        var profileSize = new Rectangle(0, 0, 120, 120); // Perfect square, bigger
        AddButton("profile", new Rectangle(
            20,
            VirtualResolution.VirtualHeight - profileSize.Height - 50,
            profileSize.Width,
            profileSize.Height
        ), "Profile button - bottom left corner");
        
        // Screenshots button - bottom right in virtual coordinates
        AddButton("screenshots", new Rectangle(
            VirtualResolution.VirtualWidth - 250 - 80,
            VirtualResolution.VirtualHeight - profileSize.Height - 50,
            250,
            profileSize.Height
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
