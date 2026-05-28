using System.IO;

namespace CubicEngine.UnityCli
{
    internal static class FileSystemUtility
    {
        public static void EnsureCleanDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            Directory.CreateDirectory(path);
        }

        public static void CopyDirectory(string sourceDirectory, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);

            foreach (var file in Directory.GetFiles(sourceDirectory))
            {
                var targetFile = Path.Combine(destinationDirectory, Path.GetFileName(file));
                File.Copy(file, targetFile, true);
            }

            foreach (var directory in Directory.GetDirectories(sourceDirectory))
            {
                var targetDirectory = Path.Combine(destinationDirectory, Path.GetFileName(directory));
                CopyDirectory(directory, targetDirectory);
            }
        }
    }
}
