using System;
using System.IO;
using Microsoft.Xna.Framework.Graphics;

namespace LatticeVeilMonoGame.Crash;

public static class CrashAnalyzer
{
    public static (string Code, string Description, string Tip) Analyze(Exception ex)
    {
        // 1. Texture / Graphics Issues
        if (ex is NoSuitableGraphicsDeviceException || ex.Message.Contains("GraphicsDevice") || ex.Message.Contains("Shader"))
        {
            return ("BURNT_TOAST", "Graphics Device Error", "Your GPU might be unsupported or drivers are outdated.");
        }
        if (ex.Message.Contains("Texture") || ex.Message.Contains("dxt") || ex.Message.Contains("png"))
        {
            return ("CHOCOLATE", "Texture Load Failure", "A texture file is missing, corrupt, or has an invalid format.");
        }
        if (ex.Message.Contains("Effect") || ex.Message.Contains("Technique"))
        {
            return ("KALEIDOSCOPE", "Shader/Effect Error", "A visual effect failed to compile or apply.");
        }

        // 2. Memory / Resource Issues
        if (ex is OutOfMemoryException)
        {
            return ("FULL_STOMACH", "Out of Memory", "The game ran out of RAM. Try closing other apps or lowering render distance.");
        }
        if (ex is StackOverflowException)
        {
            return ("INFINITE_PANCAKE", "Stack Overflow", "Infinite loop detected in the code.");
        }

        // 3. IO / File Issues
        if (ex is FileNotFoundException fnf)
        {
            return ("EMPTY_PLATE", "File Not Found", $"Missing file: {Path.GetFileName(fnf.FileName)}");
        }
        if (ex is DirectoryNotFoundException)
        {
            return ("LOST_KITCHEN", "Directory Missing", "A required folder could not be found.");
        }
        if (ex is IOException)
        {
            return ("LOCKED_FRIDGE", "File Access Error", "A file is locked by another process or cannot be accessed.");
        }

        // 4. Network & Sync
        if (ex is ArgumentNullException && ex.StackTrace?.Contains("UpdateNetwork") == true)
        {
            return ("SOGGY_CEREAL", "World Sync Error", "The game received incomplete world data. This usually happens if the connection is too slow or the host is overwhelmed.");
        }
        if (ex.Message.Contains("EOS") || ex.Message.Contains("Epic") || ex.Message.Contains("Socket"))
        {
            return ("SPAGHETTI_CODE", "Network/EOS Error", "Connection issue or Epic Online Services failure.");
        }

        // 5. Code / Logic
        if (ex is NullReferenceException)
        {
            return ("GHOST_PEPPER", "Null Reference", "The game tried to use something that doesn't exist (internal logic error).");
        }
        if (ex is IndexOutOfRangeException || ex is ArgumentOutOfRangeException)
        {
            return ("SLICED_FINGER", "Index Out of Range", "The game tried to access a list item that isn't there.");
        }
        if (ex is DivideByZeroException)
        {
            return ("BLACK_HOLE", "Divide By Zero", "Tried to divide by zero. Math is hard.");
        }

        // 6. Fallback
        return ("MYSTERY_STEW", "Unexpected Crash", "An unknown error occurred.");
    }
}
