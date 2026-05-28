using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;

namespace CubicEngine.UnityCli
{
    [InitializeOnLoad]
    internal static class CompilationAwaiter
    {
        [Serializable]
        internal sealed class CompilerMessageRecord
        {
            public string assembly;
            public string file;
            public int line;
            public int column;
            public string level;
            public string message;
        }

        [Serializable]
        internal sealed class VerifyJobRecord
        {
            public string id;
            public string state;
            public string mode;
            public string assetPath;
            public string startedAtUtc;
            public string updatedAtUtc;
            public int timeoutMs;
            public bool? success;
            public string message;
            public List<CompilerMessageRecord> errors = new List<CompilerMessageRecord>();
        }

        private const string CompilerMessagesKey = "cubix_cli.compiler.messages";
        private const string VerifyJobKey = "cubix_cli.verify.job";

        static CompilationAwaiter()
        {
            CompilationPipeline.compilationStarted += HandleCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += HandleAssemblyCompilationFinished;
            EditorApplication.update += PumpVerifyJobs;
            CleanupExpiredJob();
        }

        public static object StartVerify(JObject parameters)
        {
            var assetPath = ObjectResolver.NormalizeAssetPath(parameters?.Value<string>("path"));
            var requestedMode = parameters?.Value<string>("mode");
            var timeoutMs = Math.Max(parameters?.Value<int?>("timeoutMs") ?? 180000, 1000);

            var mode = ResolveMode(assetPath, requestedMode);
            ClearCompilerMessages();

            var record = new VerifyJobRecord
            {
                id = Guid.NewGuid().ToString("N"),
                state = "queued",
                mode = mode,
                assetPath = assetPath,
                startedAtUtc = DateTime.UtcNow.ToString("o"),
                updatedAtUtc = DateTime.UtcNow.ToString("o"),
                timeoutMs = timeoutMs,
                message = "Verify job queued."
            };

            SaveVerifyJob(record);

            if (string.Equals(mode, "reimport", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(assetPath))
            {
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
            }
            else
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            }

            CompilationPipeline.RequestScriptCompilation();

            return BuildVerifyState(record);
        }

        public static object GetVerifyJob()
        {
            return BuildVerifyState(RefreshVerifyJobState());
        }

        public static bool HasPendingVerifyJob()
        {
            var record = RefreshVerifyJobState();
            return record != null && !record.success.HasValue;
        }

