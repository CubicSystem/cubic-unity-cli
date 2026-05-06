using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Cubix.UnityCli
{
    internal static class ObjectSnapshotter
    {
        private const int MaxSnapshotMembers = 32;
        private const double SnapshotMemberBudgetMilliseconds = 50.0d;

        public static object SnapshotScene(Scene scene, bool includeComponents = false, int maxDepth = 6)
        {
            var roots = new List<object>();
            if (scene.IsValid() && scene.isLoaded)
            {
                foreach (var root in scene.GetRootGameObjects())
                {
                    roots.Add(SnapshotGameObject(root, includeComponents, 0, maxDepth));
                }
            }

            return new
            {
                name = scene.name,
                path = scene.path,
                isLoaded = scene.isLoaded,
                rootCount = roots.Count,
                roots
            };
        }

        public static object SnapshotGameObject(GameObject gameObject, bool includeComponents = false, int depth = 0, int maxDepth = 4)
        {
            if (gameObject == null)
            {
                return null;
            }

            var transform = gameObject.transform;
            var components = includeComponents
                ? gameObject.GetComponents<Component>()
                    .Where(component => component != null)
                    .Select(SnapshotComponent)
                    .ToList()
                : null;

            var children = new List<object>();
            if (depth < maxDepth)
            {
                foreach (Transform child in transform)
                {
                    children.Add(SnapshotGameObject(child.gameObject, includeComponents, depth + 1, maxDepth));
                }
            }
            else if (transform.childCount > 0)
            {
                children.Add("...truncated...");
            }

            return new
            {
                name = gameObject.name,
                path = ObjectResolver.GetHierarchyPath(transform),
                scenePath = ObjectResolver.GetSceneQualifiedPath(transform),
                instanceId = gameObject.GetInstanceID(),
                activeSelf = gameObject.activeSelf,
                activeInHierarchy = gameObject.activeInHierarchy,
                tag = gameObject.tag,
                layer = gameObject.layer,
                isStatic = gameObject.isStatic,
                transform = new
                {
                    position = new { x = transform.position.x, y = transform.position.y, z = transform.position.z },
                    localPosition = new { x = transform.localPosition.x, y = transform.localPosition.y, z = transform.localPosition.z },
                    rotation = new { x = transform.rotation.x, y = transform.rotation.y, z = transform.rotation.z, w = transform.rotation.w },
                    localScale = new { x = transform.localScale.x, y = transform.localScale.y, z = transform.localScale.z }
                },
                components,
                children
            };
        }

        public static object SnapshotComponent(Component component)
        {
            return SnapshotComponent(component, includeValues: false);
        }

        public static object SnapshotComponent(Component component, bool includeValues, string memberName = null, IEnumerable<string> memberNames = null)
        {
            if (component == null)
            {
                return null;
            }

            var enabled = component is Behaviour behaviour ? (bool?)behaviour.enabled : null;
            var snapshot = new Dictionary<string, object>
            {
                ["type"] = component.GetType().Name,
                ["fullType"] = component.GetType().FullName,
                ["instanceId"] = component.GetInstanceID(),
                ["enabled"] = enabled
            };

            if (includeValues)
            {
                snapshot["values"] = SnapshotMembers(component, memberName, memberNames);
            }

            return snapshot;
        }

        public static object SnapshotMembers(object instance, string memberName = null, IEnumerable<string> memberNames = null)
        {
            if (instance == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(memberName))
            {
                return SnapshotSelectedMembers(instance, new[] { memberName });
            }

            if (memberNames != null)
            {
                return SnapshotSelectedMembers(instance, memberNames);
            }

            var output = new Dictionary<string, object>();
            var startedAtUtc = System.DateTime.UtcNow;
            var count = 0;
            foreach (var member in ReflectionMemberAccess.ListMembers(instance.GetType()))
            {
                if (count >= MaxSnapshotMembers)
                {
                    output["..."] = "...truncated...";
                    break;
                }

                if ((System.DateTime.UtcNow - startedAtUtc).TotalMilliseconds >= SnapshotMemberBudgetMilliseconds)
                {
                    output["..."] = "<time-budget-exceeded>";
                    break;
                }

                try
                {
                    var value = ReflectionMemberAccess.ReadMember(instance, member);
                    output[member] = ValueSerializer.Serialize(value);
                }
                catch
                {
                    output[member] = "<unavailable>";
                }

                count++;
            }

            return output;
        }

        private static Dictionary<string, object> SnapshotSelectedMembers(object instance, IEnumerable<string> memberNames)
        {
            var output = new Dictionary<string, object>();
            foreach (var rawMemberName in memberNames)
            {
                if (string.IsNullOrWhiteSpace(rawMemberName))
                {
                    continue;
                }

                var memberName = rawMemberName.Trim();
                try
                {
                    var value = ReflectionMemberAccess.ReadMember(instance, memberName);
                    output[memberName] = ValueSerializer.Serialize(value);
                }
                catch
                {
                    output[memberName] = "<unavailable>";
                }
            }

            return output;
        }

        public static JArray SnapshotMatches(IEnumerable<GameObject> matches, bool includeComponents = false)
        {
            var array = new JArray();
            foreach (var match in matches)
            {
                array.Add(JToken.FromObject(SnapshotGameObject(match, includeComponents, 0, 2)));
            }

            return array;
        }
    }
}
