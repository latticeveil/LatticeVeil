// Global aliases to avoid type ambiguity when Windows Forms is enabled.
// WinForms introduces System.Drawing and System.Windows.Forms types that collide with MonoGame types.
global using Rectangle = Microsoft.Xna.Framework.Rectangle;
global using Point = Microsoft.Xna.Framework.Point;
global using Color = Microsoft.Xna.Framework.Color;

global using Keys = Microsoft.Xna.Framework.Input.Keys;
global using ButtonState = Microsoft.Xna.Framework.Input.ButtonState;
