using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CubicEngine.UnityCli
{
    [CubixCliCommand(Group = "prefab", Name = "prefab", Description = "Instantiate and save prefab assets.")]
    internal sealed class PrefabCommand : ICubixCliCommandHandler, ICubixCliPreflightHandler
    {
        public IEnumerable<CommandDefinition> DescribeActions()
        {
            yield return new CommandDefinition
            {
                Action = "instantiate",
                Description = "Instantiate a prefab asset into the active scene.",
                Tags = CommandMetadata.Tags(CommandTags.Core, CommandTags.Prefab, CommandTags.Scene),
                Parameters = CommandMetadata.Parameters(
                    CommandMetadata.Parameter("path", "string", true, "Prefab asset path."),
                    CommandMetadata.Parameter("parent", "string", false, "Optional parent target."),
                    CommandMetadata.Parameter("name", "string", false, "Optional instance name override."),
                    CommandMetadata.Parameter("position", "vector3", false, "World position."),
                    CommandMetadata.Parameter("localPosition", "vector3", false, "Local position."),
                    CommandMetadata.Parameter("localScale", "vector3", false, "Local scale."),
                    CommandMetadata.Parameter("rotationEuler", "vector3", false, "World rotation euler."),
                    CommandMetadata.Parameter("localRotationEuler", "vector3", false, "Local rotation euler."))
            };
            yield return new CommandDefinition
            {
                Action = "save",
                Description = "Save a GameObject as a prefab asset.",
                Tags = CommandMetadata.Tags(CommandTags.Prefab, CommandTags.Assets, CommandTags.Unsafe),
                SafetyLevel = CommandSafetyLevels.Destructive,
                SupportsPreflight = true,
                Parameters = CommandMetadata.Parameters(
                    CommandMetadata.Parameter("target", "string", true, "Scene object to save."),
                    CommandMetadata.Parameter("path", "string", true, "Prefab asset path."))
            };
            yield return new CommandDefinition
            {
                Action = "connect",
                Description = "Save and connect a GameObject to a prefab asset path.",
                Tags = CommandMetadata.Tags(CommandTags.Prefab, CommandTags.Assets, CommandTags.Unsafe),
                SafetyLevel = CommandSafetyLevels.Destructive,
                SupportsPreflight = true,
                Parameters = CommandMetadata.Parameters(
                    CommandMetadata.Parameter("target", "string", true, "Scene object to save."),
                    CommandMetadata.Parameter("path", "string", true, "Prefab asset path."))
            };
        }

        public object Execute(string action, JObject parameters)
        {
            switch (action)
            {
                case "instantiate":
                    return Instantiate(parameters);
                case "save":
                case "connect":
                    return SaveAndConnect(parameters);
                default:
                    return new CommandErrorResponse("Unsupported prefab action '" + action + "'.");
            }
        }

        public CommandPreflightResult Preflight(string action, JObject parameters)
        {
            switch (action)
            {
                case "save":
                case "connect":
                    return ValidateSave(parameters, "prefab." + action);
                default:
                    return CommandMetadata.Success("prefab." + action, "No preflight issues.");
            }
        }

        private static object Instantiate(JObject parameters)
        {
            var assetPath = ObjectResolver.NormalizeAssetPath(parameters.Value<string>("path"));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null)
            {
                return new CommandErrorResponse("Could not load prefab at '" + assetPath + "'.");
            }

            var activeScene = SceneManager.GetActiveScene();
            var instance = PrefabUtility.InstantiatePrefab(prefab, activeScene) as GameObject;
            if (instance == null)
            {
                return new CommandErrorResponse("Prefab instantiation failed.");
            }

            var parentTarget = parameters.Value<string>("parent");
            if (!string.IsNullOrWhiteSpace(parentTarget))
            {
                var parent = ObjectResolver.ResolveGameObject(parentTarget, true);
                if (parent != null)
                {
                    instance.transform.SetParent(parent.transform, false);
                }
            }

            ObjectCommand.ApplyTransformMutations(instance.transform, parameters);
            var name = parameters.Value<string>("name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                instance.name = name;
            }

            ObjectCommand.MarkDirty(instance);
            return new CommandSuccessResponse("Prefab instantiated.", ObjectSnapshotter.SnapshotGameObject(instance, false, 0, 1));
        }

        private static object SaveAndConnect(JObject parameters)
        {
            if (EditorApplication.isPlaying)
            {
                return new CommandErrorResponse("Saving prefab assets is disabled during play mode.");
            }

            var gameObject = ObjectResolver.ResolveGameObject(parameters, true);
            if (gameObject == null)
            {
                return new CommandErrorResponse("Could not resolve target GameObject.");
            }

            var assetPath = ObjectResolver.NormalizeAssetPath(parameters.Value<string>("path"));
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return new CommandErrorResponse("A prefab asset path is required.");
            }

            var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(gameObject, assetPath, InteractionMode.AutomatedAction);
            if (prefab == null)
            {
                return new CommandErrorResponse("Saving prefab failed.");
            }

            AssetDatabase.Refresh();
            return new CommandSuccessResponse("Prefab saved.", new
            {
                assetPath,
                prefab = ValueSerializer.Serialize(prefab),
                instance = ObjectSnapshotter.SnapshotGameObject(gameObject, false, 0, 1)
            });
        }

        private static CommandPreflightResult ValidateSave(JObject parameters, string command)
        {
            var issues = new List<CommandPreflightIssue>();
            if (EditorApplication.isPlaying)
            {
                issues.Add(CommandMetadata.Issue("error", "Saving prefab assets is disabled during play mode.", "play_mode"));
            }

            if (ObjectResolver.ResolveGameObject(parameters, true) == null)
            {
                issues.Add(CommandMetadata.Issue("error", "Could not resolve target GameObject.", "missing_target"));
            }

            var assetPath = ObjectResolver.NormalizeAssetPath(parameters.Value<string>("path"));
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                issues.Add(CommandMetadata.Issue("error", "A prefab asset path is required.", "missing_path"));
            }
            else if (!assetPath.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(CommandMetadata.Issue("error", "Prefab path must be under Assets/.", "invalid_path"));
            }

            var result = CommandMetadata.Result(command, issues.ToArray());
            if (issues.Count == 0)
            {
                result.summary = "Prefab save/connect can execute.";
            }

            return result;
        }
    }
}
