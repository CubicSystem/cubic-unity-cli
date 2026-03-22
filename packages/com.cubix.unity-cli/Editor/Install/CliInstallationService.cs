using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Cubix.UnityCli
{
    internal sealed class CommandSpec
    {
        public string FileName { get; set; }
        public string PrefixArguments { get; set; }

        public string BuildArguments(string additionalArguments)
        {
            if (string.IsNullOrWhiteSpace(PrefixArguments))
            {
                return additionalArguments ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(additionalArguments))
            {
                return PrefixArguments;
            }

            return PrefixArguments + " " + additionalArguments;
        }
    }

    internal sealed class CliInstallationStatus
    {
        public bool pythonAvailable;
        public bool pipAvailable;
        public bool pipxAvailable;
        public bool cliInstalled;
        public bool cliVersionMatches;
        public bool cliCommandAvailable;
        public bool cliCommandVersionMatches;
        public bool cliSupportsTestTopLevel;
        public string pythonCommand;
        public string pythonVersion;
        public string pipVersion;
        public string pipxVersion;
        public string expectedCliVersion;
        public string cliVersion;
        public string cliCommandVersion;
        public string cliCommandCheckMessage;
        public string diagnostics;
    }

    internal static class CliInstallationService
    {
        private const string CliPackageName = "cubix-unity-cli";

        public static CliInstallationStatus Inspect()
        {
            var status = new CliInstallationStatus
            {
                expectedCliVersion = PackageLayout.PackageVersion
            };
            var diagnostics = new StringBuilder();

            if (!TryResolvePython(out var python, out var pythonVersion))
            {
                diagnostics.AppendLine("Python 3.10+ was not found.");
                status.diagnostics = diagnostics.ToString().Trim();
                return status;
            }

            status.pythonAvailable = true;
            status.pythonCommand = python.FileName + (string.IsNullOrWhiteSpace(python.PrefixArguments) ? string.Empty : " " + python.PrefixArguments);
            status.pythonVersion = pythonVersion;
            diagnostics.AppendLine("Python: " + status.pythonVersion + " via " + status.pythonCommand);

            var pipResult = RunPython(python, "-m pip --version");
            status.pipAvailable = pipResult.Success;
            status.pipVersion = pipResult.Success ? FirstLine(pipResult.StdOut) : null;
            diagnostics.AppendLine("pip: " + (status.pipAvailable ? status.pipVersion : "missing"));

            status.pipxAvailable = TryResolvePipx(python, out var pipxCommand, out var pipxVersion);
            status.pipxVersion = pipxVersion;
            diagnostics.AppendLine("pipx: " + (status.pipxAvailable ? pipxVersion : "missing"));

            if (status.pipxAvailable)
            {
                var listResult = Run(pipxCommand, "list --json");
                if (listResult.Success && TryParseCliVersion(listResult.StdOut, out var cliVersion))
                {
                    status.cliInstalled = true;
                    status.cliVersion = cliVersion;
                    status.cliVersionMatches = VersionsMatch(cliVersion, status.expectedCliVersion);
                }
            }

            if (TryResolveCliCommand(out var cliCommand, out var cliCommandVersion, out var cliCommandCheckMessage))
            {
                status.cliCommandAvailable = true;
                status.cliCommandVersion = cliCommandVersion;
                status.cliCommandVersionMatches = VersionsMatch(cliCommandVersion, status.expectedCliVersion);

                var testParserCheck = Run(cliCommand, "test status --help");
                status.cliSupportsTestTopLevel = testParserCheck.Success;
                status.cliCommandCheckMessage = FirstMeaningfulLine(
                    testParserCheck.Success ? testParserCheck.StdOut : testParserCheck.StdErr,
                    testParserCheck.Success ? null : testParserCheck.StdOut,
                    cliCommandCheckMessage);
            }
            else
            {
                status.cliCommandCheckMessage = cliCommandCheckMessage;
            }

            diagnostics.AppendLine("Expected CLI payload: " + status.expectedCliVersion);
            diagnostics.AppendLine("cubix-cli package: " + (status.cliInstalled ? status.cliVersion ?? "installed" : "missing"));
            diagnostics.AppendLine("Package matches payload: " + (status.cliVersionMatches ? "yes" : "no"));
            diagnostics.AppendLine("cubix-cli command: " + (status.cliCommandAvailable ? status.cliCommandVersion ?? "available" : "missing"));
            diagnostics.AppendLine("Command matches payload: " + (status.cliCommandVersionMatches ? "yes" : "no"));
            diagnostics.AppendLine("Top-level test parser: " + (status.cliSupportsTestTopLevel ? "supported" : "missing"));
            if (!string.IsNullOrWhiteSpace(status.cliCommandCheckMessage))
            {
                diagnostics.AppendLine("CLI self-check: " + status.cliCommandCheckMessage);
            }

            status.diagnostics = diagnostics.ToString().Trim();
            return status;
        }

        public static string InstallCli()
        {
            return InstallOrRepairCli(reinstall: false);
        }

        public static string UpdateCli()
        {
            return InstallOrRepairCli(reinstall: true);
        }

        public static string RepairCli()
        {
            return InstallOrRepairCli(reinstall: true);
        }

        public static string UninstallCli()
        {
            if (!TryResolvePython(out var python, out _))
            {
                return "Python 3.10+ was not found.";
            }

            if (!TryResolvePipx(python, out var pipxCommand, out _))
            {
                return "pipx is not installed.";
            }

            var result = Run(pipxCommand, "uninstall " + CliPackageName);
            return BuildLog("Uninstall CLI", result);
        }

        public static string CopyDiagnostics()
        {
            return Inspect().diagnostics;
        }

        private static string InstallOrRepairCli(bool reinstall)
        {
            if (!TryResolvePython(out var python, out _))
            {
                return "Python 3.10+ was not found. Install Python manually, then retry.";
            }

            var log = new StringBuilder();
            log.AppendLine("Cubix Unity CLI installation");
            log.AppendLine();

            var ensurePip = RunPython(python, "-m pip --version");
            if (!ensurePip.Success)
            {
                var ensurePipResult = RunPython(python, "-m ensurepip --upgrade");
                log.AppendLine(BuildLog("Install pip", ensurePipResult));
                if (!ensurePipResult.Success)
                {
                    return log.ToString().Trim();
                }
            }

            if (!TryResolvePipx(python, out var pipxCommand, out _))
            {
                var installPipx = RunPython(python, "-m pip install --user pipx");
                log.AppendLine(BuildLog("Install pipx", installPipx));
                if (!installPipx.Success)
                {
                    return log.ToString().Trim();
                }

                var ensurePath = RunPython(python, "-m pipx ensurepath");
                log.AppendLine(BuildLog("Run pipx ensurepath", ensurePath));
                if (!TryResolvePipx(python, out pipxCommand, out _))
                {
                    return log.AppendLine("pipx is still unavailable after installation.").ToString().Trim();
                }
            }

            var status = Inspect();
            if (status.cliInstalled && reinstall)
            {
                var uninstall = Run(pipxCommand, "uninstall " + CliPackageName);
                log.AppendLine(BuildLog("Uninstall existing CLI", uninstall));
            }

            var stagePath = StagePythonPayload();
            var install = Run(pipxCommand, "install --force \"" + stagePath + "\"");
            log.AppendLine(BuildLog(status.cliInstalled ? "Reinstall CLI" : "Install CLI", install));

            return log.ToString().Trim();
        }

        private static string StagePythonPayload()
        {
            var stageRoot = Path.Combine(PackageLayout.TempRoot, "python-stage");
            var stagePath = Path.Combine(stageRoot, Guid.NewGuid().ToString("N"));
            FileSystemUtility.EnsureCleanDirectory(stagePath);
            FileSystemUtility.CopyDirectory(PackageLayout.PythonPayloadDirectory, stagePath);
            return stagePath;
        }

        private static bool TryResolvePython(out CommandSpec python, out string version)
        {
            var candidates = new[]
            {
                new CommandSpec { FileName = "python", PrefixArguments = string.Empty },
                new CommandSpec { FileName = "py", PrefixArguments = "-3" }
            };

            foreach (var candidate in candidates)
            {
                var result = Run(candidate, "--version");
                var resolvedVersion = FirstLine(string.IsNullOrWhiteSpace(result.StdOut) ? result.StdErr : result.StdOut);
                if (result.Success && IsSupportedPythonVersion(resolvedVersion))
                {
                    python = candidate;
                    version = resolvedVersion;
                    return true;
                }
            }

            python = null;
            version = null;
            return false;
        }

        private static bool IsSupportedPythonVersion(string versionLine)
        {
            if (string.IsNullOrWhiteSpace(versionLine))
            {
                return false;
            }

            var parts = versionLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return false;
            }

            if (!Version.TryParse(parts[1], out var version))
            {
                return false;
            }

            return version >= new Version(3, 10);
        }

        private static bool TryResolvePipx(CommandSpec python, out CommandSpec pipxCommand, out string version)
        {
            var asPythonModule = new CommandSpec
            {
                FileName = python.FileName,
                PrefixArguments = python.BuildArguments("-m pipx")
            };

            var moduleResult = Run(asPythonModule, "--version");
            if (moduleResult.Success)
            {
                pipxCommand = asPythonModule;
                version = FirstLine(moduleResult.StdOut);
                return true;
            }

            var direct = new CommandSpec { FileName = "pipx", PrefixArguments = string.Empty };
            var directResult = Run(direct, "--version");
            if (directResult.Success)
            {
                pipxCommand = direct;
                version = FirstLine(directResult.StdOut);
                return true;
            }

            pipxCommand = null;
            version = null;
            return false;
        }

        private static bool TryParseCliVersion(string json, out string version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                var payload = JObject.Parse(json);
                var package = payload["venvs"]?[CliPackageName];
                if (package == null)
                {
                    return false;
                }

                version = package["metadata"]?["main_package"]?["package_version"]?.Value<string>()
                    ?? package["metadata"]?["main_package"]?["package_or_url"]?.Value<string>()
                    ?? "installed";
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryResolveCliCommand(out CommandSpec cliCommand, out string version, out string message)
        {
            cliCommand = new CommandSpec
            {
                FileName = "cubix-cli",
                PrefixArguments = string.Empty
            };

            var result = Run(cliCommand, "--version");
            message = FirstMeaningfulLine(result.StdOut, result.StdErr);
            if (!result.Success)
            {
                version = null;
                cliCommand = null;
                return false;
            }

            version = ParseCliCommandVersion(result.StdOut, result.StdErr);
            return true;
        }

        private static string ParseCliCommandVersion(params string[] outputs)
        {
            var line = FirstMeaningfulLine(outputs);
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            foreach (var token in line.Split(new[] { ' ', '\t', ',', ';', '=' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = token.Trim().Trim('"', '\'', '(', ')', '[', ']', '{', '}');
                if (Version.TryParse(candidate, out _))
                {
                    return candidate;
                }
            }

            return line.Trim();
        }

        private static bool VersionsMatch(string left, string right)
        {
            var normalizedLeft = ParseCliCommandVersion(left);
            var normalizedRight = ParseCliCommandVersion(right);
            if (string.IsNullOrWhiteSpace(normalizedLeft) || string.IsNullOrWhiteSpace(normalizedRight))
            {
                return false;
            }

            return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }

        private static ProcessResult RunPython(CommandSpec python, string additionalArguments)
        {
            return Run(python, python.BuildArguments(additionalArguments), true);
        }

        private static ProcessResult Run(CommandSpec command, string additionalArguments, bool useRawArguments = false)
        {
            return ProcessRunner.Run(new ProcessCommand
            {
                FileName = command.FileName,
                Arguments = useRawArguments ? additionalArguments : command.BuildArguments(additionalArguments),
                WorkingDirectory = PackageLayout.PackageRoot
            });
        }

        private static string BuildLog(string title, ProcessResult result)
        {
            var builder = new StringBuilder();
            builder.AppendLine(title + ": " + (result.Success ? "OK" : "FAILED"));
            builder.AppendLine("Command: " + result.CommandLine);
            if (!string.IsNullOrWhiteSpace(result.StdOut))
            {
                builder.AppendLine(result.StdOut.Trim());
            }

            if (!string.IsNullOrWhiteSpace(result.StdErr))
            {
                builder.AppendLine(result.StdErr.Trim());
            }

            return builder.ToString().Trim();
        }

        private static string FirstMeaningfulLine(params string[] values)
        {
            foreach (var value in values)
            {
                var line = FirstLine(value);
                if (!string.IsNullOrWhiteSpace(line))
                {
                    return line;
                }
            }

            return null;
        }

        private static string FirstLine(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            using (var reader = new StringReader(value))
            {
                return reader.ReadLine();
            }
        }
    }
}
