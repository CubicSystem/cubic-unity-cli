using System.Text;
using UnityEditor;
using UnityEngine;

namespace Cubix.UnityCli
{
    internal sealed class UnityCliSetupWindow : EditorWindow
    {
        private Vector2 _scrollPosition;
        private string _actionLog;
        private CliInstallationStatus _cliStatus;
        private SkillInstallStatus _codexSkills;
        private SkillInstallStatus _claudeSkills;

        [MenuItem("Tools/Cubix/Unity CLI")]
        private static void OpenWindow()
        {
            var window = GetWindow<UnityCliSetupWindow>("Unity CLI");
            window.minSize = new Vector2(680f, 540f);
            window.RefreshAll();
        }

        private void OnEnable()
        {
            RefreshAll();
        }

        private void OnGUI()
        {
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            DrawHeader();
            EditorGUILayout.Space(8f);
            DrawConnectionSection();
            EditorGUILayout.Space(8f);
            DrawCliSection();
            EditorGUILayout.Space(8f);
            DrawSkillsSection();
            EditorGUILayout.Space(8f);
            DrawActionLog();
            EditorGUILayout.EndScrollView();
        }

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("Cubix Unity CLI", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Package Version", PackageLayout.PackageVersion);
            EditorGUILayout.LabelField("Package Root", PackageLayout.PackageRoot);
        }

