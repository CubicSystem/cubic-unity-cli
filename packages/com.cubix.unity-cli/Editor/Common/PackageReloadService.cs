using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;

namespace Cubix.UnityCli
{
    [InitializeOnLoad]
    internal static class PackageReloadService
    {
        private const string PendingKey = "Cubix.UnityCli.PackageReload.Pending";
        private const string StatusKey = "Cubix.UnityCli.PackageReload.Status";
        private const string ResolveRequestedKey = "Cubix.UnityCli.PackageReload.ResolveRequested";
        private const double RetryIntervalSeconds = 5.0d;

        private static double _nextAttemptAt;

        static PackageReloadService()
        {
            EditorApplication.update += Pump;
        }

        public static bool HasPendingReload => SessionState.GetBool(PendingKey, false);

        public static string StatusMessage => SessionState.GetString(StatusKey, string.Empty);

        private static bool ResolveWasRequested
        {
            get => SessionState.GetBool(ResolveRequestedKey, false);
            set => SessionState.SetBool(ResolveRequestedKey, value);
        }

        public static void RequestReload(string reason)
        {
            SessionState.SetBool(PendingKey, true);
            ResolveWasRequested = false;
            _nextAttemptAt = 0d;
            SetStatus(string.IsNullOrWhiteSpace(reason)
                ? "Queued a Cubix Unity CLI package reload."
                : reason);
        }

        private static void Pump()
        {
            if (!HasPendingReload)
            {
                return;
            }

            if (EditorApplication.timeSinceStartup < _nextAttemptAt)
            {
                return;
            }

            if (ResolveWasRequested)
            {
                if (EditorApplication.isUpdating || EditorApplication.isCompiling || ConnectionService.IsReloading)
                {
                    _nextAttemptAt = EditorApplication.timeSinceStartup + RetryIntervalSeconds;
                    SetStatus("Waiting for Unity Package Manager and script reload to finish updating Cubix Unity CLI.");
                    return;
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                RequestScriptReload();
                CompilationPipeline.RequestScriptCompilation();
                ClearPending("Triggered a Cubix Unity CLI package reload. If the loaded package stays stale after compilation, restart Unity.");
                return;
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating || ConnectionService.IsReloading)
            {
                _nextAttemptAt = EditorApplication.timeSinceStartup + RetryIntervalSeconds;
                SetStatus("Waiting for Unity to finish compiling, updating, or reconnecting before reloading the package.");
                return;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(PackageLayout.ProjectPackageJsonAssetPath))
                {
                    AssetDatabase.ImportAsset(PackageLayout.ProjectPackageJsonAssetPath, ImportAssetOptions.ForceUpdate);
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                Client.Resolve();
                ResolveWasRequested = true;
                _nextAttemptAt = EditorApplication.timeSinceStartup + RetryIntervalSeconds;
                SetStatus("Triggered a Cubix Unity CLI package resolve. Waiting for Package Manager processing.");
            }
            catch (System.Exception exception)
            {
                ClearPending("Could not trigger a Cubix Unity CLI package reload: " + exception.Message);
            }
        }

        private static void ClearPending(string statusMessage = null)
        {
            SessionState.SetBool(PendingKey, false);
            ResolveWasRequested = false;
            if (!string.IsNullOrWhiteSpace(statusMessage))
            {
                SetStatus(statusMessage);
            }
        }

        private static void SetStatus(string message)
        {
            SessionState.SetString(StatusKey, message ?? string.Empty);
        }

        private static void RequestScriptReload()
        {
            var method = typeof(EditorUtility).GetMethod(
                "RequestScriptReload",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            method?.Invoke(null, null);
        }
    }
}
