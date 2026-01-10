using System;
using System.Drawing;
using System.Drawing.Imaging;

class Program
{
    static int tileSize = 16;
    static Bitmap bmp;

    static void FillTile(int tx, int ty, Color c)
    {
        for(int x = 0; x < tileSize; x++)
            for(int y = 0; y < tileSize; y++)
                bmp.SetPixel(tx * tileSize + x, ty * tileSize + y, c);
    }

    static void Main()
    {
        int w = tileSize * 3;
        int h = tileSize * 2;
        
        bmp = new Bitmap(w, h);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            
            Color dirt = Color.FromArgb(101, 67, 33);
            Color dirtDark = Color.FromArgb(80, 50, 20);
            Color grass = Color.FromArgb(78, 154, 6);
            Color grassDark = Color.FromArgb(60, 120, 4);

            // 1. Fill all with Dirt
            for(int x=0; x<3; x++) for(int y=0; y<2; y++) FillTile(x, y, dirt);
            
            // Global dirt noise
            Random rnd = new Random(777);
            for(int i=0; i < 150; i++) {
                bmp.SetPixel(rnd.Next(w), rnd.Next(h), dirtDark);
            }

            // 2. Top Tile (2,0) - Solid Green
            FillTile(2, 0, grass);
            for(int i=0; i<40; i++) {
                bmp.SetPixel(2*tileSize + rnd.Next(tileSize), 0*tileSize + rnd.Next(tileSize), grassDark);
            }

            // 3. Side Tiles - Minimal green (1 pixel max)
            int[][] sideTiles = { new int[] {0,0}, new int[] {1,0}, new int[] {1,1}, new int[] {2,1} };
            foreach(var tile in sideTiles)
            {
                int tx = tile[0];
                int ty = tile[1];
                for(int x=0; x<tileSize; x++) {
                    // Only 1 pixel of green at the very top, and only on some pixels
                    if (rnd.NextDouble() < 0.8) {
                        bmp.SetPixel(tx*tileSize + x, ty*tileSize, grass);
                    }
                }
            }
        }
        bmp.Save("grass_solid_v6.png", ImageFormat.Png);
        bmp.Dispose();
    }
}
