using System;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace CubicEngine.UnityCli
{
    [InitializeOnLoad]
    internal static class SceneOpenController
    {
        [Serializable]
        internal sealed class SceneOpenRecord
        {
            public string id;
            public string state;
            public string scenePath;
            public string sceneName;
            public bool sceneIsLoaded;
            public int sceneRootCount;
            public string startedAtUtc;
            public string updatedAtUtc;
            public int timeoutMs;
            public bool? success;
            public string message;
        }

        private const string SceneOpenKey = "cubic_cli.scene.open";

        static SceneOpenController()
        {
            EditorApplication.update += Pump;
            CleanupExpiredRequest();
        }

        public static object StartOpen(JObject parameters)
        {
            var scenePath = ObjectResolver.NormalizeAssetPath(parameters?.Value<string>("path"));
            var timeoutMs = Math.Max(parameters?.Value<int?>("timeoutMs") ?? 120000, 1000);
            var existing = LoadRequest();
            if (existing != null && !existing.success.HasValue)
            {
                if (string.Equals(existing.scenePath, scenePath, StringComparison.OrdinalIgnoreCase))
                {
                    existing.timeoutMs = timeoutMs;
                    existing.updatedAtUtc = DateTime.UtcNow.ToString("o");
                    SaveRequest(existing);
                    return BuildState(existing);
                }

                throw new InvalidOperationException("A scene open request is already in progress.");
            }

            var nowUtc = DateTime.UtcNow.ToString("o");
            var record = new SceneOpenRecord
            {
                id = Guid.NewGuid().ToString("N"),
                state = "queued",
                scenePath = scenePath,
                startedAtUtc = nowUtc,
                updatedAtUtc = nowUtc,
                timeoutMs = timeoutMs,
                message = "Scene open queued."
            };

            var activeScene = SceneManager.GetActiveScene();
            if (string.Equals(activeScene.path, scenePath, StringComparison.OrdinalIgnoreCase) &&
                activeScene.isLoaded &&
                !EditorApplication.isUpdating &&
                !EditorApplication.isCompiling)
            {
                Complete(record, activeScene, "Scene is already open.");
            }
            else
            {
                SaveRequest(record);
                Pump();
            }

            return BuildState(LoadRequest() ?? record);
        }

        public static object GetCurrentJob()
        {
            return BuildState(RefreshCurrentState(allowStart: false));
        }

        public static bool HasPendingOpen()
        {
            var record = RefreshCurrentState(allowStart: false);
            return record != null && !record.success.HasValue;
        }

        public static string GetPendingMessage()
        {
            var record = RefreshCurrentState(allowStart: false);
            return record != null && !record.success.HasValue ? record.message : null;
        }

        private static void Pump()
        {
            RefreshCurrentState(allowStart: true);
        }

        private static void CleanupExpiredRequest()
        {
            var record = LoadRequest();
            if (record == null)
            {
                return;
            }

            if (!TryGetStartedAtUtc(record, out var startedAtUtc))
            {
                SessionState.EraseString(SceneOpenKey);
                return;
            }

            if (DateTime.UtcNow > startedAtUtc.AddHours(4))
            {
                SessionState.EraseString(SceneOpenKey);
            }
        }

        private static SceneOpenRecord LoadRequest()
        {
            var json = SessionState.GetString(SceneOpenKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<SceneOpenRecord>(json);
            }
            catch
            {
                SessionState.EraseString(SceneOpenKey);
                return null;
            }
        }

        private static void SaveRequest(SceneOpenRecord record)
        {
            SessionState.SetString(SceneOpenKey, JsonConvert.SerializeObject(record));
        }

        private static SceneOpenRecord RefreshCurrentState(bool allowStart)
        {
            var record = LoadRequest();
            if (record == null || record.success.HasValue)
            {
                return record;
            }

            if (TryMarkTimedOut(record))
            {
                return record;
            }

            var activeScene = SceneManager.GetActiveScene();
            if (string.Equals(activeScene.path, record.scenePath, StringComparison.OrdinalIgnoreCase) &&
                activeScene.isLoaded &&
                !EditorApplication.isUpdating &&
                !EditorApplication.isCompiling)
            {
                Complete(record, activeScene, "Scene opened.");
                return record;
            }

            var waitingMessage = BuildQueuedMessage();
            if (!CanStartOpen())
            {
                UpdatePendingState(record, "queued", waitingMessage);
                return record;
            }

            if (!ValidateCurrentState(record.scenePath, out var validationMessage))
            {
                Fail(record, "failed", validationMessage);
                return record;
            }

            if (allowStart)
            {
                LaunchOpen(record);
            }

            return LoadRequest() ?? record;
        }

        private static bool TryMarkTimedOut(SceneOpenRecord record)
        {
            if (!TryGetStartedAtUtc(record, out var startedAtUtc))
            {
                return false;
            }

            if (DateTime.UtcNow <= startedAtUtc.AddMilliseconds(record.timeoutMs))
            {
                return false;
            }

            record.state = "timed_out";
            record.success = false;
            record.message = "Scene open timed out while waiting for completion.";
            record.updatedAtUtc = DateTime.UtcNow.ToString("o");
            SaveRequest(record);
            return true;
        }

        private static bool CanStartOpen()
        {
            return !ConnectionService.IsReloading &&
                   !EditorApplication.isCompiling &&
                   !EditorApplication.isUpdating &&
                   !EditorApplication.isPlaying &&
                   !EditorApplication.isPlayingOrWillChangePlaymode &&
                   !CompilationAwaiter.HasPendingVerifyJob() &&
                   !TestRunController.HasPendingRun() &&
                   !PlayModeTransitionController.HasPendingTransition();
        }

        private static string BuildQueuedMessage()
        {
            if (ConnectionService.IsReloading)
            {
                return "Waiting for Unity to reconnect after domain reload before opening the scene.";
            }

            if (EditorApplication.isUpdating)
            {
                return "Waiting for Unity asset refresh to finish before opening the scene.";
            }

            if (EditorApplication.isCompiling)
            {
                return "Waiting for Unity script compilation to finish before opening the scene.";
            }

            if (CompilationAwaiter.HasPendingVerifyJob())
            {
                return "Waiting for verify to finish before opening the scene.";
            }

            if (TestRunController.HasPendingRun())
            {
                return "Waiting for the current Unity test run to finish before opening the scene.";
            }

            if (PlayModeTransitionController.HasPendingTransition() ||
                EditorApplication.isPlaying ||
                EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return "Waiting for Unity to exit play mode before opening the scene.";
            }

            return "Scene open queued.";
        }

        private static bool ValidateCurrentState(string scenePath, out string validationMessage)
        {
            if (string.IsNullOrWhiteSpace(scenePath))
            {
                validationMessage = "A scene path is required.";
                return false;
            }

            if (!scenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                validationMessage = "Scene path must point to a .unity asset.";
                return false;
            }

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath) == null)
            {
                validationMessage = "Scene asset '" + scenePath + "' was not found.";
                return false;
            }

            var activeScene = SceneManager.GetActiveScene();
            if (activeScene.IsValid() &&
                activeScene.isDirty &&
                !string.Equals(activeScene.path, scenePath, StringComparison.OrdinalIgnoreCase))
            {
                validationMessage = "The active scene has unsaved changes. Save or discard them before opening another scene.";
                return false;
            }

            validationMessage = null;
            return true;
        }

        private static void LaunchOpen(SceneOpenRecord record)
        {
            try
            {
                record.state = "running";
                record.message = "Opening scene.";
                record.updatedAtUtc = DateTime.UtcNow.ToString("o");
                SaveRequest(record);

                var openedScene = EditorSceneManager.OpenScene(record.scenePath, OpenSceneMode.Single);
                Complete(record, openedScene, "Scene opened.");
            }
            catch (Exception exception)
            {
                Fail(record, "failed", "Could not open scene: " + exception.Message);
            }
        }

        private static void Complete(SceneOpenRecord record, Scene scene, string message)
        {
            record.state = "completed";
            record.success = true;
            record.message = message;
            record.updatedAtUtc = DateTime.UtcNow.ToString("o");
            record.sceneName = scene.name;
            record.scenePath = scene.path;
            record.sceneIsLoaded = scene.isLoaded;
            record.sceneRootCount = scene.rootCount;
            SaveRequest(record);
        }

        private static void Fail(SceneOpenRecord record, string state, string message)
        {
            record.state = state;
            record.success = false;
            record.message = message;
            record.updatedAtUtc = DateTime.UtcNow.ToString("o");
            SaveRequest(record);
        }

        private static SceneOpenRecord UpdatePendingState(SceneOpenRecord record, string state, string message)
        {
            if (!string.Equals(record.state, state, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(record.message, message, StringComparison.Ordinal))
            {
                record.state = state;
                record.message = message;
                record.updatedAtUtc = DateTime.UtcNow.ToString("o");
                SaveRequest(record);
            }

            return record;
        }

        private static bool TryGetStartedAtUtc(SceneOpenRecord record, out DateTime startedAtUtc)
        {
            if (!DateTime.TryParse(
                    record.startedAtUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out startedAtUtc))
            {
                startedAtUtc = default;
                return false;
            }

            return true;
        }

        private static object BuildState(SceneOpenRecord record)
        {
            if (record == null)
            {
                return new
                {
                    state = "idle",
                    success = true,
                    message = "No scene open request is pending."
                };
            }

            return new
            {
                id = record.id,
                state = record.state,
                path = record.scenePath,
                startedAtUtc = record.startedAtUtc,
                updatedAtUtc = record.updatedAtUtc,
                timeoutMs = record.timeoutMs,
                success = record.success,
                message = record.message,
                scene = string.IsNullOrWhiteSpace(record.scenePath)
                    ? null
                    : new
                    {
                        name = record.sceneName,
                        path = record.scenePath,
                        isLoaded = record.sceneIsLoaded,
                        rootCount = record.sceneRootCount
                    }
            };
        }
    }
}
