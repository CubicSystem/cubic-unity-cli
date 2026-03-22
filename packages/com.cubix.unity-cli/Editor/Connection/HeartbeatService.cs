using System;
using System.IO;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine.SceneManagement;

namespace Cubix.UnityCli
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
