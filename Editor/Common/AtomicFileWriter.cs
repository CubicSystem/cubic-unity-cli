using System;
using System.IO;
using System.Text;

namespace CubicEngine.UnityCli
{
    internal static class AtomicFileWriter
    {
        private const int MaxPublishAttempts = 2;

        public static bool TryWriteAllText(string path, string contents, Encoding encoding)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("A destination path is required.", nameof(path));
            }

            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new ArgumentException("The destination path must include a directory.", nameof(path));
            }

            Directory.CreateDirectory(directory);

            var fileName = Path.GetFileName(path);
            var temporaryPath = Path.Combine(
                directory,
                "." + fileName + "." + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                WriteTemporaryFile(temporaryPath, contents ?? string.Empty, encoding ?? Encoding.UTF8);
                return TryPublish(temporaryPath, path);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }

        private static void WriteTemporaryFile(string path, string contents, Encoding encoding)
        {
            var bytes = encoding.GetBytes(contents);
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        private static bool TryPublish(string temporaryPath, string destinationPath)
        {
            for (var attempt = 0; attempt < MaxPublishAttempts; attempt++)
            {
                try
                {
                    if (File.Exists(destinationPath))
                    {
                        File.Replace(temporaryPath, destinationPath, null, true);
                    }
                    else
                    {
                        File.Move(temporaryPath, destinationPath);
                    }

                    return true;
                }
                catch (IOException) when (attempt < MaxPublishAttempts - 1)
                {
                    continue;
                }
                catch (UnauthorizedAccessException) when (attempt < MaxPublishAttempts - 1)
                {
                    continue;
                }
            }

            return false;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
