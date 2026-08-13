using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace CubicEngine.UnityCli
{
    /// <summary>
    /// Owns the periodic heartbeat deadline. A forced refresh publishes immediately,
    /// but it also moves the next periodic deadline so the following editor update
    /// cannot duplicate the publication.
    /// </summary>
    internal sealed class HeartbeatPump
    {
        private readonly double _intervalSeconds;
        private double _nextHeartbeatAt;

        public HeartbeatPump(double intervalSeconds)
        {
            if (intervalSeconds <= 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(intervalSeconds));
            }

            _intervalSeconds = intervalSeconds;
        }

        public bool Pump(double now, bool publicationEnabled)
        {
            if (!publicationEnabled || now < _nextHeartbeatAt)
            {
                return false;
            }

            ScheduleNext(now);
            return true;
        }

        public bool ForceRefresh(double now, bool publicationEnabled)
        {
            if (!publicationEnabled)
            {
                return false;
            }

            ScheduleNext(now);
            return true;
        }

        private void ScheduleNext(double now)
        {
            _nextHeartbeatAt = now + _intervalSeconds;
        }
    }

    internal enum InstancePublicationResult
    {
        Unchanged,
        Published,
        Failed
    }

    /// <summary>
    /// Publishes status JSON atomically and treats the instance file as a stable
    /// connection advertisement. Its timestamp changes only when the advertised
    /// connection identity changes, not on every heartbeat.
    /// </summary>
    internal sealed class HeartbeatPublicationStore
    {
        private readonly string _statusFilePath;
        private readonly string _instanceFilePath;
        private readonly Encoding _encoding;
        private readonly Func<string> _utcNow;
        private readonly Func<string, string, Encoding, bool> _atomicWrite;
        private readonly object _statusWriteLock = new object();
        private readonly object _instanceWriteLock = new object();

        private string _lastInstanceIdentityJson;

        public HeartbeatPublicationStore(
            string statusFilePath,
            string instanceFilePath,
            Encoding encoding = null,
            Func<string> utcNow = null,
            Func<string, string, Encoding, bool> atomicWrite = null)
        {
            _statusFilePath = statusFilePath ?? throw new ArgumentNullException(nameof(statusFilePath));
            _instanceFilePath = instanceFilePath ?? throw new ArgumentNullException(nameof(instanceFilePath));
            _encoding = encoding ?? new UTF8Encoding(false);
            _utcNow = utcNow ?? (() => DateTime.UtcNow.ToString("o"));
            _atomicWrite = atomicWrite ?? AtomicFileWriter.TryWriteAllText;
        }

        public bool PublishStatus(object snapshot)
        {
            lock (_statusWriteLock)
            {
                var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
                return _atomicWrite(_statusFilePath, json, _encoding);
            }
        }

        public InstancePublicationResult PublishInstance(object connectionIdentity)
        {
            var identity = JObject.FromObject(connectionIdentity ?? throw new ArgumentNullException(nameof(connectionIdentity)));
            var identityJson = identity.ToString(Formatting.None);

            lock (_instanceWriteLock)
            {
                if (string.Equals(_lastInstanceIdentityJson, identityJson, StringComparison.Ordinal) &&
                    File.Exists(_instanceFilePath))
                {
                    return InstancePublicationResult.Unchanged;
                }

                if (TryReadJsonObject(_instanceFilePath, _encoding, out var existing) &&
                    MatchesConnectionIdentity(existing, identity))
                {
                    _lastInstanceIdentityJson = identityJson;
                    return InstancePublicationResult.Unchanged;
                }

                var payload = (JObject)identity.DeepClone();
                payload["updatedAtUtc"] = _utcNow();
                var json = payload.ToString(Formatting.Indented);
                if (!_atomicWrite(_instanceFilePath, json, _encoding))
                {
                    return InstancePublicationResult.Failed;
                }

                _lastInstanceIdentityJson = identityJson;
                return InstancePublicationResult.Published;
            }
        }

        public void DeletePublishedFiles()
        {
            lock (_statusWriteLock)
            {
                TryDelete(_statusFilePath);
            }

            lock (_instanceWriteLock)
            {
                TryDelete(_instanceFilePath);
                _lastInstanceIdentityJson = null;
            }
        }

        private static bool MatchesConnectionIdentity(JObject existing, JObject desiredIdentity)
        {
            foreach (var property in desiredIdentity.Properties())
            {
                if (!JToken.DeepEquals(existing[property.Name], property.Value))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryReadJsonObject(string path, Encoding encoding, out JObject payload)
        {
            payload = null;
            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                payload = JObject.Parse(File.ReadAllText(path, encoding));
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (JsonException)
            {
                return false;
            }
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
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    internal static class HeartbeatSnapshotContract
    {
        public static object BuildBusyKeepAliveSnapshot(
            object cachedSnapshot,
            CommandActivitySnapshot activity,
            string updatedAtUtc)
        {
            if (cachedSnapshot == null || activity == null)
            {
                return null;
            }

            var cachedObject = cachedSnapshot as JObject;
            var snapshot = cachedObject != null
                ? (JObject)cachedObject.DeepClone()
                : JObject.FromObject(cachedSnapshot);
            ApplyCommandActivity(snapshot, activity);
            snapshot["updatedAtUtc"] = updatedAtUtc;
            snapshot["lastUpdatedUtc"] = updatedAtUtc;
            return snapshot;
        }

        private static void ApplyCommandActivity(JObject snapshot, CommandActivitySnapshot activity)
        {
            var busy = activity.busy || activity.queuedCount > 0;
            var message = activity.busy && !string.IsNullOrWhiteSpace(activity.command)
                ? "Cubic Unity CLI is processing '" + activity.command + "'."
                : activity.queuedCount > 0
                    ? "Cubic Unity CLI has queued commands waiting to run."
                    : "Cubic Unity CLI is processing a command.";
            if (activity.stale && !string.IsNullOrWhiteSpace(activity.command))
            {
                message = "Cubic Unity CLI command '" + activity.command + "' has been running longer than expected.";
            }
            else if (activity.stale)
            {
                message = "Cubic Unity CLI has a stale pending command.";
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
    }

    [InitializeOnLoad]
    internal static class HeartbeatService
    {
        private const double HeartbeatIntervalSeconds = 1.0d;

        private static readonly object SnapshotLock = new object();
        private static readonly object PublicationLifecycleLock = new object();
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly HeartbeatPump Heartbeat = new HeartbeatPump(HeartbeatIntervalSeconds);
        private static readonly HeartbeatPublicationStore Publications;
        private static readonly int ProcessId = System.Diagnostics.Process.GetCurrentProcess().Id;

        private static object _cachedStatusSnapshot;

        static HeartbeatService()
        {
            ConnectorPaths.EnsureDirectories();
            Publications = new HeartbeatPublicationStore(
                ConnectorPaths.StatusFilePath(),
                ConnectorPaths.InstanceFilePath,
                Utf8NoBom);
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
            var publicationEnabled = HttpServer.IsRunning || ConnectionService.ShouldMaintainStatusFiles;
            if (!Heartbeat.ForceRefresh(EditorApplication.timeSinceStartup, publicationEnabled))
            {
                return;
            }

            ConnectorPaths.EnsureDirectories();
            PublishCurrentSnapshot();
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

            lock (PublicationLifecycleLock)
            {
                // The keepalive loop runs on a worker thread. Re-check lifecycle
                // state while serialized with reload/cleanup so an in-flight task
                // cannot recreate a stale status file after disconnect.
                if (!HttpServer.IsRunning || ConnectionService.IsReloading)
                {
                    return;
                }

                object cachedSnapshot;
                lock (SnapshotLock)
                {
                    cachedSnapshot = _cachedStatusSnapshot;
                }

                var updatedAtUtc = DateTime.UtcNow.ToString("o");
                var snapshot = HeartbeatSnapshotContract.BuildBusyKeepAliveSnapshot(
                    cachedSnapshot,
                    activity,
                    updatedAtUtc);
                if (snapshot == null)
                {
                    return;
                }

                CacheStatusSnapshot(snapshot);
                Publications.PublishStatus(snapshot);
            }
        }

        public static void PrepareForReload()
        {
            lock (PublicationLifecycleLock)
            {
                ConnectorPaths.EnsureDirectories();

                var port = HttpServer.Port;
                var url = HttpServer.Url;
                if (port > 0 && !string.IsNullOrWhiteSpace(url))
                {
                    var snapshot = BuildStatusSnapshot(port, url, false);
                    PublishSnapshotFiles(port, url, snapshot);
                    return;
                }

                lock (SnapshotLock)
                {
                    _cachedStatusSnapshot = null;
                }
            }
        }

        public static void CleanupNow()
        {
            lock (PublicationLifecycleLock)
            {
                Publications.DeletePublishedFiles();

                lock (SnapshotLock)
                {
                    _cachedStatusSnapshot = null;
                }
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
            var updatedAtUtc = DateTime.UtcNow.ToString("o");
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
                updatedAtUtc,
                lastUpdatedUtc = updatedAtUtc
            };
        }

        private static void Pump()
        {
            var publicationEnabled = HttpServer.IsRunning || ConnectionService.ShouldMaintainStatusFiles;
            if (Heartbeat.Pump(EditorApplication.timeSinceStartup, publicationEnabled))
            {
                PublishCurrentSnapshot();
            }
        }

        private static void PublishCurrentSnapshot()
        {
            var port = HttpServer.AdvertisedPort;
            var url = HttpServer.IsRunning ? HttpServer.Url : HttpServer.AdvertisedUrl;
            var snapshot = BuildStatusSnapshot(port, url, HttpServer.IsRunning && !ConnectionService.IsReloading);
            PublishSnapshotFiles(port, url, snapshot);
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

        private static void PublishSnapshotFiles(int port, string url, object snapshot)
        {
            CacheStatusSnapshot(snapshot);
            Publications.PublishStatus(snapshot);

            if (port <= 0 || string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            Publications.PublishInstance(new
            {
                projectName = ConnectorPaths.ProjectName,
                projectPath = ConnectorPaths.ProjectPath,
                projectHash = ConnectorPaths.ProjectHash,
                port,
                url,
                pid = ProcessId,
                statusFile = ConnectorPaths.StatusFilePath()
            });
        }

        private static string BuildStatusMessage(bool connected, bool reloading, ConnectionSnapshot connection, string sceneOpenMessage)
        {
            if (connection != null && connection.busyStale && !string.IsNullOrWhiteSpace(connection.busyCommand))
            {
                return "Cubic Unity CLI command '" + connection.busyCommand + "' has been running longer than expected.";
            }

            if (connection != null && connection.busyStale)
            {
                return "Cubic Unity CLI has a stale pending command.";
            }

            if (connection != null && connection.busy && !string.IsNullOrWhiteSpace(connection.busyCommand))
            {
                return "Cubic Unity CLI is processing '" + connection.busyCommand + "'.";
            }

            if (connection != null && connection.queuedCommands > 0)
            {
                return "Cubic Unity CLI has queued commands waiting to run.";
            }

            if (connection != null && connection.busy)
            {
                return "Cubic Unity CLI is processing a command.";
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
                return "Cubic Unity CLI is disconnected.";
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

            return "Cubic Unity CLI is ready.";
        }
    }
}
