using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace CubicEngine.UnityCli
{
    [CubixCliCommand(Group = "object", Name = "object", Description = "Create and mutate scene objects and components.")]
    internal sealed class ObjectCommand : ICubixCliCommandHandler
    {
        public IEnumerable<CommandDefinition> DescribeActions()
        {
            yield return new CommandDefinition
            {
                Action = "create",
                Description = "Create a GameObject in the active scene.",
                Tags = CommandMetadata.Tags(CommandTags.Core, CommandTags.Scene, CommandTags.Object),
                Parameters = CommandMetadata.Parameters(
                    CommandMetadata.Parameter("name", "string", true, "GameObject name."),
                    CommandMetadata.Parameter("parent", "string", false, "Optional parent target."),
                    CommandMetadata.Parameter("active", "boolean", false, "Initial active state.", true),
                    CommandMetadata.Parameter("select", "boolean", false, "Select the created object.", true),
                    CommandMetadata.Parameter("position", "vector3", false, "World position."),
                    CommandMetadata.Parameter("localPosition", "vector3", false, "Local position."),
                    CommandMetadata.Parameter("localScale", "vector3", false, "Local scale."),
                    CommandMetadata.Parameter("rotationEuler", "vector3", false, "World rotation euler."),
                    CommandMetadata.Parameter("localRotationEuler", "vector3", false, "Local rotation euler."))
            };
            yield return new CommandDefinition
            {
                Action = "set-active",
                Description = "Change a GameObject active state.",
                Tags = CommandMetadata.Tags(CommandTags.Object, CommandTags.Scene),
                Parameters = CommandMetadata.Parameters(
                    CommandMetadata.Parameter("target", "string", true, "Target object path or name."),
                    CommandMetadata.Parameter("active", "boolean", true, "Desired active state."))
            };
            yield return new CommandDefinition
            {
                Action = "set-parent",
                Description = "Reparent a GameObject.",
                Tags = CommandMetadata.Tags(CommandTags.Object, CommandTags.Scene),
                Parameters = CommandMetadata.Parameters(
                    CommandMetadata.Parameter("target", "string", true, "Target object path or name."),
                    CommandMetadata.Parameter("parent", "string", false, "New parent path or name. Omit to unparent."))
            };
            yield return new CommandDefinition
            {
                Action = "component-get",
                Description = "Read component metadata or explicit members from a GameObject.",
                Tags = CommandMetadata.Tags(CommandTags.Object, CommandTags.Scene),
                Parameters = CommandMetadata.Parameters(
                    CommandMetadata.Parameter("target", "string", true, "Target object path or name."),
                    CommandMetadata.Parameter("component", "string", true, "Component type name."),
                    CommandMetadata.Parameter("member", "string", false, "Optional member name to read explicitly."),
                    CommandMetadata.Parameter("readAllMembers", "boolean", false, "Opt-in broad reflection read of all public members.", false),
                    CommandMetadata.Parameter("includeInactive", "boolean", false, "Allow inactive objects.", false))
            };
            yield return new CommandDefinition
            {
                Action = "component-set",
                Description = "Write component members on a GameObject.",
                Tags = CommandMetadata.Tags(CommandTags.Object, CommandTags.Scene, CommandTags.Unsafe),
                SafetyLevel = CommandSafetyLevels.Destructive,
                Parameters = CommandMetadata.Parameters(
                    CommandMetadata.Parameter("target", "string", true, "Target object path or name."),
                    CommandMetadata.Parameter("component", "string", true, "Component type name."),
                    CommandMetadata.Parameter("member", "string", false, "Single member name."),
                    CommandMetadata.Parameter("value", "any", false, "Single member value."),
                    CommandMetadata.Parameter("values", "object", false, "Multiple member values."),
                    CommandMetadata.Parameter("includeInactive", "boolean", false, "Allow inactive objects.", false))
            };
        }

        public object Execute(string action, JObject parameters)
        {
            switch (action)
            {
                case "create":
                    return Create(parameters);
                case "set-active":
                    return SetActive(parameters);
                case "set-parent":
                    return SetParent(parameters);
                case "component-get":
                    return ComponentGet(parameters);
                case "component-set":
                    return ComponentSet(parameters);
                default:
                    return new CommandErrorResponse("Unsupported object action '" + action + "'.");
            }
        }

        internal static CommandErrorResponse TryResolveComponent(JObject parameters, out GameObject gameObject, out Component component)
        {
            gameObject = ObjectResolver.ResolveGameObject(parameters, parameters?.Value<bool?>("includeInactive") ?? true);
            if (gameObject == null)
            {
                component = null;
                return new CommandErrorResponse("Could not resolve target GameObject.");
            }

            var componentName = parameters?.Value<string>("component");
            if (string.IsNullOrWhiteSpace(componentName))
            {
                component = null;
                return new CommandErrorResponse("A component name is required.");
            }

            component = ObjectResolver.ResolveComponent(gameObject, componentName);
            if (component == null)
            {
                return new CommandErrorResponse("Could not resolve component '" + componentName + "' on '" + gameObject.name + "'.");
            }

            return null;
        }

        internal static void ApplyTransformMutations(Transform transform, JObject parameters)
        {
            var position = parameters["position"];
            if (position != null)
            {
                transform.position = VectorParsing.ReadVector3(position, transform.position);
            }

            var localPosition = parameters["localPosition"];
            if (localPosition != null)
            {
                transform.localPosition = VectorParsing.ReadVector3(localPosition, transform.localPosition);
            }

            var localScale = parameters["localScale"];
            if (localScale != null)
            {
                transform.localScale = VectorParsing.ReadVector3(localScale, transform.localScale);
            }

            var rotationEuler = parameters["rotationEuler"];
            if (rotationEuler != null)
            {
                transform.rotation = Quaternion.Euler(VectorParsing.ReadVector3(rotationEuler, transform.rotation.eulerAngles));
            }

            var localRotationEuler = parameters["localRotationEuler"];
            if (localRotationEuler != null)
            {
                transform.localRotation = Quaternion.Euler(VectorParsing.ReadVector3(localRotationEuler, transform.localRotation.eulerAngles));
            }
        }

        internal static void MarkDirty(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return;
            }

            EditorUtility.SetDirty(gameObject);
            if (gameObject.scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(gameObject.scene);
            }
        }

        private static object Create(JObject parameters)
        {
            var name = parameters.Value<string>("name") ?? "GameObject";
            var gameObject = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(gameObject, "Create " + name);

            var parentTarget = parameters.Value<string>("parent");
            if (!string.IsNullOrWhiteSpace(parentTarget))
            {
                var parent = ObjectResolver.ResolveGameObject(parentTarget, true);
                if (parent != null)
                {
                    gameObject.transform.SetParent(parent.transform, false);
                }
            }

            ApplyTransformMutations(gameObject.transform, parameters);
            gameObject.SetActive(parameters.Value<bool?>("active") ?? true);
            if (parameters.Value<bool?>("select") ?? true)
            {
                Selection.activeGameObject = gameObject;
            }

            MarkDirty(gameObject);
            return new CommandSuccessResponse("GameObject created.", ObjectSnapshotter.SnapshotGameObject(gameObject, false, 0, 1));
        }

        private static object SetActive(JObject parameters)
        {
            var gameObject = ObjectResolver.ResolveGameObject(parameters, true);
            if (gameObject == null)
            {
                return new CommandErrorResponse("Could not resolve target GameObject.");
            }

            var active = parameters.Value<bool?>("active");
            if (!active.HasValue)
            {
                return new CommandErrorResponse("An 'active' boolean is required.");
            }

            Undo.RecordObject(gameObject, "Set Active");
            gameObject.SetActive(active.Value);
            MarkDirty(gameObject);
            return new CommandSuccessResponse("GameObject active state updated.", ObjectSnapshotter.SnapshotGameObject(gameObject, false, 0, 1));
        }

        private static object SetParent(JObject parameters)
        {
            var gameObject = ObjectResolver.ResolveGameObject(parameters, true);
            if (gameObject == null)
            {
                return new CommandErrorResponse("Could not resolve target GameObject.");
            }

            var parentTarget = parameters.Value<string>("parent");
            var parent = string.IsNullOrWhiteSpace(parentTarget) ? null : ObjectResolver.ResolveGameObject(parentTarget, true);
            Undo.SetTransformParent(gameObject.transform, parent == null ? null : parent.transform, "Set Parent");
            MarkDirty(gameObject);
            return new CommandSuccessResponse("GameObject parent updated.", ObjectSnapshotter.SnapshotGameObject(gameObject, false, 0, 1));
        }

        private static object ComponentGet(JObject parameters)
        {
            var error = TryResolveComponent(parameters, out var gameObject, out var component);
            if (error != null)
            {
                return error;
            }

            var member = parameters.Value<string>("member");
            var readAllMembers = parameters.Value<bool?>("readAllMembers") ?? false;
            var includeValues = readAllMembers || !string.IsNullOrWhiteSpace(member);
            return new CommandSuccessResponse("Component values.", new
            {
                gameObject = ObjectSnapshotter.SnapshotGameObject(gameObject, false, 0, 0),
                component = ObjectSnapshotter.SnapshotComponent(component, false),
                values = includeValues
                    ? (readAllMembers
                        ? ObjectSnapshotter.SnapshotMembers(component)
                        : ObjectSnapshotter.SnapshotMembers(component, member))
                    : null,
                hint = includeValues ? null : "Specify 'member' or set 'readAllMembers' to read component values safely."
            });
        }

        private static object ComponentSet(JObject parameters)
        {
            var error = TryResolveComponent(parameters, out var gameObject, out var component);
            if (error != null)
            {
                return error;
            }

            Undo.RecordObject(component, "Set Component Value");
            var values = parameters["values"] as JObject;
            if (values != null)
            {
                ReflectionMemberAccess.WriteMembers(component, values);
            }
            else
            {
                var member = parameters.Value<string>("member");
                if (string.IsNullOrWhiteSpace(member))
                {
                    return new CommandErrorResponse("Either 'values' or 'member' must be provided.");
                }

                ReflectionMemberAccess.WriteMember(component, member, parameters["value"]);
            }

            EditorUtility.SetDirty(component);
            MarkDirty(gameObject);
            var changedMembers = values != null ? values.Properties().Select(property => property.Name) : null;
            return new CommandSuccessResponse("Component updated.", new
            {
                gameObject = ObjectSnapshotter.SnapshotGameObject(gameObject, false, 0, 0),
                component = ObjectSnapshotter.SnapshotComponent(
                    component,
                    includeValues: values != null || !string.IsNullOrWhiteSpace(parameters.Value<string>("member")),
                    memberName: parameters.Value<string>("member"),
                    memberNames: changedMembers)
            });
        }
    }
}
