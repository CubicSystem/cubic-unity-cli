using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor.PackageManager;

namespace Cubix.UnityCli
{
    internal static class PackageLayout
    {
        private const string PackageName = "com.cubix.unity-cli";
        private const string PackageAssetPath = "Packages/" + PackageName;

        public static PackageInfo LoadedPackageInfo => PackageInfo.FindForAssembly(Assembly.GetExecutingAssembly());

        public static string PackageRoot => LoadedPackageInfo?.resolvedPath;

        public static string PackageVersion => LoadedPackageInfo?.version;

        public static string ProjectManifestPath => Path.Combine(ConnectorPaths.ProjectPath, "Packages", "manifest.json");

        public static string ProjectPackagesLockPath => Path.Combine(ConnectorPaths.ProjectPath, "Packages", "packages-lock.json");

        public static string ProjectPackageRoot => ResolveEmbeddedPackageRoot();

        public static string ProjectPackageJsonAssetPath => !string.IsNullOrWhiteSpace(ProjectPackageRoot)
            ? PackageAssetPath + "/package.json"
            : null;

        public static string ProjectManifestDependencySpec => ReadProjectManifestDependencySpec();

        public static string ProjectLockPackageVersion => ReadProjectLockPackageVersion();

        public static string ProjectPackageVersion => FirstNonEmpty(ProjectLockPackageVersion, NormalizeVersion(ProjectManifestDependencySpec));

        public static string CliPayloadVersion => ReadCliPayloadVersion();

        public static bool HasLoadedPackageDrift
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(ProjectPackageVersion) &&
                    !string.Equals(PackageVersion, ProjectPackageVersion, System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return false;
            }
        }

        public static string PythonPayloadDirectory => Path.Combine(PackageRoot, "Payload~", "python");

        public static string SkillsPayloadDirectory => Path.Combine(PackageRoot, "Payload~", "skills");

        public static string TempRoot => Path.Combine(Path.GetTempPath(), "cubix-unity-cli");

        private static string ResolveEmbeddedPackageRoot()
        {
            var path = Path.Combine(ConnectorPaths.ProjectPath, "Packages", PackageName);
            return Directory.Exists(path) && File.Exists(Path.Combine(path, "package.json")) ? path : null;
        }

        private static string ReadProjectManifestDependencySpec()
        {
            return ReadJsonString(ProjectManifestPath, payload => payload["dependencies"]?[PackageName]?.Value<string>());
        }

        private static string ReadProjectLockPackageVersion()
        {
            return NormalizeVersion(ReadJsonString(ProjectPackagesLockPath, payload => payload["dependencies"]?[PackageName]?["version"]?.Value<string>()));
        }

        private static string ReadJsonString(string path, System.Func<JObject, string> selector)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                return selector(JObject.Parse(File.ReadAllText(path)));
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            foreach (var token in value.Split(new[] { ' ', '\t', ',', ';', '=', '@' }, System.StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = token.Trim().Trim('"', '\'', '(', ')', '[', ']', '{', '}');
                if (System.Version.TryParse(candidate, out _))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static string ReadCliPayloadVersion()
        {
            var pyprojectPath = Path.Combine(PythonPayloadDirectory, "pyproject.toml");
            if (!File.Exists(pyprojectPath))
            {
                return null;
            }

            try
            {
                foreach (var rawLine in File.ReadAllLines(pyprojectPath))
                {
                    var line = rawLine?.Trim();
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
                    {
                        continue;
                    }

                    if (!line.StartsWith("version", System.StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var separatorIndex = line.IndexOf('=');
                    if (separatorIndex < 0 || separatorIndex >= line.Length - 1)
                    {
                        continue;
                    }

                    return NormalizeVersion(line.Substring(separatorIndex + 1));
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }
    }
}
