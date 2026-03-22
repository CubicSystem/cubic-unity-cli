using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace Cubix.UnityCli
{
    [CubixCliCommand(Group = "runtime", Name = "runtime", Description = "Inspect or mutate runtime state while playing.")]
    internal sealed class RuntimeCommand : ICubixCliCommandHandler, ICubixCliPreflightHandler
    {
        public IEnumerable<CommandDefinition> DescribeActions()
        {
            yield return new CommandDefinition
            {
                Action = "state",
                Description = "Read play mode runtime state.",
                Tags = CommandMetadata.Tags(CommandTags.Core, CommandTags.Runtime, CommandTags.Diagnostics)
            };
            yield return new CommandDefinition
            {
                Action = "inspect",
                Description = "Inspect a runtime object or component.",
                Tags = CommandMetadata.Tags(CommandTags.Runtime, CommandTags.Object),
                Parameters = CommandMetadata.Parameters(
                    CommandMetadata.Parameter("target", "string", true, "Target runtime object."),
                    CommandMetadata.Parameter("component", "string", false, "Optional component type name."))
            };
            yield return new CommandDefinition
            {
                Action = "mutate",
                Description = "Mutate a runtime object or component.",
                Tags = CommandMetadata.Tags(CommandTags.Runtime, CommandTags.Object, CommandTags.Unsafe),
                SafetyLevel = CommandSafetyLevels.Destructive,
                SupportsPreflight = true,
                Parameters = CommandMetadata.Parameters(
                    CommandMetadata.Parameter("target", "string", true, "Target runtime object."),
                    CommandMetadata.Parameter("component", "string", false, "Optional component type name."),
                    CommandMetadata.Parameter("member", "string", false, "Single member name."),
                    CommandMetadata.Parameter("value", "any", false, "Single member value."),
                    CommandMetadata.Parameter("values", "object", false, "Multiple member values."),
                    CommandMetadata.Parameter("active", "boolean", false, "Runtime active state."),
                    CommandMetadata.Parameter("position", "vector3", false, "World position."),
                    CommandMetadata.Parameter("localPosition", "vector3", false, "Local position."),
                    CommandMetadata.Parameter("localScale", "vector3", false, "Local scale."),
                    CommandMetadata.Parameter("rotationEuler", "vector3", false, "World rotation euler."),
                    CommandMetadata.Parameter("localRotationEuler", "vector3", false, "Local rotation euler."))
            };
        }

        public object Execute(string action, JObject parameters)
        {
            switch (action)
            {
                case "state":
                    return new CommandSuccessResponse("Runtime state.", BuildState());
                case "inspect":
                    return Inspect(parameters);
                case "mutate":
                    return Mutate(parameters);
                default:
                    return new CommandErrorResponse("Unsupported runtime action '" + action + "'.");
            }
        }

        public CommandPreflightResult Preflight(string action, JObject parameters)
        {
            switch (action)
            {
                case "mutate":
                    return ValidateMutation(parameters);
                default:
                    return CommandMetadata.Success("runtime." + action, "No preflight issues.");
            }
        }

        private static object BuildState()
        {
            return new
            {
                isPlaying = EditorApplication.isPlaying,
                isPaused = EditorApplication.isPaused,
                frameCount = Time.frameCount,
                timeScale = Time.timeScale,
                realtimeSinceStartup = Time.realtimeSinceStartup
            };
        }

        private static object Inspect(JObject parameters)
        {
            if (!EditorApplication.isPlaying)
            {
                return new CommandErrorResponse("Play mode is required for runtime inspection.");
            }

            var gameObject = ObjectResolver.ResolveGameObject(parameters, true);
            if (gameObject == null)
            {
                return new CommandErrorResponse("Could not resolve target GameObject.");
            }

            if (string.IsNullOrWhiteSpace(parameters.Value<string>("component")))
            {
                return new CommandSuccessResponse("Runtime GameObject.", ObjectSnapshotter.SnapshotGameObject(gameObject, true, 0, 2));
            }

            var error = ObjectCommand.TryResolveComponent(parameters, out _, out var component);
            if (error != null)
            {
                return error;
            }

            return new CommandSuccessResponse("Runtime component.", new
            {
                gameObject = ObjectSnapshotter.SnapshotGameObject(gameObject, false, 0, 0),
                component = ObjectSnapshotter.SnapshotComponent(component)
            });
        }

        private static object Mutate(JObject parameters)
        {
            if (!EditorApplication.isPlaying)
            {
                return new CommandErrorResponse("Play mode is required for runtime mutation.");
            }

            var gameObject = ObjectResolver.ResolveGameObject(parameters, true);
            if (gameObject == null)
            {
                return new CommandErrorResponse("Could not resolve target GameObject.");
            }

            Undo.RecordObject(gameObject.transform, "Runtime Mutate");
            ObjectCommand.ApplyTransformMutations(gameObject.transform, parameters);

            var active = parameters.Value<bool?>("active");
            if (active.HasValue)
            {
                gameObject.SetActive(active.Value);
            }

            if (!string.IsNullOrWhiteSpace(parameters.Value<string>("component")))
            {
                var error = ObjectCommand.TryResolveComponent(parameters, out _, out var component);
                if (error != null)
                {
                    return error;
                }

                Undo.RecordObject(component, "Runtime Component Mutate");
                var values = parameters["values"] as JObject;
                if (values != null)
                {
                    ReflectionMemberAccess.WriteMembers(component, values);
                }
                else if (!string.IsNullOrWhiteSpace(parameters.Value<string>("member")))
                {
                    ReflectionMemberAccess.WriteMember(component, parameters.Value<string>("member"), parameters["value"]);
                }
            }

            return new CommandSuccessResponse("Runtime mutation applied.", ObjectSnapshotter.SnapshotGameObject(gameObject, true, 0, 2));
        }

        private static CommandPreflightResult ValidateMutation(JObject parameters)
        {
            var issues = new List<CommandPreflightIssue>();
            if (!EditorApplication.isPlaying)
            {
                issues.Add(CommandMetadata.Issue("error", "Play mode is required for runtime mutation.", "play_mode_required"));
            }

            if (ObjectResolver.ResolveGameObject(parameters, true) == null)
            {
                issues.Add(CommandMetadata.Issue("error", "Could not resolve target GameObject.", "missing_target"));
            }

            if (!string.IsNullOrWhiteSpace(parameters.Value<string>("component")))
            {
                var componentError = ObjectCommand.TryResolveComponent(parameters, out _, out _);
                if (componentError != null)
                {
                    issues.Add(CommandMetadata.Issue("error", componentError.message, "missing_component"));
                }
            }

            var result = CommandMetadata.Result("runtime.mutate", issues.ToArray());
            if (issues.Count == 0)
            {
                result.summary = "Runtime mutation can execute.";
            }

            return result;
        }
    }
}
