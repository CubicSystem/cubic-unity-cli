using System;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace CubicEngine.UnityCli
{
    [InitializeOnLoad]
    internal static class PlayModeTransitionController
    {
        [Serializable]
        internal sealed class PlayModeTransitionRecord
        {
            public string id;
            public string state;
            public bool desiredIsPlaying;
            public string startedAtUtc;
            public string updatedAtUtc;
            public int timeoutMs;
            public bool? success;
            public string message;
        }

        private const string TransitionKey = "cubix_cli.playmode.transition";

        static PlayModeTransitionController()
        {
            EditorApplication.update += Pump;
            EditorApplication.playModeStateChanged += HandlePlayModeStateChanged;
            CleanupExpiredTransition();
        }

        public static object StartTransition(JObject parameters, bool desiredIsPlaying)
        {
            var timeoutMs = Math.Max(parameters?.Value<int?>("timeoutMs") ?? 120000, 1000);
            var existing = LoadTransition();
            if (existing != null && !existing.success.HasValue)
            {
                if (existing.desiredIsPlaying == desiredIsPlaying)
                {
                    existing.timeoutMs = timeoutMs;
                    existing.updatedAtUtc = DateTime.UtcNow.ToString("o");
                    SaveTransition(existing);
                    return BuildTransitionState(existing);
                }

                existing.state = "canceled";
                existing.success = false;
                existing.message = "Play mode request was superseded by a new command.";
                existing.updatedAtUtc = DateTime.UtcNow.ToString("o");
                SaveTransition(existing);
            }

            var nowUtc = DateTime.UtcNow.ToString("o");
            var record = new PlayModeTransitionRecord
            {
                id = Guid.NewGuid().ToString("N"),
                state = "queued",
                desiredIsPlaying = desiredIsPlaying,
                startedAtUtc = nowUtc,
                updatedAtUtc = nowUtc,
                timeoutMs = timeoutMs,
                message = desiredIsPlaying ? "Play mode start queued." : "Play mode stop queued."
            };

            if (EditorApplication.isPlaying == desiredIsPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                record.state = "completed";
                record.success = true;
                record.message = desiredIsPlaying ? "Editor is already in play mode." : "Editor is already stopped.";
            }

            SaveTransition(record);
            return BuildTransitionState(record);
        }

        public static object GetCurrentJob()
        {
            return BuildTransitionState(RefreshTransitionState());
        }

        public static bool HasPendingTransition()
        {
            var record = RefreshTransitionState();
            return record != null && !record.success.HasValue;
        }

        private static void Pump()
        {
            RefreshTransitionState();
        }

        private static void HandlePlayModeStateChanged(PlayModeStateChange change)
        {
            var record = LoadTransition();
            if (record == null || record.success.HasValue)
            {
                return;
            }

            switch (change)
            {
                case PlayModeStateChange.ExitingEditMode:
                    if (record.desiredIsPlaying)
                    {
                        UpdatePendingState(record, "running", "Unity is entering play mode.");
                    }
                    break;
                case PlayModeStateChange.EnteredPlayMode:
                    if (record.desiredIsPlaying)
                    {
                        Complete(record, true, "Play mode started.");
                    }
                    break;
                case PlayModeStateChange.ExitingPlayMode:
                    if (!record.desiredIsPlaying)
                    {
                        UpdatePendingState(record, "running", "Unity is exiting play mode.");
                    }
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    if (!record.desiredIsPlaying)
                    {
                        Complete(record, true, "Play mode stopped.");
                    }
                    break;
            }
        }

        private static void CleanupExpiredTransition()
        {
            var record = LoadTransition();
            if (record == null)
            {
                return;
            }

            if (!TryGetStartedAtUtc(record, out var startedAtUtc))
            {
                SessionState.EraseString(TransitionKey);
                return;
            }

            if (DateTime.UtcNow > startedAtUtc.AddHours(4))
            {
                SessionState.EraseString(TransitionKey);
            }
        }

        private static PlayModeTransitionRecord LoadTransition()
        {
            var json = SessionState.GetString(TransitionKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<PlayModeTransitionRecord>(json);
            }
            catch
            {
                SessionState.EraseString(TransitionKey);
                return null;
            }
        }

        private static void SaveTransition(PlayModeTransitionRecord record)
        {
            SessionState.SetString(TransitionKey, JsonConvert.SerializeObject(record));
        }

        private static PlayModeTransitionRecord RefreshTransitionState()
        {
            var record = LoadTransition();
            if (record == null || record.success.HasValue)
            {
                return record;
            }

            var playModeTransitionInProgress = EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode;

            var nowUtc = DateTime.UtcNow;
            if (TryGetStartedAtUtc(record, out var startedAtUtc) &&
                nowUtc > startedAtUtc.AddMilliseconds(record.timeoutMs))
            {
                Fail(record, "timed_out", record.desiredIsPlaying
                    ? "Unity did not enter play mode before the timeout."
                    : "Unity did not exit play mode before the timeout.");
                return record;
            }

            if (EditorApplication.isPlaying == record.desiredIsPlaying && !playModeTransitionInProgress)
            {
                Complete(record, true, record.desiredIsPlaying ? "Play mode started." : "Play mode stopped.");
                return record;
            }

            if (ConnectionService.IsReloading)
            {
                return UpdatePendingState(record, "queued", "Waiting for Unity to reconnect after domain reload.");
            }

            if (EditorApplication.isUpdating)
            {
                return UpdatePendingState(record, "queued", "Waiting for Unity asset refresh to finish before changing play mode.");
            }

            if (EditorApplication.isCompiling)
            {
                return UpdatePendingState(record, "queued", "Waiting for Unity script compilation to finish before changing play mode.");
            }

            if (CompilationAwaiter.HasPendingVerifyJob())
            {
                return UpdatePendingState(record, "queued", "Waiting for verify to finish before changing play mode.");
            }

            if (TestRunController.HasPendingRun())
            {
                return UpdatePendingState(record, "queued", "Waiting for the current Unity test run to finish before changing play mode.");
            }

            if (playModeTransitionInProgress)
            {
                return UpdatePendingState(
                    record,
                    "running",
                    record.desiredIsPlaying ? "Waiting for Unity to enter play mode." : "Waiting for Unity to exit play mode.");
            }

            try
            {
                EditorApplication.isPaused = false;
                EditorApplication.isPlaying = record.desiredIsPlaying;
                return UpdatePendingState(
                    record,
                    "running",
                    record.desiredIsPlaying ? "Requesting Unity to enter play mode." : "Requesting Unity to exit play mode.");
            }
            catch (Exception exception)
            {
                Fail(record, "failed", "Could not change Unity play mode: " + exception.Message);
                return record;
            }
        }

        private static void Complete(PlayModeTransitionRecord record, bool success, string message)
        {
            record.state = "completed";
            record.success = success;
            record.message = message;
            record.updatedAtUtc = DateTime.UtcNow.ToString("o");
            SaveTransition(record);
        }

        private static void Fail(PlayModeTransitionRecord record, string state, string message)
        {
            record.state = state;
            record.success = false;
            record.message = message;
            record.updatedAtUtc = DateTime.UtcNow.ToString("o");
            SaveTransition(record);
        }

        private static PlayModeTransitionRecord UpdatePendingState(PlayModeTransitionRecord record, string state, string message)
        {
            if (!string.Equals(record.state, state, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(record.message, message, StringComparison.Ordinal))
            {
                record.state = state;
                record.message = message;
                record.updatedAtUtc = DateTime.UtcNow.ToString("o");
                SaveTransition(record);
            }

            return record;
        }

        private static bool TryGetStartedAtUtc(PlayModeTransitionRecord record, out DateTime startedAtUtc)
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

        private static object BuildTransitionState(PlayModeTransitionRecord record)
        {
            if (record == null)
            {
                return null;
            }

            var scene = SceneManager.GetActiveScene();
            return new
            {
                id = record.id,
                state = record.state,
                desiredIsPlaying = record.desiredIsPlaying,
                startedAtUtc = record.startedAtUtc,
                updatedAtUtc = record.updatedAtUtc,
                timeoutMs = record.timeoutMs,
                success = record.success,
                message = record.message,
                isPlaying = EditorApplication.isPlaying,
                isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
                playModeTransitionPending = EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode,
                isPaused = EditorApplication.isPaused,
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating,
                activeScene = new
                {
                    name = scene.name,
                    path = scene.path,
                    isLoaded = scene.isLoaded
                }
            };
        }
    }
}
