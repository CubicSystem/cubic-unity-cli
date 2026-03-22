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
        private static double _nextHeartbeatAt;

        static HeartbeatService()
        {
            ConnectorPaths.EnsureDirectories();
            EditorApplication.update += Pump;
            EditorApplication.quitting += CleanupNow;
        }

        public static object BuildStatusSnapshot()
        {
            var activeScene = SceneManager.GetActiveScene();
            var verify = CompilationAwaiter.GetVerifyJob();
            var commands = ToolDiscovery.GetCommandMetadata();
            return new
            {
                projectName = ConnectorPaths.ProjectName,
                projectPath = ConnectorPaths.ProjectPath,
                projectHash = ConnectorPaths.ProjectHash,
                port = HttpServer.Port,
                url = HttpServer.Port > 0 ? "http://127.0.0.1:" + HttpServer.Port : null,
                editor = new
                {
                    isPlaying = EditorApplication.isPlaying,
                    isPaused = EditorApplication.isPaused,
                    isCompiling = EditorApplication.isCompiling,
                    isUpdating = EditorApplication.isUpdating
                },
                activeScene = new
                {
                    name = activeScene.name,
                    path = activeScene.path,
                    isLoaded = activeScene.isLoaded
                },
                ready = !EditorApplication.isCompiling && !EditorApplication.isUpdating && !CompilationAwaiter.HasPendingVerifyJob(),
                verify,
                commandCount = commands.Count,
                commands,
                connection = ConnectionService.GetSnapshot(),
                lastUpdatedUtc = DateTime.UtcNow.ToString("o")
            };
        }

        public static void RefreshNow()
        {
            _nextHeartbeatAt = 0d;
            if (HttpServer.IsRunning)
            {
                ConnectorPaths.EnsureDirectories();
                WriteJson(ConnectorPaths.InstanceFilePath, new
                {
                    projectName = ConnectorPaths.ProjectName,
                    projectPath = ConnectorPaths.ProjectPath,
                    projectHash = ConnectorPaths.ProjectHash,
                    port = HttpServer.Port,
                    url = HttpServer.Url,
                    pid = System.Diagnostics.Process.GetCurrentProcess().Id,
                    updatedAtUtc = DateTime.UtcNow.ToString("o"),
                    statusFile = ConnectorPaths.StatusFilePath(HttpServer.Port)
                });

                WriteJson(ConnectorPaths.StatusFilePath(HttpServer.Port), BuildStatusSnapshot());
            }
        }

        public static void CleanupNow()
        {
            TryDelete(ConnectorPaths.InstanceFilePath);
            if (HttpServer.Port > 0)
            {
                TryDelete(ConnectorPaths.StatusFilePath(HttpServer.Port));
            }
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
