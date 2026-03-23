using System.IO;
using System.Reflection;
using UnityEditor.PackageManager;

namespace Cubix.UnityCli
{
    internal static class PackageLayout
    {
        private const string PackageAssetPath = "Packages/com.cubix.unity-cli";

        public static PackageInfo LoadedPackageInfo => PackageInfo.FindForAssembly(Assembly.GetExecutingAssembly());

        public static PackageInfo ProjectPackageInfo => PackageInfo.FindForAssetPath(PackageAssetPath);

        public static string PackageRoot => LoadedPackageInfo?.resolvedPath ?? ProjectPackageInfo?.resolvedPath;

        public static string PackageVersion => LoadedPackageInfo?.version ?? ProjectPackageInfo?.version;

        public static string ProjectPackageRoot => ProjectPackageInfo?.resolvedPath ?? PackageRoot;

        public static string ProjectPackageVersion => ProjectPackageInfo?.version ?? PackageVersion;

        public static bool HasLoadedPackageDrift =>
            !string.Equals(PackageVersion, ProjectPackageVersion, System.StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(PackageRoot, ProjectPackageRoot, System.StringComparison.OrdinalIgnoreCase);

        public static string PythonPayloadDirectory => Path.Combine(PackageRoot, "Payload~", "python");

        public static string SkillsPayloadDirectory => Path.Combine(PackageRoot, "Payload~", "skills");

        public static string TempRoot => Path.Combine(Path.GetTempPath(), "cubix-unity-cli");
    }
}
