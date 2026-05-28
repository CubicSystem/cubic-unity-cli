using System;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace CubicEngine.UnityCli
{
    [InitializeOnLoad]
    internal static class HeartbeatService
    {
        private static readonly object SnapshotLock = new object();
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        private static double _nextHeartbeatAt;
        private static object _cachedStatusSnapshot;

        static HeartbeatService()
        {
            ConnectorPaths.EnsureDirectories();
            EditorApplication.update += Pump;
            EditorApplication.quitting += CleanupNow;
        }

        public static object BuildStatusSnapshot()
        {
            var port = HttpServer.AdvertisedPort;
            var url = HttpServer.IsRunning ? HttpServer.Url : HttpServer.AdvertisedUrl;
            return BuildStatusSnapshot(port, url, HttpServer.IsRunning && !ConnectionService.IsReloading);
        }

        public static void RefreshNow()
        {
            _nextHeartbeatAt = 0d;
            if (HttpServer.IsRunning || ConnectionService.ShouldMaintainStatusFiles)
            {
                ConnectorPaths.EnsureDirectories();
                var snapshot = BuildStatusSnapshot();
                WriteSnapshotFiles(HttpServer.AdvertisedPort, HttpServer.IsRunning ? HttpServer.Url : HttpServer.AdvertisedUrl, snapshot);
            }
        }

        public static bool TryGetCachedStatusSnapshot(out object snapshot)
        {
            lock (SnapshotLock)
            {
                snapshot = _cachedStatusSnapshot;
                return snapshot != null;
            }
        }

        public static void RefreshBusyKeepAlive(CommandActivitySnapshot activity)
        {
            if (activity == null || (!activity.busy && activity.queuedCount <= 0))
            {
                return;
            }

            JObject snapshot;
            lock (SnapshotLock)
            {
                if (_cachedStatusSnapshot == null)
                {
                    return;
                }

                snapshot = JObject.FromObject(_cachedStatusSnapshot);
            }

            ApplyCommandActivity(snapshot, activity);
            snapshot["lastUpdatedUtc"] = DateTime.UtcNow.ToString("o");
            WriteSnapshotFiles(HttpServer.AdvertisedPort, HttpServer.IsRunning ? HttpServer.Url : HttpServer.AdvertisedUrl, snapshot);
        }

        public static void PrepareForReload()
        {
            ConnectorPaths.EnsureDirectories();

            var port = HttpServer.Port;
            var url = HttpServer.Url;
            if (port > 0 && !string.IsNullOrWhiteSpace(url))
            {
                var snapshot = BuildStatusSnapshot(port, url, false);
                WriteSnapshotFiles(port, url, snapshot);
                return;
            }

            lock (SnapshotLock)
            {
                _cachedStatusSnapshot = null;
            }
        }

        public static void CleanupNow()
        {
            TryDelete(ConnectorPaths.InstanceFilePath);
            TryDelete(ConnectorPaths.StatusFilePath());

            lock (SnapshotLock)
            {
                _cachedStatusSnapshot = null;
            }
        }

        private static object BuildStatusSnapshot(int port, string url, bool connected)
        {
            var verify = CompilationAwaiter.GetVerifyJob();
            var test = TestRunController.GetCurrentJob();
            var playMode = PlayModeTransitionController.GetCurrentJob();
            var sceneOpen = SceneOpenController.GetCurrentJob();
            var sceneOpenPending = SceneOpenController.HasPendingOpen();
            var commands = ToolDiscovery.GetCommandMetadata();
            var activeScene = BuildActiveSceneSnapshot();
            var reloading = ConnectionService.IsReloading;
            var connection = ConnectionService.GetSnapshot();
            var ready = connected &&
                        !reloading &&
                        !connection.busy &&
                        !EditorApplication.isCompiling &&
                        !EditorApplication.isUpdating &&
                        !CompilationAwaiter.HasPendingVerifyJob() &&
                        !sceneOpenPending &&
                        !TestRunController.HasPendingRun() &&
                        !PlayModeTransitionController.HasPendingTransition();
            var message = BuildStatusMessage(connected, reloading, connection, SceneOpenController.GetPendingMessage());
            return new
            {
                projectName = ConnectorPaths.ProjectName,
                projectPath = ConnectorPaths.ProjectPath,
                projectHash = ConnectorPaths.ProjectHash,
                port,
                url,
                editor = new
                {
                    isPlaying = EditorApplication.isPlaying,
                    isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
                    playModeTransitionPending = EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode,
                    isPaused = EditorApplication.isPaused,
                    isCompiling = EditorApplication.isCompiling,
                    isUpdating = EditorApplication.isUpdating
                },
                activeScene,
                ready,
                reloading,
                busy = connection.busy,
                busyCommand = connection.busyCommand,
                busyStale = connection.busyStale,
                busyStaleAfterMs = connection.busyStaleAfterMs,
                queuedCommands = connection.queuedCommands,
                queuedStartedAtUtc = connection.queuedStartedAtUtc,
                queuedDurationMs = connection.queuedDurationMs,
                message,
                verify,
                test,
                playMode,
                sceneOpen,
                commandCount = commands.Count,
                commands,
                connection,
                lastUpdatedUtc = DateTime.UtcNow.ToString("o")
            };
        }

        private static void Pump()
        {
            if (EditorApplication.timeSinceStartup < _nextHeartbeatAt)
            {
                return;
            }

            if (!HttpServer.IsRunning && !ConnectionService.ShouldMaintainStatusFiles)
            {
                return;
            }

            _nextHeartbeatAt = EditorApplication.timeSinceStartup + 1.0d;

            RefreshNow();
        }

        private static void WriteJson(string path, object payload)
        {
            var json = JsonConvert.SerializeObject(payload, Formatting.Indented);
            WriteJson(path, json);
        }

        private static bool WriteJson(string path, string json)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                    using (var writer = new StreamWriter(stream, Utf8NoBom))
                    {
                        writer.Write(json);
                    }

                    return true;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(15 * (attempt + 1));
                }
                catch (IOException)
                {
                    return false;
                }
            }

            return false;
        }

        private static object BuildActiveSceneSnapshot()
        {
            try
            {
                var activeScene = SceneManager.GetActiveScene();
                return new
                {
                    name = activeScene.name,
                    path = activeScene.path,
                    isLoaded = activeScene.isLoaded
                };
            }
            catch
            {
                return new
                {
                    name = string.Empty,
                    path = string.Empty,
                    isLoaded = false
                };
            }
        }

        private static void CacheStatusSnapshot(object snapshot)
        {
            lock (SnapshotLock)
            {
                _cachedStatusSnapshot = snapshot;
            }
        }

        private static void WriteSnapshotFiles(int port, string url, object snapshot)
        {
            CacheStatusSnapshot(snapshot);

            WriteJson(ConnectorPaths.StatusFilePath(), snapshot);

            if (port <= 0 || string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            var instancePayload = new
            {
                projectName = ConnectorPaths.ProjectName,
                projectPath = ConnectorPaths.ProjectPath,
                projectHash = ConnectorPaths.ProjectHash,
                port,
                url,
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                statusFile = ConnectorPaths.StatusFilePath()
            };
            WriteInstanceSnapshotIfNeeded(instancePayload);
        }

        private static void WriteInstanceSnapshotIfNeeded(object payload)
        {
            var path = ConnectorPaths.InstanceFilePath;
            var json = JsonConvert.SerializeObject(payload, Formatting.Indented);

            try
            {
                if (File.Exists(path))
                {
                    var existing = File.ReadAllText(path);
                    if (string.Equals(existing, json, StringComparison.Ordinal))
                    {
                        return;
                    }
                }
            }
            catch (IOException)
            {
                return;
            }

            WriteJson(path, json);
        }

        private static string BuildStatusMessage(bool connected, bool reloading, ConnectionSnapshot connection, string sceneOpenMessage)
        {
            if (connection != null && connection.busyStale && !string.IsNullOrWhiteSpace(connection.busyCommand))
            {
                return "Cubix Unity CLI command '" + connection.busyCommand + "' has been running longer than expected.";
            }

            if (connection != null && connection.busyStale)
            {
                return "Cubix Unity CLI has a stale pending command.";
            }

            if (connection != null && connection.busy && !string.IsNullOrWhiteSpace(connection.busyCommand))
            {
                return "Cubix Unity CLI is processing '" + connection.busyCommand + "'.";
            }

            if (connection != null && connection.queuedCommands > 0)
            {
                return "Cubix Unity CLI has queued commands waiting to run.";
            }

            if (connection != null && connection.busy)
            {
                return "Cubix Unity CLI is processing a command.";
            }

            if (!string.IsNullOrWhiteSpace(sceneOpenMessage))
            {
                return sceneOpenMessage;
            }

            if (reloading)
            {
                return "Unity is reconnecting after assembly reload.";
            }

            if (!connected)
            {
                return "Cubix Unity CLI is disconnected.";
            }

            if (EditorApplication.isUpdating)
            {
                return "Unity is refreshing assets.";
            }

            if (EditorApplication.isCompiling)
            {
                return "Unity is compiling scripts.";
            }

            if (CompilationAwaiter.HasPendingVerifyJob())
            {
                return "Unity is running verify.";
            }

            if (TestRunController.HasPendingRun())
            {
                return "Unity is running tests.";
            }

            if (PlayModeTransitionController.HasPendingTransition())
            {
                return "Unity is changing play mode.";
            }

            return "Cubix Unity CLI is ready.";
        }

        private static void ApplyCommandActivity(JObject snapshot, CommandActivitySnapshot activity)
        {
            if (snapshot == null || activity == null)
            {
                return;
            }

            var busy = activity.busy || activity.queuedCount > 0;
            var message = activity.busy && !string.IsNullOrWhiteSpace(activity.command)
                ? "Cubix Unity CLI is processing '" + activity.command + "'."
                : activity.queuedCount > 0
                    ? "Cubix Unity CLI has queued commands waiting to run."
                    : "Cubix Unity CLI is processing a command.";
            if (activity.stale && !string.IsNullOrWhiteSpace(activity.command))
            {
                message = "Cubix Unity CLI command '" + activity.command + "' has been running longer than expected.";
            }
            else if (activity.stale)
            {
                message = "Cubix Unity CLI has a stale pending command.";
            }

            snapshot["ready"] = false;
            snapshot["busy"] = busy;
            snapshot["busyCommand"] = activity.command;
            snapshot["queuedCommands"] = activity.queuedCount;
            snapshot["busyStale"] = activity.stale;
            snapshot["message"] = message;

            var connection = snapshot["connection"] as JObject ?? new JObject();
            connection["ready"] = false;
            connection["busy"] = busy;
            connection["busyCommand"] = activity.command;
            connection["busyRequestId"] = activity.requestId;
            connection["busyStartedAtUtc"] = activity.startedAtUtc;
            connection["busyDurationMs"] = activity.durationMs;
            connection["busyStale"] = activity.stale;
            connection["busyStaleAfterMs"] = activity.staleAfterMs;
            connection["queuedCommands"] = activity.queuedCount;
            connection["queuedStartedAtUtc"] = activity.queuedStartedAtUtc;
            connection["queuedDurationMs"] = activity.queuedDurationMs;
            snapshot["connection"] = connection;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }
}
