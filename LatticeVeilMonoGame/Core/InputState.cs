using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace LatticeVeilMonoGame.Core;

public sealed class InputState
{
    private KeyboardState _prevKeyboard;
    private KeyboardState _keyboard;

    private MouseState _prevMouse;
    private MouseState _mouse;
    private Vector2 _lookDelta;
    private float _uiScale = 1f;
    private Point _uiOffset = Point.Zero;
    private readonly Dictionary<Keys, long> _textRepeatTicks = new();
    private static readonly double StopwatchTickFrequency = Stopwatch.Frequency;
    private const double TextRepeatInitialDelaySeconds = 0.34;
    private const double TextRepeatIntervalSeconds = 0.045;

    public void Update()
    {
        _prevKeyboard = _keyboard;
        _prevMouse = _mouse;

        _keyboard = Microsoft.Xna.Framework.Input.Keyboard.GetState();
        _mouse = Microsoft.Xna.Framework.Input.Mouse.GetState();
    }

    public void Reset()
    {
        _keyboard = Microsoft.Xna.Framework.Input.Keyboard.GetState();
        _prevKeyboard = _keyboard;
        _mouse = Microsoft.Xna.Framework.Input.Mouse.GetState();
        _prevMouse = _mouse;
        _lookDelta = Vector2.Zero;
        _textRepeatTicks.Clear();
    }

    public Point MousePosition
    {
        get
        {
            var x = (_mouse.Position.X - _uiOffset.X) / _uiScale;
            var y = (_mouse.Position.Y - _uiOffset.Y) / _uiScale;
            return new Point((int)Math.Round(x), (int)Math.Round(y));
        }
    }

    public Point RawMousePosition => _mouse.Position;

    public Vector2 LookDelta => _lookDelta;

    public int ScrollDelta => _mouse.ScrollWheelValue - _prevMouse.ScrollWheelValue;

    public bool IsKeyDown(Keys key) => _keyboard.IsKeyDown(key);

    public bool IsNewKeyPress(Keys key) => _keyboard.IsKeyDown(key) && !_prevKeyboard.IsKeyDown(key);

    public bool IsLeftDown() => _mouse.LeftButton == global::Microsoft.Xna.Framework.Input.ButtonState.Pressed;

    public bool IsNewLeftClick() => _mouse.LeftButton == global::Microsoft.Xna.Framework.Input.ButtonState.Pressed && _prevMouse.LeftButton == global::Microsoft.Xna.Framework.Input.ButtonState.Released;

    public bool IsRightDown() => _mouse.RightButton == global::Microsoft.Xna.Framework.Input.ButtonState.Pressed;

    public bool IsNewRightClick() => _mouse.RightButton == global::Microsoft.Xna.Framework.Input.ButtonState.Pressed && _prevMouse.RightButton == global::Microsoft.Xna.Framework.Input.ButtonState.Released;

    public bool IsMiddleDown() => _mouse.MiddleButton == global::Microsoft.Xna.Framework.Input.ButtonState.Pressed;

    public bool IsNewMiddleClick() => _mouse.MiddleButton == global::Microsoft.Xna.Framework.Input.ButtonState.Pressed && _prevMouse.MiddleButton == global::Microsoft.Xna.Framework.Input.ButtonState.Released;

    public void SetUiTransform(float scale, Point offset)
    {
        _uiScale = scale <= 0.01f ? 1f : scale;
        _uiOffset = offset;
    }

    public void SetLookDelta(Vector2 delta)
    {
        _lookDelta = delta;
    }

    public IEnumerable<Keys> GetNewKeys()
    {
        // Keys pressed this frame that were not pressed last frame.
        var keys = _keyboard.GetPressedKeys();
        for (int i = 0; i < keys.Length; i++)
        {
            var k = keys[i];
            if (!_prevKeyboard.IsKeyDown(k))
                yield return k;
        }
    }

    public IEnumerable<Keys> GetTextInputKeys()
    {
        var nowTicks = Stopwatch.GetTimestamp();

        var pressed = _keyboard.GetPressedKeys();
        var pressedSet = new HashSet<Keys>(pressed);
        if (_textRepeatTicks.Count > 0)
        {
            var stale = new List<Keys>();
            foreach (var kvp in _textRepeatTicks)
            {
                if (!pressedSet.Contains(kvp.Key))
                    stale.Add(kvp.Key);
            }

            for (int i = 0; i < stale.Count; i++)
                _textRepeatTicks.Remove(stale[i]);
        }

        var initialDelayTicks = (long)(TextRepeatInitialDelaySeconds * StopwatchTickFrequency);
        var repeatIntervalTicks = (long)(TextRepeatIntervalSeconds * StopwatchTickFrequency);

        for (int i = 0; i < pressed.Length; i++)
        {
            var key = pressed[i];
            if (!_prevKeyboard.IsKeyDown(key))
            {
                _textRepeatTicks[key] = nowTicks + initialDelayTicks;
                yield return key;
                continue;
            }

            if (!_textRepeatTicks.TryGetValue(key, out var nextTick))
                continue;

            if (nowTicks >= nextTick)
            {
                _textRepeatTicks[key] = nowTicks + repeatIntervalTicks;
                yield return key;
            }
        }
    }
}
