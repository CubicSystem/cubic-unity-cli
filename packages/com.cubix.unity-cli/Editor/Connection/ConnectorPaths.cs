using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace CubicEngine.UnityCli
{
    internal static class ConnectorPaths
    {
        private const string RootFolderName = ".cubix-cli";

        public static string RootDirectory
        {
            get
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, RootFolderName);
            }
        }

        public static string InstancesDirectory => Path.Combine(RootDirectory, "instances");

        public static string StatusDirectory => Path.Combine(RootDirectory, "status");

        public static string ProjectPath => Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;

        public static string ProjectName => new DirectoryInfo(ProjectPath).Name;

        public static string ProjectHash => ComputeHash(ProjectPath);

        public static string InstanceFilePath => Path.Combine(InstancesDirectory, ProjectHash + ".json");

        public static string StatusFilePath() => Path.Combine(StatusDirectory, ProjectHash + ".json");

        public static string StatusFilePath(int port) => StatusFilePath();

        public static void EnsureDirectories()
        {
            Directory.CreateDirectory(RootDirectory);
            Directory.CreateDirectory(InstancesDirectory);
            Directory.CreateDirectory(StatusDirectory);
        }

        private static string ComputeHash(string value)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var builder = new StringBuilder(12);
                for (var index = 0; index < 6; index++)
                {
                    builder.Append(bytes[index].ToString("x2"));
                }

                return builder.ToString();
            }
        }
    }
}
