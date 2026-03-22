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
    }

    [InitializeOnLoad]
    internal static class ConnectionService
    {
        private const string AutoConnectEditorPrefKey = "Cubix.UnityCli.AutoConnectOnLoad";
        private const string ReconnectOnReloadSessionKey = "Cubix.UnityCli.ReconnectOnReload";
        private const string ReloadingSessionKey = "Cubix.UnityCli.Reloading";

        static ConnectionService()
        {
            AssemblyReloadEvents.beforeAssemblyReload += HandleBeforeAssemblyReload;
            EditorApplication.delayCall += EnsureAutoConnected;
            EditorApplication.quitting += Disconnect;
        }

        public static string LastError { get; private set; }

        public static bool IsReloading => SessionState.GetBool(ReloadingSessionKey, false);

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
            return new ConnectionSnapshot
            {
                connected = connected,
                autoConnectOnLoad = AutoConnectOnLoad,
                port = HttpServer.Port,
                url = HttpServer.Url,
                projectHash = ConnectorPaths.ProjectHash,
                lastError = string.IsNullOrWhiteSpace(LastError) ? HttpServer.LastError : LastError,
                ready = connected && !EditorApplication.isCompiling && !EditorApplication.isUpdating && !CompilationAwaiter.HasPendingVerifyJob(),
                reloading = IsReloading,
                commandCount = CommandRouter.ListCommands(includeUnsafe: true).Count
            };
        }

        private static void EnsureAutoConnected()
        {
            if (AutoConnectOnLoad || SessionState.GetBool(ReconnectOnReloadSessionKey, false))
            {
                Connect();
            }
        }

        private static void HandleBeforeAssemblyReload()
        {
            SessionState.SetBool(
                ReconnectOnReloadSessionKey,
                HttpServer.IsRunning || SessionState.GetBool(ReconnectOnReloadSessionKey, false));
            SessionState.SetBool(ReloadingSessionKey, true);
            HeartbeatService.PrepareForReload();
            HttpServer.Stop();
        }
    }
}
