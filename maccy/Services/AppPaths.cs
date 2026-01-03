using System;
using System.IO;

namespace maccy.Services;

public static class AppPaths
{
    public static string AppDataRoot
    {
        get
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "maccy");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    public static string ImagesRoot
    {
        get
        {
            var root = Path.Combine(AppDataRoot, "images");
            Directory.CreateDirectory(root);
            return root;
        }
    }
}
