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
        public int commandCount;
    }

    [InitializeOnLoad]
    internal static class ConnectionService
    {
        private const string AutoConnectEditorPrefKey = "Cubix.UnityCli.AutoConnectOnLoad";

        static ConnectionService()
        {
            EditorApplication.delayCall += EnsureAutoConnected;
            EditorApplication.quitting += Disconnect;
        }

        public static string LastError { get; private set; }

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
                HeartbeatService.RefreshNow();
                return true;
            }

            LastError = HttpServer.LastError;
            return false;
        }

        public static void Disconnect()
        {
            LastError = null;
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
            return new ConnectionSnapshot
            {
                connected = HttpServer.IsRunning,
                autoConnectOnLoad = AutoConnectOnLoad,
                port = HttpServer.Port,
                url = HttpServer.Url,
                projectHash = ConnectorPaths.ProjectHash,
                lastError = string.IsNullOrWhiteSpace(LastError) ? HttpServer.LastError : LastError,
                ready = HttpServer.IsRunning && !EditorApplication.isCompiling && !EditorApplication.isUpdating && !CompilationAwaiter.HasPendingVerifyJob(),
                commandCount = CommandRouter.ListCommands(includeUnsafe: true).Count
            };
        }

        private static void EnsureAutoConnected()
        {
            if (AutoConnectOnLoad)
            {
                Connect();
            }
        }
    }
}
