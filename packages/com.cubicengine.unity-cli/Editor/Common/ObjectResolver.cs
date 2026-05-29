using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CubicEngine.UnityCli
{
    internal static class ObjectResolver
    {
        public static GameObject ResolveGameObject(JObject parameters, bool includeInactive = true)
        {
            if (parameters == null)
            {
                return null;
            }

            var target = parameters.Value<string>("target")
                ?? parameters.Value<string>("path")
                ?? parameters.Value<string>("name");
            var instanceId = parameters.Value<int?>("instanceId");

            if (instanceId.HasValue)
            {
                var instance = EditorUtility.InstanceIDToObject(instanceId.Value);
                if (instance is GameObject gameObject)
                {
                    return gameObject;
                }

                if (instance is Component component)
                {
                    return component.gameObject;
                }
            }

            return ResolveGameObject(target, includeInactive);
        }

        public static GameObject ResolveGameObject(string target, bool includeInactive = true)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return Selection.activeGameObject;
            }

            if (int.TryParse(target, out var instanceId))
            {
                var instance = EditorUtility.InstanceIDToObject(instanceId);
                if (instance is GameObject byId)
                {
                    return byId;
                }

                if (instance is Component component)
                {
                    return component.gameObject;
                }
            }

            var normalizedTarget = target.Trim();
            var roots = EnumerateSceneObjects(includeInactive).ToList();

            if (normalizedTarget.Contains("/"))
            {
                var byPath = roots.FirstOrDefault(candidate =>
                    string.Equals(GetHierarchyPath(candidate.transform), normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(GetSceneQualifiedPath(candidate.transform), normalizedTarget, StringComparison.OrdinalIgnoreCase));
                if (byPath != null)
                {
                    return byPath;
                }
            }

            var byName = roots.FirstOrDefault(candidate =>
                string.Equals(candidate.name, normalizedTarget, StringComparison.OrdinalIgnoreCase));
            if (byName != null)
            {
                return byName;
            }

            return roots.FirstOrDefault(candidate =>
                string.Equals(GetSceneQualifiedPath(candidate.transform), normalizedTarget, StringComparison.OrdinalIgnoreCase));
        }

        public static Component ResolveComponent(GameObject gameObject, string componentTypeName)
        {
            if (gameObject == null || string.IsNullOrWhiteSpace(componentTypeName))
            {
                return null;
            }

            var componentType = ResolveComponentType(componentTypeName);
            return componentType == null ? null : gameObject.GetComponent(componentType);
        }

        public static Type ResolveComponentType(string componentTypeName)
        {
            if (string.IsNullOrWhiteSpace(componentTypeName))
            {
                return null;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (!typeof(Component).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    if (string.Equals(type.Name, componentTypeName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(type.FullName, componentTypeName, StringComparison.OrdinalIgnoreCase))
                    {
                        return type;
                    }
                }
            }

            return null;
        }

        public static IEnumerable<GameObject> FindGameObjects(string query, string tag = null, bool includeInactive = true)
        {
            return EnumerateSceneObjects(includeInactive).Where(candidate =>
            {
                if (!string.IsNullOrWhiteSpace(tag) && !candidate.CompareTag(tag))
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(query))
                {
                    return true;
                }

                return candidate.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    GetHierarchyPath(candidate.transform).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    GetSceneQualifiedPath(candidate.transform).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
            });
        }

        public static string NormalizeAssetPath(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return input;
            }

            var normalized = input.Replace('\\', '/');
            if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                return normalized;
            }

            var projectPath = ConnectorPaths.ProjectPath.Replace('\\', '/').TrimEnd('/');
            if (normalized.StartsWith(projectPath, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(projectPath.Length).TrimStart('/');
            }

            return normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                ? normalized
                : "Assets/" + normalized.TrimStart('/');
        }

        public static string GetHierarchyPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            return transform.parent == null
                ? transform.name
                : GetHierarchyPath(transform.parent) + "/" + transform.name;
        }

        public static string GetSceneQualifiedPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            return transform.gameObject.scene.name + "/" + GetHierarchyPath(transform);
        }

        public static IEnumerable<GameObject> EnumerateSceneObjects(bool includeInactive = true)
        {
            for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                var scene = SceneManager.GetSceneAt(sceneIndex);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    continue;
                }

                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var child in EnumerateHierarchy(root, includeInactive))
                    {
                        yield return child;
                    }
                }
            }

            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage == null)
            {
                yield break;
            }

            foreach (var root in prefabStage.scene.GetRootGameObjects())
            {
                foreach (var child in EnumerateHierarchy(root, includeInactive))
                {
                    yield return child;
                }
            }
        }

        private static IEnumerable<GameObject> EnumerateHierarchy(GameObject root, bool includeInactive)
        {
            if (root == null)
            {
                yield break;
            }

            if (includeInactive || root.activeInHierarchy)
            {
                yield return root;
            }

            foreach (Transform child in root.transform)
            {
                foreach (var nested in EnumerateHierarchy(child.gameObject, includeInactive))
                {
                    yield return nested;
                }
            }
        }
    }
}
