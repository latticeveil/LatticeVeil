using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using LatticeVeilMonoGame.Core;

namespace LatticeVeilMonoGame.UI;

public sealed class MenuStack
{
    private readonly Stack<IScreen> _stack = new();
    private Rectangle _lastViewport;

    public void Push(IScreen screen, Rectangle viewport)
    {
        Trace.WriteLine($"Screen push: {screen.GetType().Name}");
        _lastViewport = viewport;
        _stack.Push(screen);
        screen.OnResize(viewport);
    }

    public void Pop()
    {
        if (_stack.Count > 0)
        {
            var popped = _stack.Pop();
            Trace.WriteLine($"Screen pop: {popped.GetType().Name}");
            popped.OnClose();
        }

        if (_stack.Count > 0)
            _stack.Peek().OnResize(_lastViewport);
    }

    public IScreen? Peek() => _stack.Count > 0 ? _stack.Peek() : null;

    public int Count => _stack.Count;

    public void Update(GameTime gameTime, InputState input)
    {
        var top = Peek();
        top?.Update(gameTime, input);
    }

    public void Draw(SpriteBatch sb, Rectangle viewport)
    {
        var top = Peek();
        top?.Draw(sb, viewport);
    }

    public void OnResize(Rectangle viewport)
    {
        _lastViewport = viewport;
        var top = Peek();
        top?.OnResize(viewport);
    }
}
