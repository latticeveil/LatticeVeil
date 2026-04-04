// FastNoiseLite - C# Implementation
// This is a simplified implementation based on the FastNoiseLite library
// For production use, consider adding the official FastNoiseLite NuGet package

using System;

namespace LatticeVeilMonoGame.Core
{
    /// <summary>
    /// FastNoiseLite - Simplified C# implementation for voxel terrain generation
    /// Based on the FastNoiseLite library by Auburn
    /// </summary>
    public class FastNoiseLite
    {
        private int _seed;
        private float _frequency = 0.01f;
        private NoiseType _noiseType = NoiseType.Perlin;
        private int _octaves = 3;
        private float _lacunarity = 2.0f;
        private float _gain = 0.5f;
        
        public enum NoiseType
        {
            Perlin,
            Simplex,
            OpenSimplex2,
            OpenSimplex2S,
            Cellular,
            ValueCubic,
            Value
        }
        
        public FastNoiseLite(int seed = 1337)
        {
            _seed = seed;
        }
        
        public int Seed
        {
            get => _seed;
            set => _seed = value;
        }
        
        public float Frequency
        {
            get => _frequency;
            set => _frequency = value;
        }
        
        public NoiseType Noise
        {
            get => _noiseType;
            set => _noiseType = value;
        }
        
        public int Octaves
        {
            get => _octaves;
            set => _octaves = value;
        }
        
        public float Lacunarity
        {
            get => _lacunarity;
            set => _lacunarity = value;
        }
        
        public float Gain
        {
            get => _gain;
            set => _gain = value;
        }
        
        /// <summary>
        /// Get 2D noise value
        /// </summary>
        public float GetNoise(float x, float y)
        {
            x *= _frequency;
            y *= _frequency;
            
            switch (_noiseType)
            {
                case NoiseType.Perlin:
                    return PerlinNoise(x, y);
                case NoiseType.Simplex:
                    return SimplexNoise(x, y);
                case NoiseType.OpenSimplex2:
                    return OpenSimplex2Noise(x, y);
                case NoiseType.Cellular:
                    return CellularNoise(x, y);
                default:
                    return PerlinNoise(x, y);
            }
        }
        
        /// <summary>
        /// Get 3D noise value
        /// </summary>
        public float GetNoise(float x, float y, float z)
        {
            x *= _frequency;
            y *= _frequency;
            z *= _frequency;
            
            switch (_noiseType)
            {
                case NoiseType.Perlin:
                    return PerlinNoise(x, y, z);
                case NoiseType.Simplex:
                    return SimplexNoise(x, y, z);
                case NoiseType.OpenSimplex2:
                    return OpenSimplex2Noise(x, y, z);
                case NoiseType.Cellular:
                    return CellularNoise(x, y, z);
                default:
                    return PerlinNoise(x, y, z);
            }
        }
        
        // Fractal Brownian Motion
        public float GetFBM(float x, float y)
        {
            float sum = 0;
            float amplitude = 1;
            float frequency = _frequency;
            
            for (int i = 0; i < _octaves; i++)
            {
                sum += GetNoise(x * frequency, y * frequency) * amplitude;
                amplitude *= _gain;
                frequency *= _lacunarity;
            }
            
            return sum;
        }
        
        public float GetFBM(float x, float y, float z)
        {
            float sum = 0;
            float amplitude = 1;
            float frequency = _frequency;
            
            for (int i = 0; i < _octaves; i++)
            {
                sum += GetNoise(x * frequency, y * frequency, z * frequency) * amplitude;
                amplitude *= _gain;
                frequency *= _lacunarity;
            }
            
            return sum;
        }
        
        // Simplified Perlin noise implementation
        private float PerlinNoise(float x, float y)
        {
            int xi = (int)Math.Floor(x) & 255;
            int yi = (int)Math.Floor(y) & 255;
            
            float xf = x - MathF.Floor(x);
            float yf = y - MathF.Floor(y);
            
            float u = Fade(xf);
            float v = Fade(yf);
            
            // Simplified hash function
            int a = P(xi) + yi;
            int aa = P(a);
            int ab = P(a + 1);
            int b = P(xi + 1) + yi;
            int ba = P(b);
            int bb = P(b + 1);
            
            float x1 = Lerp(Grad(P(aa), xf, yf), Grad(P(ba), xf - 1, yf), u);
            float x2 = Lerp(Grad(P(ab), xf, yf - 1), Grad(P(bb), xf - 1, yf - 1), u);
            
            return Lerp(x1, x2, v);
        }
        
