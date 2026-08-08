using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace CubicEngine.UnityCli
{
    [InitializeOnLoad]
    internal static class ConsoleStore
    {
        [Serializable]
        internal sealed class ConsoleEntry
        {
            [JsonConstructor]
            public ConsoleEntry(string message, string stackTrace, string level, string source, string timestampUtc)
            {
                this.message = message;
                this.stackTrace = stackTrace;
                this.level = level;
                this.source = source;
                this.timestampUtc = timestampUtc;
            }

            public readonly string message;
            public readonly string stackTrace;
            public readonly string level;
            public readonly string source;
            public readonly string timestampUtc;
        }

        private const int MaxEntries = 250;
        private const string SessionKey = "cubic_cli.console.entries";

        private static readonly BoundedConcurrentQueue<ConsoleEntry> PendingEntries =
            new BoundedConcurrentQueue<ConsoleEntry>(MaxEntries);
        private static readonly List<ConsoleEntry> Entries;
        private static bool _subscribed;

        static ConsoleStore()
        {
            Entries = LoadEntries();
            Subscribe();
        }

        public static IReadOnlyList<ConsoleEntry> Read(string level = null, int limit = 50, string source = null)
        {
            IEnumerable<ConsoleEntry> query = Entries;
            if (!string.IsNullOrWhiteSpace(level))
            {
                query = query.Where(entry => string.Equals(entry.level, level, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(source))
            {
                query = query.Where(entry => string.Equals(entry.source, source, StringComparison.OrdinalIgnoreCase));
            }

            return query
                .Reverse()
                .Take(Mathf.Clamp(limit, 1, MaxEntries))
                .Reverse()
                .ToList();
        }

        public static IReadOnlyList<object> ReadMerged(string level = null, int limit = 50, string source = null)
        {
            var results = new List<object>();
            if (string.Equals(source, "compiler", StringComparison.OrdinalIgnoreCase))
            {
                results.AddRange(CompilationAwaiter.GetCompilerMessages(level).Cast<object>());
                return results;
            }

            results.AddRange(Read(level, limit, source).Cast<object>());
            var remaining = limit - results.Count;
            if (remaining > 0)
            {
                results.AddRange(CompilationAwaiter.GetCompilerMessages(level).Take(remaining).Cast<object>());
            }

            return results;
        }

        public static void Clear()
        {
            PendingEntries.Clear();
            Entries.Clear();
            SaveEntries();
            CompilationAwaiter.ClearCompilerMessages();
            ClearEditorConsole();
        }

        internal static void EnqueueLog(string condition, string stackTrace, LogType type)
        {
            PendingEntries.Enqueue(new ConsoleEntry(
                condition,
                stackTrace,
                NormalizeLevel(type),
                "editor",
                DateTime.UtcNow.ToString("o")));
        }

        internal static void DrainPendingLogs()
        {
            var pending = PendingEntries.Drain();
            if (pending.Count == 0)
            {
                return;
            }

            Entries.AddRange(pending);
            if (Entries.Count > MaxEntries)
            {
                Entries.RemoveRange(0, Entries.Count - MaxEntries);
            }

            SaveEntries();
        }

        internal static int PendingCount => PendingEntries.Count;

        private static List<ConsoleEntry> LoadEntries()
        {
            var json = SessionState.GetString(SessionKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<ConsoleEntry>();
            }

            try
            {
                return JsonConvert.DeserializeObject<List<ConsoleEntry>>(json) ?? new List<ConsoleEntry>();
            }
            catch
            {
                return new List<ConsoleEntry>();
            }
        }

        private static void SaveEntries()
        {
            SessionState.SetString(SessionKey, JsonConvert.SerializeObject(Entries));
        }

        private static void Subscribe()
        {
            if (_subscribed)
            {
                return;
            }

            Application.logMessageReceivedThreaded += EnqueueLog;
            EditorApplication.update += DrainPendingLogs;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += Shutdown;
            _subscribed = true;
        }

        private static void Shutdown()
        {
            if (!_subscribed)
            {
                return;
            }

            Application.logMessageReceivedThreaded -= EnqueueLog;
            EditorApplication.update -= DrainPendingLogs;
            AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
            EditorApplication.quitting -= Shutdown;
            _subscribed = false;

            DrainPendingLogs();
        }

        private static string NormalizeLevel(LogType type)
        {
            switch (type)
            {
                case LogType.Error:
                case LogType.Exception:
                case LogType.Assert:
                    return "error";
                case LogType.Warning:
                    return "warning";
                default:
                    return "info";
            }
        }

        private static void ClearEditorConsole()
        {
            var logEntries = Type.GetType("UnityEditor.LogEntries, UnityEditor.dll");
            var clearMethod = logEntries?.GetMethod("Clear", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
            clearMethod?.Invoke(null, null);
        }
    }
}
