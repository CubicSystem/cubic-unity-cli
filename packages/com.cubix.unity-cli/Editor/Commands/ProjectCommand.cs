using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace CubicEngine.UnityCli
{
    [CubixCliCommand(Group = "project", Name = "project", Description = "Project-wide asset maintenance commands.")]
    internal sealed class ProjectCommand : ICubixCliCommandHandler, ICubixCliPreflightHandler
    {
        public IEnumerable<CommandDefinition> DescribeActions()
        {
            yield return new CommandDefinition
            {
                Action = "reserialize",
                Description = "Force reserialize explicit asset paths under Assets/.",
                Tags = CommandMetadata.Tags(CommandTags.Assets, CommandTags.Unsafe),
                SafetyLevel = CommandSafetyLevels.Destructive,
                SupportsPreflight = true,
                Parameters = CommandMetadata.Parameters(
                    CommandMetadata.Parameter("paths", "array", true, "Explicit asset paths to reserialize."))
            };
        }

        public object Execute(string action, JObject parameters)
        {
            switch (action)
            {
                case "reserialize":
                    var preflight = ValidateReserialize(parameters);
                    if (!preflight.canExecute)
                    {
                        return new CommandErrorResponse("Reserialize validation failed.", preflight);
                    }

                    var paths = NormalizePaths(parameters);
                    AssetDatabase.ForceReserializeAssets(paths);
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                    return new CommandSuccessResponse("Assets reserialized.", new
                    {
                        count = paths.Count,
                        paths
                    });
                default:
                    return new CommandErrorResponse("Unsupported project action '" + action + "'.");
            }
        }

        public CommandPreflightResult Preflight(string action, JObject parameters)
        {
            switch (action)
            {
                case "reserialize":
                    return ValidateReserialize(parameters);
                default:
                    return CommandMetadata.Success("project." + action, "No preflight issues.");
            }
        }

        private static CommandPreflightResult ValidateReserialize(JObject parameters)
        {
            var issues = new List<CommandPreflightIssue>();
            var paths = NormalizePaths(parameters);
            if (paths.Count == 0)
            {
                issues.Add(CommandMetadata.Issue("error", "At least one asset path is required.", "missing_paths"));
            }

            foreach (var path in paths)
            {
                if (!path.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(CommandMetadata.Issue("error", "Asset path must be under Assets/: " + path, "invalid_path"));
                    continue;
                }

                if (AssetDatabase.LoadMainAssetAtPath(path) == null)
                {
                    issues.Add(CommandMetadata.Issue("error", "Asset does not exist: " + path, "missing_asset"));
                }
            }

            var result = CommandMetadata.Result("project.reserialize", issues.ToArray());
            if (issues.Count == 0)
            {
                result.summary = "Reserialize can execute.";
            }

            return result;
        }

        private static List<string> NormalizePaths(JObject parameters)
        {
            return (parameters["paths"] as JArray)?
                .Values<string>()
                .Select(ObjectResolver.NormalizeAssetPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(System.StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
        }
    }
}
