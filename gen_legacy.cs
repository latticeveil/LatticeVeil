using System;
using System.Drawing;
using System.Drawing.Imaging;

class Program
{
    static int tileSize = 16;
    static void Main()
    {
        using (Bitmap bmp = new Bitmap(tileSize * 3, tileSize * 2))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(101, 67, 33));
                Color grass = Color.FromArgb(78, 154, 6);
                Random rnd = new Random(123);
                for(int x=tileSize*2; x<tileSize*3; x++)
                    for(int y=0; y<tileSize; y++)
                        if(rnd.NextDouble() < 0.4) bmp.SetPixel(x, y, grass);
                for(int x=0; x<tileSize*3; x++)
                    for(int y=0; y<2; y++)
                        bmp.SetPixel(x, y, grass);
            }
            bmp.Save("grass_legacy_patchy.png", ImageFormat.Png);
        }
    }
}
