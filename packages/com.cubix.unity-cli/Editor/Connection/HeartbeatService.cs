using System;
using System.IO;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace Cubix.UnityCli
{
    [InitializeOnLoad]
    internal static class HeartbeatService
    {
        private static readonly object SnapshotLock = new object();

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
            return BuildStatusSnapshot(HttpServer.Port, HttpServer.Url, !ConnectionService.IsReloading);
        }

        public static void RefreshNow()
        {
            _nextHeartbeatAt = 0d;
            if (HttpServer.IsRunning)
            {
                ConnectorPaths.EnsureDirectories();
                var snapshot = BuildStatusSnapshot();
                WriteSnapshotFiles(HttpServer.Port, HttpServer.Url, snapshot);
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
            var commands = ToolDiscovery.GetCommandMetadata();
            var activeScene = BuildActiveSceneSnapshot();
            var reloading = ConnectionService.IsReloading;
            var ready = connected && !reloading && !EditorApplication.isCompiling && !EditorApplication.isUpdating && !CompilationAwaiter.HasPendingVerifyJob();
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
                    isPaused = EditorApplication.isPaused,
                    isCompiling = EditorApplication.isCompiling,
                    isUpdating = EditorApplication.isUpdating
                },
                activeScene,
                ready,
                reloading,
                verify,
                commandCount = commands.Count,
                commands,
                connection = ConnectionService.GetSnapshot(),
                lastUpdatedUtc = DateTime.UtcNow.ToString("o")
            };
        }

        private static void Pump()
        {
            if (EditorApplication.timeSinceStartup < _nextHeartbeatAt || !HttpServer.IsRunning)
            {
                return;
            }

            _nextHeartbeatAt = EditorApplication.timeSinceStartup + 1.0d;

            RefreshNow();
        }

        private static void WriteJson(string path, object payload)
        {
            var json = JsonConvert.SerializeObject(payload, Formatting.Indented);
            var tempPath = path + ".tmp";
            File.WriteAllText(tempPath, json);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(tempPath, path);
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
            WriteJson(ConnectorPaths.InstanceFilePath, new
            {
                projectName = ConnectorPaths.ProjectName,
                projectPath = ConnectorPaths.ProjectPath,
                projectHash = ConnectorPaths.ProjectHash,
                port,
                url,
                pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                updatedAtUtc = DateTime.UtcNow.ToString("o"),
                statusFile = ConnectorPaths.StatusFilePath()
            });

            WriteJson(ConnectorPaths.StatusFilePath(), snapshot);
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