        private float PerlinNoise(float x, float y, float z)
        {
            // Simplified 3D Perlin - for production use, implement full 3D Perlin
            return PerlinNoise(x, y) * 0.5f + PerlinNoise(x + z, y + z) * 0.5f;
        }
        
        private float SimplexNoise(float x, float y)
        {
            // Simplified Simplex - for production use, implement full Simplex
            return PerlinNoise(x, y);
        }
        
        private float SimplexNoise(float x, float y, float z)
        {
            // Simplified 3D Simplex - for production use, implement full 3D Simplex
            return PerlinNoise(x, y, z);
        }
        
        private float OpenSimplex2Noise(float x, float y)
        {
            // Simplified OpenSimplex2 - for production use, implement full OpenSimplex2
            return PerlinNoise(x, y);
        }
        
        private float OpenSimplex2Noise(float x, float y, float z)
        {
            // Simplified 3D OpenSimplex2 - for production use, implement full 3D OpenSimplex2
            return PerlinNoise(x, y, z);
        }
        
        private float CellularNoise(float x, float y)
        {
            // Simplified cellular noise - for production use, implement full cellular
            return (PerlinNoise(x, y) + 1) * 0.5f;
        }
        
        private float CellularNoise(float x, float y, float z)
        {
            // Simplified 3D cellular noise - for production use, implement full 3D cellular
            return (PerlinNoise(x, y, z) + 1) * 0.5f;
        }
        
        // Helper functions
        private static float Fade(float t) => t * t * t * (t * (t * 6 - 15) + 10);
        private static float Lerp(float a, float b, float t) => a + t * (b - a);
        private static float Grad(int hash, float x, float y)
        {
            int h = hash & 3;
            float u = h < 2 ? x : y;
            float v = h < 2 ? y : x;
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }
        
        private static int P(int i) => Permutation[i & 255];
        
        // Permutation table
        private static readonly int[] Permutation = {
            151,160,137,91,90,15,131,13,201,95,96,53,194,233,7,225,140,36,103,30,69,142,
            8,99,37,240,21,10,23,190,6,148,247,120,234,75,0,26,197,62,94,252,219,203,117,
            35,11,32,57,177,33,88,237,149,56,87,174,20,125,136,171,168,68,175,74,165,71,
            134,139,48,27,166,77,146,158,231,83,111,229,122,60,211,133,230,220,105,92,41,
            55,46,245,40,244,102,143,54,65,25,63,161,1,216,80,73,209,76,132,187,208,89,
            18,169,200,196,135,130,116,188,159,86,164,100,109,198,173,186,3,64,52,217,226,
            250,124,123,5,202,38,147,118,126,255,82,85,212,207,206,59,227,47,16,58,17,182,
            189,28,42,223,183,170,213,119,248,152,2,44,154,163,70,221,153,101,155,167,43,
            172,9,129,22,39,253,19,98,108,110,79,113,224,232,178,185,112,104,218,246,97,228,
            251,34,242,193,238,210,144,12,191,179,162,241,81,51,145,235,249,14,239,107,49,
            192,214,31,181,199,106,157,184,84,204,176,115,121,50,45,127,4,150,254,138,236,
            205,93,222,114,67,29,24,72,243,141,128,195,78,66,215,61,156,180,151,160,137,91,
            90,15,131,13,201,95,96,53,194,233,7,225,140,36,103,30,69,142,8,99,37,240,21,10,23,
            190,6,148,247,120,234,75,0,26,197,62,94,252,219,203,117,35,11,32,57,177,33,88,237,
            149,56,87,174,20,125,136,171,168,68,175,74,165,71,134,139,48,27,166,77,146,158,
            231,83,111,229,122,60,211,133,230,220,105,92,41,55,46,245,40,244,102,143,54,65,
            25,63,161,1,216,80,73,209,76,132,187,208,89,18,169,200,196,135,130,116,188,159,
            86,164,100,109,198,173,186,3,64,52,217,226,250,124,123,5,202,38,147,118,126,255,
            82,85,212,207,206,59,227,47,16,58,17,182,189,28,42,223,183,170,213,119,248,152,
            2,44,154,163,70,221,153,101,155,167,43,172,9,129,22,39,253,19,98,108,110,79,113,
            224,232,178,185,112,104,218,246,97,228,251,34,242,193,238,210,144,12,191,179,162,
            241,81,51,145,235,249,14,239,107,49,192,214,31,181,199,106,157,184,84,204,176,115,
            121,50,45,127,4,150,254,138,236,205,93,222,114,67,29,24,72,243,141,128,195,78,66,
            215,61,156,180
        };
    }
}
