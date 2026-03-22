using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace Cubix.UnityCli
{
    [InitializeOnLoad]
    internal static class ConsoleStore
    {
        [Serializable]
        internal sealed class ConsoleEntry
        {
            public string message;
            public string stackTrace;
            public string level;
            public string source;
            public string timestampUtc;
        }

        private const int MaxEntries = 250;
        private const string SessionKey = "cubix_cli.console.entries";

        private static readonly List<ConsoleEntry> Entries;

        static ConsoleStore()
        {
            Entries = LoadEntries();
            Application.logMessageReceivedThreaded += HandleLog;
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
            Entries.Clear();
            SaveEntries();
            CompilationAwaiter.ClearCompilerMessages();
            ClearEditorConsole();
        }

        private static void HandleLog(string condition, string stackTrace, LogType type)
        {
            lock (Entries)
            {
                Entries.Add(new ConsoleEntry
                {
                    message = condition,
                    stackTrace = stackTrace,
                    level = NormalizeLevel(type),
                    source = "editor",
                    timestampUtc = DateTime.UtcNow.ToString("o")
                });

                while (Entries.Count > MaxEntries)
                {
                    Entries.RemoveAt(0);
                }

                SaveEntries();
            }
        }

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
