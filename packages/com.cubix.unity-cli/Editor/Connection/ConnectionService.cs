using UnityEditor;

namespace Cubix.UnityCli
{
    internal sealed class ConnectionSnapshot
    {
        public bool connected;
        public bool autoConnectOnLoad;
        public int port;
        public string url;
        public string projectHash;
        public string lastError;
        public bool ready;
        public bool reloading;
        public int commandCount;
        public bool busy;
        public string busyCommand;
        public string busyRequestId;
        public string busyStartedAtUtc;
        public long busyDurationMs;
        public bool busyStale;
        public long busyStaleAfterMs;
        public int queuedCommands;
        public string queuedStartedAtUtc;
        public long queuedDurationMs;
    }

    [InitializeOnLoad]
    internal static class ConnectionService
    {
        private const string AutoConnectEditorPrefKey = "Cubix.UnityCli.AutoConnectOnLoad";
        private const string ReconnectOnReloadSessionKey = "Cubix.UnityCli.ReconnectOnReload";
        private const string ReloadingSessionKey = "Cubix.UnityCli.Reloading";
        private const double ReconnectRetryIntervalSeconds = 1.0d;

        private static bool _autoConnectPending;
        private static double _nextReconnectAttemptAt;

        static ConnectionService()
        {
            _autoConnectPending = AutoConnectOnLoad || SessionState.GetBool(ReconnectOnReloadSessionKey, false);
            AssemblyReloadEvents.beforeAssemblyReload += HandleBeforeAssemblyReload;
            EditorApplication.delayCall += EnsureAutoConnected;
            EditorApplication.update += PumpReconnect;
            EditorApplication.quitting += Disconnect;
        }

        public static string LastError { get; private set; }

        public static bool IsReloading => SessionState.GetBool(ReloadingSessionKey, false);

        public static bool ShouldMaintainStatusFiles => IsReloading || _autoConnectPending;

        public static bool AutoConnectOnLoad
        {
            get => EditorPrefs.GetBool(AutoConnectEditorPrefKey, true);
            set => EditorPrefs.SetBool(AutoConnectEditorPrefKey, value);
        }

        public static bool Connect()
        {
            LastError = null;
            if (HttpServer.Start())
            {
                _autoConnectPending = false;
                _nextReconnectAttemptAt = 0d;
                SessionState.SetBool(ReconnectOnReloadSessionKey, true);
                SessionState.SetBool(ReloadingSessionKey, false);
                HeartbeatService.RefreshNow();
                return true;
            }

            LastError = HttpServer.LastError;
            return false;
        }

        public static void Disconnect()
        {
            LastError = null;
            _autoConnectPending = false;
            _nextReconnectAttemptAt = 0d;
            SessionState.SetBool(ReconnectOnReloadSessionKey, false);
            SessionState.SetBool(ReloadingSessionKey, false);
            HeartbeatService.CleanupNow();
            HttpServer.Stop();
        }

        public static bool Reconnect()
        {
            Disconnect();
            return Connect();
        }

        public static ConnectionSnapshot RefreshStatus()
        {
            HeartbeatService.RefreshNow();
            return GetSnapshot();
        }

        public static ConnectionSnapshot GetSnapshot()
        {
            var connected = HttpServer.IsRunning && !IsReloading;
            var commandActivity = HttpServer.GetCommandActivitySnapshot();
            var busy = commandActivity.busy || commandActivity.queuedCount > 0;
            return new ConnectionSnapshot
            {
                connected = connected,
                autoConnectOnLoad = AutoConnectOnLoad,
                port = HttpServer.Port,
                url = HttpServer.Url,
                projectHash = ConnectorPaths.ProjectHash,
                lastError = string.IsNullOrWhiteSpace(LastError) ? HttpServer.LastError : LastError,
                ready = connected &&
                        !busy &&
                        !EditorApplication.isCompiling &&
                        !EditorApplication.isUpdating &&
                        !CompilationAwaiter.HasPendingVerifyJob() &&
                        !SceneOpenController.HasPendingOpen() &&
                        !TestRunController.HasPendingRun() &&
                        !PlayModeTransitionController.HasPendingTransition(),
                reloading = IsReloading,
                commandCount = CommandRouter.ListCommands(includeUnsafe: true).Count,
                busy = busy,
                busyCommand = commandActivity.command,
                busyRequestId = commandActivity.requestId,
                busyStartedAtUtc = commandActivity.startedAtUtc,
                busyDurationMs = commandActivity.durationMs,
                busyStale = commandActivity.stale,
                busyStaleAfterMs = commandActivity.staleAfterMs,
                queuedCommands = commandActivity.queuedCount,
                queuedStartedAtUtc = commandActivity.queuedStartedAtUtc,
                queuedDurationMs = commandActivity.queuedDurationMs
            };
        }

        private static void EnsureAutoConnected()
        {
            if (_autoConnectPending)
            {
                TryReconnect();
            }
        }

        private static void HandleBeforeAssemblyReload()
        {
            SessionState.SetBool(
                ReconnectOnReloadSessionKey,
                HttpServer.IsRunning || SessionState.GetBool(ReconnectOnReloadSessionKey, false));
            SessionState.SetBool(ReloadingSessionKey, true);
            _autoConnectPending = true;
            _nextReconnectAttemptAt = 0d;
            HeartbeatService.PrepareForReload();
            HttpServer.Stop();
        }

        private static void PumpReconnect()
        {
            if (!_autoConnectPending || HttpServer.IsRunning)
            {
                return;
            }

            if (EditorApplication.timeSinceStartup < _nextReconnectAttemptAt)
            {
                return;
            }

            TryReconnect();
        }

        private static void TryReconnect()
        {
            if (Connect())
            {
                return;
            }

            _nextReconnectAttemptAt = EditorApplication.timeSinceStartup + ReconnectRetryIntervalSeconds;
            HeartbeatService.RefreshNow();
        }
    }
}
