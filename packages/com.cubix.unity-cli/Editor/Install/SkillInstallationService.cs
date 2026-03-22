using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Cubix.UnityCli
{
    internal enum SkillAgentTarget
    {
        Codex,
        ClaudeCode
    }

    internal enum SkillInstallState
    {
        NotInstalled,
        Installed,
        Outdated,
        Error
    }

    internal sealed class SkillFolderStatus
    {
        public string name;
        public string sourcePath;
        public string destinationPath;
        public SkillInstallState state;
        public string installedVersion;
        public string expectedVersion;
        public string error;
    }

    internal sealed class SkillInstallStatus
    {
        public SkillAgentTarget target;
        public string rootPath;
        public bool agentAvailable;
        public string agentName;
        public string agentLocation;
        public SkillInstallState state;
        public List<SkillFolderStatus> skills = new List<SkillFolderStatus>();
    }

    internal static class SkillInstallationService
    {
        private static readonly string[] SkillNames =
        {
            "cubix-unity-cli-verify",
            "cubix-unity-cli-edit-loop"
        };

        public static SkillInstallStatus Inspect(SkillAgentTarget target)
        {
            var status = new SkillInstallStatus
            {
                target = target,
                rootPath = GetDestinationRoot(target),
                agentName = GetAgentDisplayName(target),
                agentAvailable = TryLocateAgent(target, out var agentLocation),
                agentLocation = agentLocation
            };

            foreach (var skillName in SkillNames)
            {
                var sourcePath = GetSourcePath(target, skillName);
                var destinationPath = Path.Combine(status.rootPath, skillName);
                var skillStatus = new SkillFolderStatus
                {
                    name = skillName,
                    sourcePath = sourcePath,
                    destinationPath = destinationPath,
                    expectedVersion = PackageLayout.PackageVersion
                };

                if (!Directory.Exists(destinationPath))
                {
                    skillStatus.state = SkillInstallState.NotInstalled;
                }
                else if (!TryReadManifestVersion(Path.Combine(destinationPath, ".cubix-skill.json"), out var installedVersion))
                {
                    skillStatus.state = SkillInstallState.Error;
                    skillStatus.error = "Missing or invalid .cubix-skill.json.";
                }
                else
                {
                    skillStatus.installedVersion = installedVersion;
                    skillStatus.state = installedVersion == PackageLayout.PackageVersion
                        ? SkillInstallState.Installed
                        : SkillInstallState.Outdated;
                }

                status.skills.Add(skillStatus);
            }

            status.state = DeriveOverallState(status.skills);
            return status;
        }

        public static string Install(SkillAgentTarget target)
        {
            var root = GetDestinationRoot(target);
            Directory.CreateDirectory(root);
            var log = new List<string>();

            foreach (var skillName in SkillNames)
            {
                var source = GetSourcePath(target, skillName);
                var destination = Path.Combine(root, skillName);
                if (!Directory.Exists(source))
                {
                    log.Add("Missing skill payload: " + source);
                    continue;
                }

                if (Directory.Exists(destination))
                {
                    Directory.Delete(destination, true);
                }

                FileSystemUtility.CopyDirectory(source, destination);
                WriteManifestVersion(Path.Combine(destination, ".cubix-skill.json"), skillName, target);
                log.Add("Installed " + skillName + " -> " + destination);
            }

            log.Add("Target: " + target);
            log.Add("Root: " + root);
            return string.Join(Environment.NewLine, log);
        }

        public static string InstallAll()
        {
            return Install(SkillAgentTarget.Codex) + Environment.NewLine + Install(SkillAgentTarget.ClaudeCode);
        }

        public static string RepairAll()
        {
            return InstallAll();
        }

        public static string Remove(SkillAgentTarget target)
        {
            var root = GetDestinationRoot(target);
            var log = new List<string>();
            foreach (var skillName in SkillNames)
            {
                var destination = Path.Combine(root, skillName);
                if (Directory.Exists(destination))
                {
                    Directory.Delete(destination, true);
                    log.Add("Removed " + destination);
                }
            }

            if (log.Count == 0)
            {
                log.Add("No installed " + target + " skills were found under " + root);
            }

            return string.Join(Environment.NewLine, log);
        }

        public static string GetDiagnostics()
        {
            var codex = Inspect(SkillAgentTarget.Codex);
            var claude = Inspect(SkillAgentTarget.ClaudeCode);
            return FormatStatus(codex) + Environment.NewLine + FormatStatus(claude);
        }

        private static string GetSourcePath(SkillAgentTarget target, string skillName)
        {
            var targetName = target == SkillAgentTarget.Codex ? "codex" : "claude";
            return Path.Combine(PackageLayout.SkillsPayloadDirectory, targetName, skillName);
        }

        private static string GetAgentDisplayName(SkillAgentTarget target)
        {
            return target == SkillAgentTarget.Codex ? "Codex" : "Claude Code";
        }

        private static string GetDestinationRoot(SkillAgentTarget target)
        {
            if (target == SkillAgentTarget.Codex)
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "skills");
            }

            return Path.Combine(ConnectorPaths.ProjectPath, ".claude", "skills");
        }

        private static bool TryLocateAgent(SkillAgentTarget target, out string location)
        {
            var commandCandidates = target == SkillAgentTarget.Codex
                ? new[] { "codex" }
                : new[] { "claude", "claude-code" };

            if (TryFindOnPath(commandCandidates, out location))
            {
                return true;
            }

            var fallbackPath = target == SkillAgentTarget.Codex
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
                : Path.Combine(ConnectorPaths.ProjectPath, ".claude");

            if (Directory.Exists(fallbackPath))
            {
                location = fallbackPath;
                return true;
            }

            location = null;
            return false;
        }

        private static bool TryFindOnPath(IEnumerable<string> commandNames, out string location)
        {
            location = null;
            var pathValue = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathValue))
            {
                return false;
            }

            var extensions = GetExecutableExtensions();
            foreach (var directory in pathValue.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                {
                    continue;
                }

                foreach (var commandName in commandNames)
                {
                    foreach (var candidate in ExpandExecutableNames(commandName, extensions))
                    {
                        var fullPath = Path.Combine(directory.Trim(), candidate);
                        if (File.Exists(fullPath))
                        {
                            location = fullPath;
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static IEnumerable<string> ExpandExecutableNames(string commandName, IEnumerable<string> extensions)
        {
            if (Path.HasExtension(commandName))
            {
                yield return commandName;
                yield break;
            }

            foreach (var extension in extensions)
            {
                yield return commandName + extension;
            }
        }

        private static IEnumerable<string> GetExecutableExtensions()
        {
            if (Path.PathSeparator != ';')
            {
                yield return string.Empty;
                yield break;
            }

            var pathExt = Environment.GetEnvironmentVariable("PATHEXT");
            if (string.IsNullOrWhiteSpace(pathExt))
            {
                yield return ".exe";
                yield return ".cmd";
                yield return ".bat";
                yield break;
            }

            foreach (var extension in pathExt.Split(';'))
            {
                if (!string.IsNullOrWhiteSpace(extension))
                {
                    yield return extension.ToLowerInvariant();
                }
            }
        }

        private static SkillInstallState DeriveOverallState(IEnumerable<SkillFolderStatus> skills)
        {
            if (skills.Any(skill => skill.state == SkillInstallState.Error))
            {
                return SkillInstallState.Error;
            }

            if (skills.All(skill => skill.state == SkillInstallState.NotInstalled))
            {
                return SkillInstallState.NotInstalled;
            }

            if (skills.Any(skill => skill.state == SkillInstallState.Outdated))
            {
                return SkillInstallState.Outdated;
            }

            return skills.All(skill => skill.state == SkillInstallState.Installed)
                ? SkillInstallState.Installed
                : SkillInstallState.Error;
        }

        private static bool TryReadManifestVersion(string path, out string version)
        {
            version = null;
            if (!File.Exists(path))
            {
                return false;
            }

            try
            {
                version = JObject.Parse(File.ReadAllText(path))["version"]?.Value<string>();
                return !string.IsNullOrWhiteSpace(version);
            }
            catch
            {
                return false;
            }
        }

        private static void WriteManifestVersion(string path, string skillName, SkillAgentTarget target)
        {
            var payload = new JObject
            {
                ["name"] = skillName,
                ["version"] = PackageLayout.PackageVersion,
                ["target"] = target == SkillAgentTarget.Codex ? "codex" : "claude"
            };

            File.WriteAllText(path, payload.ToString());
        }

        private static string FormatStatus(SkillInstallStatus status)
        {
            var lines = new List<string>
            {
                status.target + ": " + status.state + " (" + status.rootPath + ")"
            };

            foreach (var skill in status.skills)
            {
                lines.Add("  - " + skill.name + ": " + skill.state + (string.IsNullOrWhiteSpace(skill.installedVersion) ? string.Empty : " [" + skill.installedVersion + "]"));
            }

            return string.Join(Environment.NewLine, lines);
        }
    }
}
