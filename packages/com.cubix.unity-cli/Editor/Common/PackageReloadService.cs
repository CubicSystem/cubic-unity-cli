using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;

namespace Cubix.UnityCli
{
    [InitializeOnLoad]
    internal static class PackageReloadService
    {
        private const string AttemptedSignatureKey = "Cubix.UnityCli.PackageReload.AttemptedSignature";
        private const string PendingSignatureKey = "Cubix.UnityCli.PackageReload.PendingSignature";
        private const string PendingKey = "Cubix.UnityCli.PackageReload.Pending";
        private const string StatusKey = "Cubix.UnityCli.PackageReload.Status";
        private const string ResolveRequestedKey = "Cubix.UnityCli.PackageReload.ResolveRequested";
        private const double RetryIntervalSeconds = 5.0d;

        private static double _nextAttemptAt;

        static PackageReloadService()
        {
            EditorApplication.delayCall += DetectLoadedPackageDrift;
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
            var signature = BuildDriftSignature();
            if (string.IsNullOrWhiteSpace(signature))
            {
                SetStatus("No package drift was detected.");
                return;
            }

            SessionState.SetString(PendingSignatureKey, signature);
            SessionState.SetBool(PendingKey, true);
            _nextAttemptAt = 0d;
            SetStatus(string.IsNullOrWhiteSpace(reason)
                ? "Queued a Cubix Unity CLI package reload."
                : reason);
        }

        private static void DetectLoadedPackageDrift()
        {
            if (!PackageLayout.HasLoadedPackageDrift)
            {
                ClearPending();
                return;
            }

            var signature = BuildDriftSignature();
            if (string.IsNullOrWhiteSpace(signature))
            {
                return;
            }

            if (string.Equals(SessionState.GetString(AttemptedSignatureKey, string.Empty), signature, System.StringComparison.Ordinal))
            {
                return;
            }

            RequestReload("Detected Cubix Unity CLI package drift. Scheduling a package reload.");
        }

        private static void Pump()
        {
            if (!HasPendingReload)
            {
                return;
            }

            var signature = SessionState.GetString(PendingSignatureKey, string.Empty);
            if (string.IsNullOrWhiteSpace(signature) || !PackageLayout.HasLoadedPackageDrift)
            {
                ClearPending();
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
                    SetStatus("Waiting for Unity Package Manager and script reload to finish updating Cubix Unity CLI.");
                    return;
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                RequestScriptReload();
                CompilationPipeline.RequestScriptCompilation();
                ResolveWasRequested = false;
                _nextAttemptAt = EditorApplication.timeSinceStartup + RetryIntervalSeconds;
                SetStatus("Package resolve finished. Waiting for the loaded Cubix Unity CLI build to update.");
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
                SessionState.SetString(AttemptedSignatureKey, signature);
                _nextAttemptAt = EditorApplication.timeSinceStartup + RetryIntervalSeconds;
                SetStatus("Triggered a Cubix Unity CLI package resolve. Waiting for Package Manager processing.");
            }
            catch (System.Exception exception)
            {
                ResolveWasRequested = false;
                _nextAttemptAt = EditorApplication.timeSinceStartup + RetryIntervalSeconds;
                SetStatus("Could not trigger a Cubix Unity CLI package reload: " + exception.Message);
            }
        }

        private static string BuildDriftSignature()
        {
            if (!PackageLayout.HasLoadedPackageDrift)
            {
                return null;
            }

            return string.Join(
                "|",
                PackageLayout.PackageVersion ?? string.Empty,
                PackageLayout.ProjectPackageVersion ?? string.Empty,
                PackageLayout.PackageRoot ?? string.Empty,
                PackageLayout.ProjectPackageRoot ?? string.Empty,
                PackageLayout.ProjectManifestDependencySpec ?? string.Empty);
        }

        private static void ClearPending()
        {
            SessionState.SetBool(PendingKey, false);
            SessionState.EraseString(PendingSignatureKey);
            ResolveWasRequested = false;
            if (PackageLayout.HasLoadedPackageDrift)
            {
                return;
            }

            SetStatus("Loaded Cubix Unity CLI package matches the project metadata.");
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
