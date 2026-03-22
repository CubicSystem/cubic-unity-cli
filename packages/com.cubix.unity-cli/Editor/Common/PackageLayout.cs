using System.IO;
using System.Reflection;
using UnityEditor.PackageManager;

namespace Cubix.UnityCli
{
    internal static class PackageLayout
    {
        private static PackageInfo _packageInfo;

        public static PackageInfo PackageInfo => _packageInfo ?? (_packageInfo = PackageInfo.FindForAssembly(Assembly.GetExecutingAssembly()));

        public static string PackageRoot => PackageInfo.resolvedPath;

        public static string PackageVersion => PackageInfo.version;

        public static string PythonPayloadDirectory => Path.Combine(PackageRoot, "Payload~", "python");

        public static string SkillsPayloadDirectory => Path.Combine(PackageRoot, "Payload~", "skills");

        public static string TempRoot => Path.Combine(Path.GetTempPath(), "cubix-unity-cli");
    }
}
