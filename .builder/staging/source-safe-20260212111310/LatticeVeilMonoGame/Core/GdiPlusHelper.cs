using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace LatticeVeilMonoGame.Core
{
    /// <summary>
    /// Helper class to fix GDI+ runtime errors
    /// </summary>
    public static class GdiPlusHelper
    {
        /// <summary>
        /// Safely load image with GDI+ error handling
        /// </summary>
        public static Bitmap? SafeLoadImage(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return null;
                
                // Check file extension
                var extension = Path.GetExtension(filePath).ToLower();
                if (extension != ".png" && extension != ".jpg" && extension != ".jpeg" && extension != ".bmp")
                    return null;
                
                // Load with specific settings to avoid GDI+ errors
                using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                var bitmap = new Bitmap(fileStream);
                
                // Validate bitmap
                if (bitmap.Width <= 0 || bitmap.Height <= 0)
                {
                    bitmap.Dispose();
                    return null;
                }
                
                return bitmap;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GDI+ Error loading image {filePath}: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Safely save image with GDI+ error handling
        /// </summary>
        public static bool SafeSaveImage(Bitmap bitmap, string filePath)
        {
            try
            {
                // Ensure directory exists
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                
                // Determine format from extension
                var extension = Path.GetExtension(filePath).ToLower();
                var format = extension switch
                {
                    ".png" => ImageFormat.Png,
                    ".jpg" or ".jpeg" => ImageFormat.Jpeg,
                    ".bmp" => ImageFormat.Bmp,
                    _ => ImageFormat.Png
                };
                
                // Save with specific settings
                using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
                bitmap.Save(fileStream, format);
                
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GDI+ Error saving image {filePath}: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Fix common GDI+ issues by setting proper graphics settings
        /// </summary>
        public static void ConfigureGraphics(Graphics graphics)
        {
            try
            {
                // Set interpolation mode for better quality
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                
                // Set compositing mode for proper transparency
                graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
                graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GDI+ Error configuring graphics: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Create a safe bitmap with error handling
        /// </summary>
        public static Bitmap? SafeCreateBitmap(int width, int height)
        {
            try
            {
                if (width <= 0 || height <= 0 || width > 65536 || height > 65536)
                    return null;
                
                return new Bitmap(width, height, PixelFormat.Format32bppArgb);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GDI+ Error creating bitmap {width}x{height}: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Clone bitmap safely to avoid GDI+ errors
        /// </summary>
        public static Bitmap? SafeCloneBitmap(Bitmap source)
        {
            try
            {
                if (source == null || source.Width <= 0 || source.Height <= 0)
                    return null;
                
                return new Bitmap(source);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"GDI+ Error cloning bitmap: {ex.Message}");
                return null;
            }
        }
        
        /// <summary>
        /// Check if GDI+ is available and working
        /// </summary>
        public static bool IsGdiPlusAvailable()
        {
            try
            {
                using var testBitmap = new Bitmap(1, 1);
                using var testGraphics = Graphics.FromImage(testBitmap);
                return true;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Get GDI+ error information
        /// </summary>
        public static string GetGdiPlusErrorInfo(Exception ex)
        {
            return $"GDI+ Error: {ex.GetType().Name} - {ex.Message}\n" +
                   $"Stack Trace: {ex.StackTrace}\n" +
                   $"HResult: 0x{ex.HResult:X8}";
        }
    }
}