        public static IReadOnlyList<CompilerMessageRecord> GetCompilerMessages(string level = null)
        {
            var entries = LoadCompilerMessages();
            if (string.IsNullOrWhiteSpace(level))
            {
                return entries;
            }

            return entries
                .Where(entry => string.Equals(entry.level, level, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public static void ClearCompilerMessages()
        {
            SessionState.EraseString(CompilerMessagesKey);
        }

        public static object BuildVerifyState(VerifyJobRecord record)
        {
            if (record == null)
            {
                return null;
            }

            return new
            {
                id = record.id,
                state = record.state,
                mode = record.mode,
                assetPath = record.assetPath,
                startedAtUtc = record.startedAtUtc,
                updatedAtUtc = record.updatedAtUtc,
                timeoutMs = record.timeoutMs,
                success = record.success,
                message = record.message,
                errors = record.errors
            };
        }

        private static void HandleCompilationStarted(object _)
        {
            var record = LoadVerifyJob();
            if (record == null || record.success.HasValue)
            {
                return;
            }

            record.state = "compiling";
            record.message = "Unity is compiling scripts.";
            record.updatedAtUtc = DateTime.UtcNow.ToString("o");
            SaveVerifyJob(record);
            ClearCompilerMessages();
        }

        private static void HandleAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            var entries = LoadCompilerMessages().ToList();
            foreach (var message in messages)
            {
                entries.Add(new CompilerMessageRecord
                {
                    assembly = assemblyPath,
                    file = message.file ?? string.Empty,
                    line = message.line,
                    column = message.column,
                    level = message.type == CompilerMessageType.Warning ? "warning" : "error",
                    message = message.message
                });
            }

            SaveCompilerMessages(entries);
        }

        private static void PumpVerifyJobs()
        {
            RefreshVerifyJobState();
        }

        private static void CleanupExpiredJob()
        {
            var record = LoadVerifyJob();
            if (record == null)
            {
                return;
            }

            if (!DateTime.TryParse(record.startedAtUtc, out var startedAt))
            {
                SessionState.EraseString(VerifyJobKey);
                return;
            }

            if (DateTime.UtcNow > startedAt.AddHours(4))
            {
                SessionState.EraseString(VerifyJobKey);
            }
        }

        private static string ResolveMode(string assetPath, string requestedMode)
        {
            if (!string.IsNullOrWhiteSpace(requestedMode))
            {
                return requestedMode;
            }

            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return "refresh";
            }

            return AssetDatabase.LoadMainAssetAtPath(assetPath) != null ? "reimport" : "refresh";
        }

        private static VerifyJobRecord LoadVerifyJob()
        {
            var json = SessionState.GetString(VerifyJobKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<VerifyJobRecord>(json);
            }
            catch
            {
                SessionState.EraseString(VerifyJobKey);
                return null;
            }
        }

        private static void SaveVerifyJob(VerifyJobRecord record)
        {
            SessionState.SetString(VerifyJobKey, JsonConvert.SerializeObject(record));
        }

        private static VerifyJobRecord RefreshVerifyJobState()
        {
            var record = LoadVerifyJob();
            if (record == null || record.success.HasValue)
            {
                return record;
            }

            var nowUtc = DateTime.UtcNow;
            if (TryGetStartedAtUtc(record, out var startedAtUtc))
            {
                if (nowUtc > startedAtUtc.AddMilliseconds(record.timeoutMs))
                {
                    record.state = "failed";
                    record.success = false;
                    record.message = "Verify timed out while waiting for compilation to settle.";
                    record.errors = GetCompilerMessages("error").ToList();
                    record.updatedAtUtc = nowUtc.ToString("o");
                    SaveVerifyJob(record);
                    return record;
                }

                if (nowUtc < startedAtUtc.AddMilliseconds(500))
                {
                    return record;
                }
            }

            if (ConnectionService.IsReloading)
            {
                return UpdatePendingState(record, "compiling", "Unity is reloading assemblies.");
            }

            if (EditorApplication.isUpdating)
            {
                return UpdatePendingState(record, "compiling", "Unity is refreshing assets.");
            }

            if (EditorApplication.isCompiling)
            {
                return UpdatePendingState(record, "compiling", "Unity is compiling scripts.");
            }

            var errors = GetCompilerMessages("error").ToList();
            record.errors = errors;
            record.success = errors.Count == 0;
            record.state = record.success.Value ? "completed" : "failed";
            record.message = record.success.Value
                ? "Compilation finished without compiler errors."
                : "Compilation finished with compiler errors.";
            record.updatedAtUtc = nowUtc.ToString("o");
            SaveVerifyJob(record);
            return record;
        }

        private static VerifyJobRecord UpdatePendingState(VerifyJobRecord record, string state, string message)
        {
            if (!string.Equals(record.state, state, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(record.message, message, StringComparison.Ordinal))
            {
                record.state = state;
                record.message = message;
                record.updatedAtUtc = DateTime.UtcNow.ToString("o");
                SaveVerifyJob(record);
            }

            return record;
        }

        private static bool TryGetStartedAtUtc(VerifyJobRecord record, out DateTime startedAtUtc)
        {
            if (!DateTime.TryParse(
                    record.startedAtUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out startedAtUtc))
            {
                startedAtUtc = default;
                return false;
            }

            return true;
        }

        private static IReadOnlyList<CompilerMessageRecord> LoadCompilerMessages()
        {
            var json = SessionState.GetString(CompilerMessagesKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<CompilerMessageRecord>();
            }

            try
            {
                return JsonConvert.DeserializeObject<List<CompilerMessageRecord>>(json) ?? new List<CompilerMessageRecord>();
            }
            catch
            {
                SessionState.EraseString(CompilerMessagesKey);
                return new List<CompilerMessageRecord>();
            }
        }

        private static void SaveCompilerMessages(List<CompilerMessageRecord> records)
        {
            SessionState.SetString(CompilerMessagesKey, JsonConvert.SerializeObject(records));
        }
    }
}