        private void DrawConnectionSection()
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField("Connection", EditorStyles.boldLabel);
                var snapshot = ConnectionService.GetSnapshot();
                EditorGUILayout.LabelField("Connected", snapshot.connected ? "Yes" : "No");
                EditorGUILayout.LabelField("Ready", snapshot.ready ? "Yes" : "No");
                EditorGUILayout.LabelField("Port", snapshot.port > 0 ? snapshot.port.ToString() : "-");
                EditorGUILayout.LabelField("URL", string.IsNullOrWhiteSpace(snapshot.url) ? "-" : snapshot.url);
                EditorGUILayout.LabelField("Project Hash", snapshot.projectHash);
                EditorGUILayout.LabelField("Command Count", snapshot.commandCount.ToString());
                EditorGUILayout.LabelField("Last Error", string.IsNullOrWhiteSpace(snapshot.lastError) ? "-" : snapshot.lastError);
                var autoConnect = EditorGUILayout.Toggle("Auto Connect On Load", snapshot.autoConnectOnLoad);
                if (autoConnect != snapshot.autoConnectOnLoad)
                {
                    ConnectionService.AutoConnectOnLoad = autoConnect;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Connect"))
                    {
                        LogAction(ConnectionService.Connect() ? "Connected." : "Connection failed: " + ConnectionService.GetSnapshot().lastError);
                        RefreshAll();
                    }

                    if (GUILayout.Button("Disconnect"))
                    {
                        ConnectionService.Disconnect();
                        LogAction("Disconnected.");
                        RefreshAll();
                    }

                    if (GUILayout.Button("Reconnect"))
                    {
                        LogAction(ConnectionService.Reconnect() ? "Reconnected." : "Reconnect failed: " + ConnectionService.GetSnapshot().lastError);
                        RefreshAll();
                    }

                    if (GUILayout.Button("Refresh Status"))
                    {
                        ConnectionService.RefreshStatus();
                        LogAction("Connection status refreshed.");
                        RefreshAll();
                    }
                }
            }
        }

        private void DrawCliSection()
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField("CLI", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Python", _cliStatus.pythonAvailable ? _cliStatus.pythonVersion : "Missing");
                EditorGUILayout.LabelField("pip", _cliStatus.pipAvailable ? _cliStatus.pipVersion : "Missing");
                EditorGUILayout.LabelField("pipx", _cliStatus.pipxAvailable ? _cliStatus.pipxVersion : "Missing");
                EditorGUILayout.LabelField("cubix-cli", _cliStatus.cliInstalled ? _cliStatus.cliVersion ?? "Installed" : "Missing");
                EditorGUILayout.HelpBox(BuildCliDiagnosticsText(), MessageType.None);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Install CLI"))
                    {
                        RunInstallAction(CliInstallationService.InstallCli());
                    }

                    if (GUILayout.Button("Update CLI"))
                    {
                        RunInstallAction(CliInstallationService.UpdateCli());
                    }

                    if (GUILayout.Button("Repair CLI"))
                    {
                        RunInstallAction(CliInstallationService.RepairCli());
                    }

                    if (GUILayout.Button("Uninstall CLI"))
                    {
                        RunInstallAction(CliInstallationService.UninstallCli());
                    }

                    if (GUILayout.Button("Copy Diagnostics"))
                    {
                        GUIUtility.systemCopyBuffer = BuildDiagnostics();
                        LogAction("Copied diagnostics to the clipboard.");
                    }
                }
            }
        }

        private void DrawSkillsSection()
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField("Skills", EditorStyles.boldLabel);
                DrawSkillStatus("Codex", _codexSkills);
                DrawSkillStatus("Claude Code", _claudeSkills);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Install Codex Skills"))
                    {
                        RunInstallAction(SkillInstallationService.Install(SkillAgentTarget.Codex));
                    }

                    if (GUILayout.Button("Install Claude Code Skills"))
                    {
                        RunInstallAction(SkillInstallationService.Install(SkillAgentTarget.ClaudeCode));
                    }

                    if (GUILayout.Button("Install All Skills"))
                    {
                        RunInstallAction(SkillInstallationService.InstallAll());
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Repair Skills"))
                    {
                        RunInstallAction(SkillInstallationService.RepairAll());
                    }

                    if (GUILayout.Button("Remove Codex Skills"))
                    {
                        RunInstallAction(SkillInstallationService.Remove(SkillAgentTarget.Codex));
                    }

                    if (GUILayout.Button("Remove Claude Code Skills"))
                    {
                        RunInstallAction(SkillInstallationService.Remove(SkillAgentTarget.ClaudeCode));
                    }
                }
            }
        }

        private void DrawSkillStatus(string label, SkillInstallStatus status)
        {
            EditorGUILayout.LabelField(label, status.state.ToString());
            EditorGUILayout.LabelField(label + " Path", status.rootPath);
            foreach (var skill in status.skills)
            {
                EditorGUILayout.LabelField("  " + skill.name, skill.state + (string.IsNullOrWhiteSpace(skill.installedVersion) ? string.Empty : " (" + skill.installedVersion + ")"));
            }
        }

        private void DrawActionLog()
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField("Action Log", EditorStyles.boldLabel);
                EditorGUILayout.TextArea(string.IsNullOrWhiteSpace(_actionLog) ? "No actions yet." : _actionLog, GUILayout.MinHeight(180f));
            }
        }

        private void RefreshAll()
        {
            _cliStatus = CliInstallationService.Inspect();
            _codexSkills = SkillInstallationService.Inspect(SkillAgentTarget.Codex);
            _claudeSkills = SkillInstallationService.Inspect(SkillAgentTarget.ClaudeCode);
            Repaint();
        }

        private void RunInstallAction(string result)
        {
            LogAction(result);
            RefreshAll();
        }

        private void LogAction(string message)
        {
            var entry = "[" + System.DateTime.Now.ToString("HH:mm:ss") + "] " + message;
            _actionLog = string.IsNullOrWhiteSpace(_actionLog)
                ? entry
                : entry + System.Environment.NewLine + System.Environment.NewLine + _actionLog;
        }

        private string BuildDiagnostics()
        {
            var builder = new StringBuilder();
            var connection = ConnectionService.GetSnapshot();
            builder.AppendLine("Connection");
            builder.AppendLine("Connected: " + connection.connected);
            builder.AppendLine("Ready: " + connection.ready);
            builder.AppendLine("Port: " + connection.port);
            builder.AppendLine("URL: " + connection.url);
            builder.AppendLine("ProjectHash: " + connection.projectHash);
            builder.AppendLine("CommandCount: " + connection.commandCount);
            builder.AppendLine("LastError: " + connection.lastError);
            builder.AppendLine();
            builder.AppendLine("CLI");
            builder.AppendLine(BuildCliDiagnosticsText());
            builder.AppendLine();
            builder.AppendLine("Skills");
            builder.AppendLine(SkillInstallationService.GetDiagnostics());
            return builder.ToString().Trim();
        }

        private string BuildCliDiagnosticsText()
        {
            var builder = new StringBuilder();
            builder.AppendLine(_cliStatus.diagnostics ?? "No diagnostics available.");
            builder.AppendLine();
            builder.AppendLine("CLI capabilities:");
            builder.AppendLine("- metadata catalog: commands.list / commands.describe");
            builder.AppendLine("- dynamic invocation: cubix-cli call");
            builder.AppendLine("- safety checks: cubix-cli preflight");
            builder.AppendLine("- batch execution: cubix-cli batch");
            builder.AppendLine("- operational commands: status, menu, refresh, reserialize");
            builder.AppendLine("- local command count: " + CommandRouter.ListCommands(includeUnsafe: true).Count);
            return builder.ToString().Trim();
        }
    }
}
