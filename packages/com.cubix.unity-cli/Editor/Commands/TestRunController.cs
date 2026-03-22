using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace Cubix.UnityCli
{
    [InitializeOnLoad]
    internal static class TestRunController
    {
        [Serializable]
        internal sealed class TestFilterRecord
        {
            public List<string> assemblyNames = new List<string>();
            public List<string> categoryNames = new List<string>();
            public List<string> testNames = new List<string>();
        }

        [Serializable]
        internal sealed class TestCaseResultRecord
        {
            public string name;
            public string fullName;
            public string testName;
            public string fixtureName;
            public string assemblyName;
            public string resultState;
            public string outcome;
            public string output;
            public string message;
            public string stackTrace;
            public double durationSeconds;
            public long durationMs;
            public List<string> categories = new List<string>();
        }

        [Serializable]
        internal sealed class TestJobRecord
        {
            public string id;
            public string runnerJobId;
            public string state;
            public string platform;
            public string startedAtUtc;
            public string updatedAtUtc;
            public int timeoutMs;
            public bool? success;
            public string message;
            public string failureType;
            public string resultsPath;
            public int totalCount;
            public int completedCount;
            public int passedCount;
            public int failedCount;
            public int skippedCount;
            public int inconclusiveCount;
            public double durationSeconds;
            public List<CompilationAwaiter.CompilerMessageRecord> compilerErrors = new List<CompilationAwaiter.CompilerMessageRecord>();
            public TestFilterRecord filter = new TestFilterRecord();
            public List<TestCaseResultRecord> failures = new List<TestCaseResultRecord>();
        }

        private const string TestJobKey = "cubix_cli.test.job";
        private const int MaxFailureRecords = 50;

        private static readonly TestRunnerApi RunnerApi;
        private static readonly TestRunCallbacks Callbacks;

        static TestRunController()
        {
            RunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();
            RunnerApi.hideFlags = HideFlags.HideAndDontSave;
            Callbacks = new TestRunCallbacks();
            RunnerApi.RegisterCallbacks(Callbacks);
            EditorApplication.update += Pump;
            EditorApplication.quitting += Cleanup;
        }

        public static object StartRun(JObject parameters)
        {
            var existing = LoadTestJob();
            if (existing != null && !existing.success.HasValue)
            {
                throw new InvalidOperationException("A Unity test run is already in progress.");
            }

            var platform = ParsePlatform(parameters?.Value<string>("platform"));
            var record = new TestJobRecord
            {
                id = Guid.NewGuid().ToString("N"),
                state = "queued",
                platform = platform.ToString(),
                startedAtUtc = DateTime.UtcNow.ToString("o"),
                updatedAtUtc = DateTime.UtcNow.ToString("o"),
                timeoutMs = Math.Max(parameters?.Value<int?>("timeoutMs") ?? 180000, 1000),
                message = "Test run queued.",
                resultsPath = NormalizeResultsPath(parameters?.Value<string>("resultsPath")),
                filter = new TestFilterRecord
                {
                    assemblyNames = ReadStringList(parameters, "assemblyNames"),
                    categoryNames = ReadStringList(parameters, "categoryNames"),
                    testNames = ReadStringList(parameters, "testNames")
                }
            };

            SaveTestJob(record);
            Pump();
            return BuildTestState(LoadTestJob() ?? record);
        }

        public static object GetCurrentJob()
        {
            return BuildTestState(RefreshCurrentJobState(allowStart: false));
        }

        public static bool HasPendingRun()
        {
            var record = RefreshCurrentJobState(allowStart: false);
            return record != null && !record.success.HasValue;
        }

        private static void Cleanup()
        {
            try
            {
                RunnerApi?.UnregisterCallbacks(Callbacks);
            }
            catch
            {
            }

            if (RunnerApi != null)
            {
                ScriptableObject.DestroyImmediate(RunnerApi);
            }
        }

        private static void Pump()
        {
            RefreshCurrentJobState(allowStart: true);
        }

        private static TestJobRecord RefreshCurrentJobState(bool allowStart)
        {
            var record = LoadTestJob();
            if (record == null || record.success.HasValue)
            {
                return record;
            }

            if (TryMarkTimedOut(record))
            {
                return record;
            }

            if (!string.Equals(record.state, "queued", StringComparison.OrdinalIgnoreCase))
            {
                return record;
            }

            var waitingMessage = BuildQueuedMessage();
            if (!CanStartRun())
            {
                UpdatePendingState(record, "queued", waitingMessage);
                return record;
            }

            if (allowStart)
            {
                LaunchRun(record);
            }

            return LoadTestJob();
        }

        private static bool TryMarkTimedOut(TestJobRecord record)
        {
            if (!TryGetStartedAtUtc(record, out var startedAtUtc))
            {
                return false;
            }

            if (DateTime.UtcNow <= startedAtUtc.AddMilliseconds(record.timeoutMs))
            {
                return false;
            }

            record.state = "timed_out";
            record.success = false;
            record.failureType = "timeout";
            record.message = "Test run timed out while waiting for completion.";
            record.updatedAtUtc = DateTime.UtcNow.ToString("o");
            SaveTestJob(record);
            return true;
        }

        private static bool CanStartRun()
        {
            return !ConnectionService.IsReloading &&
                   !EditorApplication.isCompiling &&
                   !EditorApplication.isUpdating &&
                   !EditorApplication.isPlaying &&
                   !EditorApplication.isPlayingOrWillChangePlaymode &&
                   !CompilationAwaiter.HasPendingVerifyJob();
        }

        private static string BuildQueuedMessage()
        {
            if (ConnectionService.IsReloading)
            {
                return "Waiting for Unity to reconnect after domain reload.";
            }

            if (EditorApplication.isUpdating)
            {
                return "Waiting for Unity asset refresh to finish before running tests.";
            }

            if (EditorApplication.isCompiling)
            {
                return "Waiting for Unity script compilation to finish before running tests.";
            }

            if (CompilationAwaiter.HasPendingVerifyJob())
            {
                return "Waiting for verify to finish before running tests.";
            }

            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return "Waiting for Unity to exit play mode before running tests.";
            }

            return "Waiting to start the Unity test run.";
        }

        private static void LaunchRun(TestJobRecord record)
        {
            try
            {
                var compilerErrors = CompilationAwaiter.GetCompilerMessages("error").ToList();
                if (compilerErrors.Count > 0)
                {
                    record.state = "failed";
                    record.success = false;
                    record.failureType = "compiler";
                    record.compilerErrors = compilerErrors;
                    record.message = "Unity has compiler errors. Run verify and fix compilation before starting tests.";
                    record.updatedAtUtc = DateTime.UtcNow.ToString("o");
                    SaveTestJob(record);
                    return;
                }

                record.state = "running";
                record.failureType = null;
                record.compilerErrors = new List<CompilationAwaiter.CompilerMessageRecord>();
                record.message = "Starting the Unity test run.";
                record.updatedAtUtc = DateTime.UtcNow.ToString("o");
                SaveTestJob(record);

                var filter = new Filter();
                SetFilterField(filter, "testMode", ParsePlatform(record.platform));
                SetFilterField(filter, "assemblyNames", ToArrayOrNull(record.filter.assemblyNames));
                SetFilterField(filter, "categoryNames", ToArrayOrNull(record.filter.categoryNames));
                SetFilterField(filter, "testNames", ToArrayOrNull(record.filter.testNames));

                var executionSettings = new ExecutionSettings(filter);
                SetExecutionSettingsField(executionSettings, "runSynchronously", false);
                var runnerJobId = RunnerApi.Execute(executionSettings);

                var latest = LoadTestJob() ?? record;
                latest.runnerJobId = runnerJobId;
                latest.updatedAtUtc = DateTime.UtcNow.ToString("o");
                if (string.IsNullOrWhiteSpace(latest.message))
                {
                    latest.message = "Unity test run started.";
                }

                SaveTestJob(latest);
            }
            catch (Exception exception)
            {
                var compilerErrors = CompilationAwaiter.GetCompilerMessages("error").ToList();
                record.state = "failed";
                record.success = false;
                record.failureType = compilerErrors.Count > 0 ? "compiler" : "infrastructure";
                record.compilerErrors = compilerErrors;
                record.message = "Could not start the Unity test run: " + exception.Message;
                record.updatedAtUtc = DateTime.UtcNow.ToString("o");
                SaveTestJob(record);
            }
        }

        private static void SetFilterField(Filter filter, string fieldName, object value)
        {
            var field = typeof(Filter).GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            field?.SetValue(filter, value);
        }

        private static void SetExecutionSettingsField(ExecutionSettings settings, string fieldName, object value)
        {
            var field = typeof(ExecutionSettings).GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            field?.SetValue(settings, value);
        }

        private static string[] ToArrayOrNull(List<string> values)
        {
            return values != null && values.Count > 0 ? values.ToArray() : null;
        }

        private static List<string> ReadStringList(JObject parameters, string propertyName)
        {
            var values = new List<string>();
            if (parameters == null || string.IsNullOrWhiteSpace(propertyName))
            {
                return values;
            }

            var token = parameters[propertyName];
            switch (token)
            {
                case JArray array:
                    foreach (var entry in array.Values<string>())
                    {
                        if (!string.IsNullOrWhiteSpace(entry))
                        {
                            values.Add(entry.Trim());
                        }
                    }
                    break;
                case JValue value when value.Type == JTokenType.String:
                    var text = value.Value<string>();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        values.Add(text.Trim());
                    }
                    break;
            }

            return values
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static TestMode ParsePlatform(string platform)
        {
            if (string.IsNullOrWhiteSpace(platform))
            {
                throw new ArgumentException("A test platform is required. Use 'EditMode' or 'PlayMode'.");
            }

            if (Enum.TryParse(platform, true, out TestMode parsed))
            {
                return parsed;
            }

            throw new ArgumentException("Unsupported test platform '" + platform + "'. Use 'EditMode' or 'PlayMode'.");
        }

        private static string NormalizeResultsPath(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            var trimmed = input.Trim();
            if (Path.IsPathRooted(trimmed))
            {
                return Path.GetFullPath(trimmed);
            }

            return Path.GetFullPath(Path.Combine(ConnectorPaths.ProjectPath, trimmed));
        }

        private static TestJobRecord LoadTestJob()
        {
            var json = SessionState.GetString(TestJobKey, string.Empty);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<TestJobRecord>(json);
            }
            catch
            {
                SessionState.EraseString(TestJobKey);
                return null;
            }
        }

        private static void SaveTestJob(TestJobRecord record)
        {
            SessionState.SetString(TestJobKey, JsonConvert.SerializeObject(record));
        }

        private static void UpdatePendingState(TestJobRecord record, string state, string message)
        {
            if (!string.Equals(record.state, state, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(record.message, message, StringComparison.Ordinal))
            {
                record.state = state;
                record.message = message;
                record.updatedAtUtc = DateTime.UtcNow.ToString("o");
                SaveTestJob(record);
            }
        }

        private static bool TryGetStartedAtUtc(TestJobRecord record, out DateTime startedAtUtc)
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

        private static void HandleRunStarted(ITestAdaptor testsToRun)
        {
            var record = LoadTestJob();
            if (record == null || record.success.HasValue)
            {
                return;
            }

            record.state = "running";
            record.totalCount = testsToRun?.TestCaseCount ?? record.totalCount;
            record.completedCount = 0;
            record.passedCount = 0;
            record.failedCount = 0;
            record.skippedCount = 0;
            record.inconclusiveCount = 0;
            record.failureType = null;
            record.compilerErrors = new List<CompilationAwaiter.CompilerMessageRecord>();
            record.failures = new List<TestCaseResultRecord>();
            record.message = record.totalCount > 0
                ? "Unity test run started (" + record.totalCount + " test cases)."
                : "Unity test run started.";
            record.updatedAtUtc = DateTime.UtcNow.ToString("o");
            SaveTestJob(record);
        }

        private static void HandleTestFinished(ITestResultAdaptor result)
        {
            var record = LoadTestJob();
            if (record == null || record.success.HasValue || result?.Test == null || result.Test.IsSuite)
            {
                return;
            }

            record.completedCount = Math.Min(record.completedCount + 1, Math.Max(record.totalCount, record.completedCount + 1));
            var bucket = GetResultBucket(result.ResultState);
            switch (bucket)
            {
                case "passed":
                    record.passedCount++;
                    break;
                case "failed":
                    record.failedCount++;
                    if (record.failures.Count < MaxFailureRecords)
                    {
                        record.failures.Add(ToCaseResultRecord(result));
                    }
                    break;
                case "skipped":
                    record.skippedCount++;
                    break;
                case "inconclusive":
                    record.inconclusiveCount++;
                    break;
            }

            record.updatedAtUtc = DateTime.UtcNow.ToString("o");
            record.message = record.totalCount > 0
                ? "Running Unity tests (" + record.completedCount + "/" + record.totalCount + ")."
                : "Running Unity tests.";
            SaveTestJob(record);
        }

        private static void HandleRunFinished(ITestResultAdaptor result)
        {
            var record = LoadTestJob();
            if (record == null)
            {
                return;
            }

            if (result != null)
            {
                record.totalCount = result.Test?.TestCaseCount ?? record.totalCount;
                record.passedCount = result.PassCount;
                record.failedCount = result.FailCount;
                record.skippedCount = result.SkipCount;
                record.inconclusiveCount = result.InconclusiveCount;
                record.completedCount = record.passedCount + record.failedCount + record.skippedCount + record.inconclusiveCount;
                record.durationSeconds = result.Duration;
                record.failures = CollectLeafResults(result)
                    .Where(entry => string.Equals(GetResultBucket(entry.resultState), "failed", StringComparison.OrdinalIgnoreCase))
                    .Take(MaxFailureRecords)
                    .ToList();
            }

            if (!string.Equals(record.state, "timed_out", StringComparison.OrdinalIgnoreCase))
            {
                if (result == null)
                {
                    record.success = false;
                    record.state = "failed";
                    record.failureType = "infrastructure";
                    record.message = "Unity test run finished without a result payload.";
                }
                else
                {
                    var outcome = GetResultBucket(result.ResultState);
                    if (string.Equals(outcome, "canceled", StringComparison.OrdinalIgnoreCase))
                    {
                        record.success = false;
                        record.state = "canceled";
                        record.failureType = "canceled";
                        record.message = "Unity tests were canceled before completion.";
                    }
                    else
                    {
                        var passed = result.FailCount == 0;
                        record.success = passed;
                        record.state = "completed";
                        record.failureType = passed ? null : "test";
                        record.message = passed
                            ? "Unity tests completed without failing test cases."
                            : "Unity tests completed with failing test cases.";
                    }
                }
            }
            else
            {
                record.failureType = "timeout";
                record.message = "Unity tests exceeded the configured timeout before completion.";
            }

            record.updatedAtUtc = DateTime.UtcNow.ToString("o");
            SaveTestJob(record);
            WriteResultsFile(record, result);
        }

        private static string GetResultBucket(string resultState)
        {
            var normalized = resultState ?? string.Empty;
            if (normalized.StartsWith("Passed", StringComparison.OrdinalIgnoreCase))
            {
                return "passed";
            }

            if (normalized.StartsWith("Failed", StringComparison.OrdinalIgnoreCase))
            {
                return "failed";
            }

            if (normalized.StartsWith("Skipped", StringComparison.OrdinalIgnoreCase))
            {
                return "skipped";
            }

            if (normalized.StartsWith("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith("Canceled", StringComparison.OrdinalIgnoreCase))
            {
                return "canceled";
            }

            return "inconclusive";
        }

        private static List<TestCaseResultRecord> CollectLeafResults(ITestResultAdaptor root)
        {
            var results = new List<TestCaseResultRecord>();
            CollectLeafResults(root, results);
            return results;
        }

        private static void CollectLeafResults(ITestResultAdaptor result, List<TestCaseResultRecord> results)
        {
            if (result == null)
            {
                return;
            }

            if (result.Test != null && !result.Test.IsSuite)
            {
                results.Add(ToCaseResultRecord(result));
                return;
            }

            if (!result.HasChildren)
            {
                return;
            }

            foreach (var child in result.Children ?? Enumerable.Empty<ITestResultAdaptor>())
            {
                CollectLeafResults(child, results);
            }
        }

        private static TestCaseResultRecord ToCaseResultRecord(ITestResultAdaptor result)
        {
            return new TestCaseResultRecord
            {
                name = result.Name,
                fullName = result.FullName,
                testName = result.Name,
                fixtureName = GetFixtureName(result),
                assemblyName = GetAssemblyName(result),
                resultState = result.ResultState,
                outcome = GetResultBucket(result.ResultState),
                output = result.Output,
                message = result.Message,
                stackTrace = result.StackTrace,
                durationSeconds = result.Duration,
                durationMs = ToDurationMs(result.Duration),
                categories = result.Test?.Categories?.Where(category => !string.IsNullOrWhiteSpace(category)).ToList() ?? new List<string>()
            };
        }

        private static string GetFixtureName(ITestResultAdaptor result)
        {
            var test = result?.Test;
            var fixtureName = ReadStringProperty(test, "ClassName");
            if (!string.IsNullOrWhiteSpace(fixtureName))
            {
                return fixtureName;
            }

            return GetFixtureNameFromFullName(result?.FullName);
        }

        private static string GetAssemblyName(ITestResultAdaptor result)
        {
            var test = result?.Test;
            return FirstNonEmpty(
                ReadStringProperty(test, "AssemblyName"),
                ReadStringProperty(result, "AssemblyName"));
        }

        private static string GetFixtureNameFromFullName(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
            {
                return null;
            }

            var lastSeparator = fullName.LastIndexOf('.');
            if (lastSeparator <= 0)
            {
                return fullName;
            }

            return fullName.Substring(0, lastSeparator);
        }

        private static string ReadStringProperty(object instance, string propertyName)
        {
            if (instance == null || string.IsNullOrWhiteSpace(propertyName))
            {
                return null;
            }

            var property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null || property.PropertyType != typeof(string))
            {
                return null;
            }

            return property.GetValue(instance) as string;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private static long ToDurationMs(double durationSeconds)
        {
            return (long)Math.Round(Math.Max(0.0, durationSeconds) * 1000.0);
        }

        private static object BuildTestState(TestJobRecord record)
        {
            if (record == null)
            {
                return null;
            }

            return new
            {
                id = record.id,
                runnerJobId = record.runnerJobId,
                state = record.state,
                platform = record.platform,
                startedAtUtc = record.startedAtUtc,
                updatedAtUtc = record.updatedAtUtc,
                timeoutMs = record.timeoutMs,
                success = record.success,
                message = record.message,
                failureType = record.failureType,
                resultsPath = record.resultsPath,
                totalCount = record.totalCount,
                completedCount = record.completedCount,
                passedCount = record.passedCount,
                failedCount = record.failedCount,
                skippedCount = record.skippedCount,
                inconclusiveCount = record.inconclusiveCount,
                durationSeconds = record.durationSeconds,
                durationMs = ToDurationMs(record.durationSeconds),
                summary = new
                {
                    total = record.totalCount,
                    completed = record.completedCount,
                    passed = record.passedCount,
                    failed = record.failedCount,
                    skipped = record.skippedCount,
                    inconclusive = record.inconclusiveCount,
                    durationMs = ToDurationMs(record.durationSeconds),
                    totalCount = record.totalCount,
                    completedCount = record.completedCount,
                    passedCount = record.passedCount,
                    failedCount = record.failedCount,
                    skippedCount = record.skippedCount,
                    inconclusiveCount = record.inconclusiveCount,
                    durationSeconds = record.durationSeconds
                },
                filter = record.filter,
                compilerErrors = record.compilerErrors ?? new List<CompilationAwaiter.CompilerMessageRecord>(),
                failures = record.failures ?? new List<TestCaseResultRecord>()
            };
        }

        private static void WriteResultsFile(TestJobRecord record, ITestResultAdaptor result)
        {
            if (string.IsNullOrWhiteSpace(record?.resultsPath))
            {
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(record.resultsPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var payload = new
                {
                    id = record.id,
                    runnerJobId = record.runnerJobId,
                    state = record.state,
                    success = record.success,
                    platform = record.platform,
                    startedAtUtc = record.startedAtUtc,
                    updatedAtUtc = record.updatedAtUtc,
                    timeoutMs = record.timeoutMs,
                    message = record.message,
                    failureType = record.failureType,
                    resultsPath = record.resultsPath,
                    durationMs = ToDurationMs(record.durationSeconds),
                    summary = new
                    {
                        total = record.totalCount,
                        completed = record.completedCount,
                        passed = record.passedCount,
                        failed = record.failedCount,
                        skipped = record.skippedCount,
                        inconclusive = record.inconclusiveCount,
                        durationMs = ToDurationMs(record.durationSeconds),
                        totalCount = record.totalCount,
                        completedCount = record.completedCount,
                        passedCount = record.passedCount,
                        failedCount = record.failedCount,
                        skippedCount = record.skippedCount,
                        inconclusiveCount = record.inconclusiveCount,
                        durationSeconds = record.durationSeconds
                    },
                    filter = record.filter,
                    compilerErrors = record.compilerErrors ?? new List<CompilationAwaiter.CompilerMessageRecord>(),
                    failures = record.failures,
                    tests = result != null ? CollectLeafResults(result) : new List<TestCaseResultRecord>()
                };

                File.WriteAllText(record.resultsPath, JsonConvert.SerializeObject(payload, Formatting.Indented));
            }
            catch (Exception exception)
            {
                record.message = "Unity tests finished, but writing results failed: " + exception.Message;
                record.updatedAtUtc = DateTime.UtcNow.ToString("o");
                SaveTestJob(record);
            }
        }

        private sealed class TestRunCallbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun)
            {
                HandleRunStarted(testsToRun);
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                HandleRunFinished(result);
            }

            public void TestStarted(ITestAdaptor test)
            {
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                HandleTestFinished(result);
            }
        }
    }
}
