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

    public static string FilesRoot
    {
        get
        {
            var root = Path.Combine(AppDataRoot, "files");
            Directory.CreateDirectory(root);
            return root;
        }
    }

    public static string BlobsRoot
    {
        get
        {
            var root = Path.Combine(AppDataRoot, "blobs");
            Directory.CreateDirectory(root);
            return root;
        }
    }
}
