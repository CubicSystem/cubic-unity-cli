using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine.SceneManagement;

namespace Cubix.UnityCli
{
    [CubixCliCommand(Group = "scene", Name = "scene", Description = "Inspect loaded scene structure.")]
    internal sealed class SceneCommand : ICubixCliCommandHandler
    {
        public IEnumerable<CommandDefinition> DescribeActions()
        {
            yield return new CommandDefinition
            {
                Action = "hierarchy",
                Description = "Read the active scene hierarchy.",
                Tags = CommandMetadata.Tags(CommandTags.Core, CommandTags.Scene),
                Parameters = CommandMetadata.Parameters(
                    CommandMetadata.Parameter("includeComponents", "boolean", false, "Include component snapshots for each object.", false),
                    CommandMetadata.Parameter("maxDepth", "integer", false, "Maximum hierarchy depth to include.", 6))
            };
            yield return new CommandDefinition
            {
                Action = "active",
                Description = "Read the active scene descriptor.",
                Tags = CommandMetadata.Tags(CommandTags.Core, CommandTags.Scene)
            };
            yield return new CommandDefinition
            {
                Action = "find",
                Description = "Search scene objects by name, path, or tag.",
                Tags = CommandMetadata.Tags(CommandTags.Scene, CommandTags.Object),
                Parameters = CommandMetadata.Parameters(
                    CommandMetadata.Parameter("query", "string", false, "Loose object name search."),
                    CommandMetadata.Parameter("name", "string", false, "Alternative name filter."),
                    CommandMetadata.Parameter("path", "string", false, "Exact hierarchy path."),
                    CommandMetadata.Parameter("tag", "string", false, "Unity tag filter."),
                    CommandMetadata.Parameter("includeInactive", "boolean", false, "Include inactive objects.", false),
                    CommandMetadata.Parameter("includeComponents", "boolean", false, "Include component snapshots.", false))
            };
        }

        public object Execute(string action, JObject parameters)
        {
            switch (action)
            {
                case "hierarchy":
                    var includeComponents = parameters.Value<bool?>("includeComponents") ?? false;
                    var maxDepth = parameters.Value<int?>("maxDepth") ?? 6;
                    return new CommandSuccessResponse("Scene hierarchy.", ObjectSnapshotter.SnapshotScene(SceneManager.GetActiveScene(), includeComponents, maxDepth));
                case "active":
                    var scene = SceneManager.GetActiveScene();
                    return new CommandSuccessResponse("Active scene.", new
                    {
                        name = scene.name,
                        path = scene.path,
                        isLoaded = scene.isLoaded,
                        rootCount = scene.rootCount
                    });
                case "find":
                    var targetPath = parameters.Value<string>("path");
                    if (!string.IsNullOrWhiteSpace(targetPath))
                    {
                        var resolved = ObjectResolver.ResolveGameObject(targetPath, parameters.Value<bool?>("includeInactive") ?? true);
                        if (resolved == null)
                        {
                            return new CommandErrorResponse("Could not resolve scene object '" + targetPath + "'.");
                        }

                        return new CommandSuccessResponse("Resolved scene object.", ObjectSnapshotter.SnapshotGameObject(resolved, parameters.Value<bool?>("includeComponents") ?? false, 0, 2));
                    }

                    var query = parameters.Value<string>("query") ?? parameters.Value<string>("name");
                    var tag = parameters.Value<string>("tag");
                    var matches = ObjectResolver.FindGameObjects(query, tag, parameters.Value<bool?>("includeInactive") ?? true).ToList();
                    return new CommandSuccessResponse("Scene search results.", new
                    {
                        count = matches.Count,
                        results = ObjectSnapshotter.SnapshotMatches(matches, parameters.Value<bool?>("includeComponents") ?? false)
                    });
                default:
                    return new CommandErrorResponse("Unsupported scene action '" + action + "'.");
            }
        }
    }
}
