using System.Text;
using UnityEditor;
using UnityEngine;

namespace Cubix.UnityCli
{
    internal sealed class UnityCliSetupWindow : EditorWindow
    {
        private static readonly Color SuccessColor = new Color(0.28f, 0.68f, 0.34f);
        private static readonly Color FailureColor = new Color(0.82f, 0.34f, 0.34f);
        private const float ButtonMinHeight = 24f;

        private Vector2 _scrollPosition;
        private string _actionLog;
        private CliInstallationStatus _cliStatus;
        private SkillInstallStatus _codexSkills;
        private SkillInstallStatus _claudeSkills;
        private GUIStyle _wrappedLabelStyle;
        private GUIStyle _wrappedStatusStyle;
        private GUIStyle _wrappedTextAreaStyle;
        private GUIStyle _wrappedButtonStyle;

        private sealed class ButtonDefinition
        {
            public readonly string Label;
            public readonly System.Action Action;
            public readonly Color? BackgroundColor;

            public ButtonDefinition(string label, System.Action action, Color? backgroundColor = null)
            {
                Label = label;
                Action = action;
                BackgroundColor = backgroundColor;
            }
        }

        [MenuItem("Tools/Cubix/Unity CLI")]
        private static void OpenWindow()
        {
            var window = GetWindow<UnityCliSetupWindow>("Unity CLI");
            window.minSize = new Vector2(520f, 540f);
            window.RefreshAll();
        }

        private void OnEnable()
        {
            RefreshAll();
        }

        private void OnGUI()
        {
            EnsureStyles();

            var previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = Mathf.Clamp(EditorGUIUtility.currentViewWidth * 0.32f, 120f, 180f);

            try
            {
                _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
                DrawHeader();
                EditorGUILayout.Space(8f);
                DrawQuickSetupSection();
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
            finally
            {
                EditorGUIUtility.labelWidth = previousLabelWidth;
            }
        }

        private void DrawHeader()
        {
            EditorGUILayout.LabelField("Cubix Unity CLI", EditorStyles.boldLabel);
            DrawWrappedRow("Package Version", PackageLayout.PackageVersion);
            DrawWrappedRow("Package Root", PackageLayout.PackageRoot);
        }

        private void DrawQuickSetupSection()
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField("Quick Setup", EditorStyles.boldLabel);
                EditorGUILayout.HelpBox("Install only the missing CLI and skill components for the current state.", MessageType.None);
                DrawButtonGrid(
                    new ButtonDefinition(
                        AreRequiredComponentsInstalled() ? "Required Components Installed" : "Install Required Components",
                        () => RunInstallAction(InstallRequiredComponents()),
                        AreRequiredComponentsInstalled() ? SuccessColor : FailureColor)
                );
            }
        }

        private void DrawConnectionSection()
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField("Connection", EditorStyles.boldLabel);
                var snapshot = ConnectionService.GetSnapshot();
                DrawStateRow("Connected", snapshot.connected);
                DrawStateRow("Ready", snapshot.ready);
                DrawWrappedRow("Port", snapshot.port > 0 ? snapshot.port.ToString() : "-");
                DrawWrappedRow("URL", snapshot.url);
                DrawWrappedRow("Project Hash", snapshot.projectHash);
                DrawWrappedRow("Command Count", snapshot.commandCount.ToString());
                DrawWrappedRow("Last Error", snapshot.lastError);
                var autoConnect = EditorGUILayout.Toggle("Auto Connect On Load", snapshot.autoConnectOnLoad);
                if (autoConnect != snapshot.autoConnectOnLoad)
                {
                    ConnectionService.AutoConnectOnLoad = autoConnect;
                }

                DrawButtonGrid(
                    new ButtonDefinition(snapshot.connected ? "Disconnect" : "Connect", () =>
                    {
                        if (snapshot.connected)
                        {
                            ConnectionService.Disconnect();
                            LogAction("Disconnected.");
                        }
                        else
                        {
                            LogAction(ConnectionService.Connect() ? "Connected." : "Connection failed: " + ConnectionService.GetSnapshot().lastError);
                        }

                        RefreshAll();
                    }, snapshot.connected ? SuccessColor : FailureColor)
                );
            }
        }

        private void DrawCliSection()
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField("CLI", EditorStyles.boldLabel);
                DrawColoredRow("Python", _cliStatus.pythonAvailable ? _cliStatus.pythonVersion : "Missing", _cliStatus.pythonAvailable);
                DrawColoredRow("pip", _cliStatus.pipAvailable ? _cliStatus.pipVersion : "Missing", _cliStatus.pipAvailable);
                DrawColoredRow("pipx", _cliStatus.pipxAvailable ? _cliStatus.pipxVersion : "Missing", _cliStatus.pipxAvailable);
                DrawColoredRow("cubix-cli", _cliStatus.cliInstalled ? _cliStatus.cliVersion ?? "Installed" : "Missing", _cliStatus.cliInstalled);
                EditorGUILayout.HelpBox(BuildCliDiagnosticsText(), MessageType.None);

                DrawButtonGrid(
                    new ButtonDefinition(
                        _cliStatus.cliInstalled ? "Uninstall CLI" : "Install CLI",
                        () => RunInstallAction(_cliStatus.cliInstalled ? CliInstallationService.UninstallCli() : CliInstallationService.InstallCli()),
                        _cliStatus.cliInstalled ? SuccessColor : FailureColor),
                    new ButtonDefinition("Copy Diagnostics", () =>
                    {
                        GUIUtility.systemCopyBuffer = BuildDiagnostics();
                        LogAction("Copied diagnostics to the clipboard.");
                    })
                );
            }
        }

        private void DrawSkillsSection()
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField("Skills", EditorStyles.boldLabel);
                DrawSkillStatus("Codex", _codexSkills);
                DrawSkillStatus("Claude Code", _claudeSkills);

                DrawButtonGrid(
                    new ButtonDefinition(
                        IsInstalled(_codexSkills.state) ? "Uninstall Codex" : "Install Codex",
                        () => RunInstallAction(IsInstalled(_codexSkills.state)
                            ? SkillInstallationService.Remove(SkillAgentTarget.Codex)
                            : SkillInstallationService.Install(SkillAgentTarget.Codex)),
                        IsInstalled(_codexSkills.state) ? SuccessColor : FailureColor),
                    new ButtonDefinition(
                        IsInstalled(_claudeSkills.state) ? "Uninstall Claude" : "Install Claude",
                        () => RunInstallAction(IsInstalled(_claudeSkills.state)
                            ? SkillInstallationService.Remove(SkillAgentTarget.ClaudeCode)
                            : SkillInstallationService.Install(SkillAgentTarget.ClaudeCode)),
                        IsInstalled(_claudeSkills.state) ? SuccessColor : FailureColor)
                );
            }
        }

        private void DrawSkillStatus(string label, SkillInstallStatus status)
        {
            DrawColoredRow(label, status.state.ToString(), IsInstalled(status.state));
            DrawWrappedRow(label + " Path", status.rootPath);
            foreach (var skill in status.skills)
            {
                var detail = skill.state + (string.IsNullOrWhiteSpace(skill.installedVersion) ? string.Empty : " (" + skill.installedVersion + ")");
                DrawColoredRow("  " + skill.name, detail, skill.state == SkillInstallState.Installed);
            }
        }

        private void DrawActionLog()
        {
            using (new EditorGUILayout.VerticalScope("box"))
            {
                EditorGUILayout.LabelField("Action Log", EditorStyles.boldLabel);
                EditorGUILayout.TextArea(
                    string.IsNullOrWhiteSpace(_actionLog) ? "No actions yet." : _actionLog,
                    _wrappedTextAreaStyle,
                    GUILayout.MinHeight(180f),
                    GUILayout.ExpandWidth(true));
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

        private void EnsureStyles()
        {
            if (_wrappedLabelStyle == null)
            {
                _wrappedLabelStyle = new GUIStyle(EditorStyles.label)
                {
                    wordWrap = true,
                    richText = false,
                    alignment = TextAnchor.UpperLeft
                };
            }

            if (_wrappedStatusStyle == null)
            {
                _wrappedStatusStyle = new GUIStyle(_wrappedLabelStyle)
                {
                    richText = true
                };
            }

            if (_wrappedTextAreaStyle == null)
            {
                _wrappedTextAreaStyle = new GUIStyle(EditorStyles.textArea)
                {
                    wordWrap = true
                };
            }

            if (_wrappedButtonStyle == null)
            {
                _wrappedButtonStyle = new GUIStyle(GUI.skin.button)
                {
                    wordWrap = true,
                    alignment = TextAnchor.MiddleCenter
                };
            }
        }

        private void DrawStateRow(string label, bool isActive)
        {
            DrawColoredRow(label, isActive ? "Yes" : "No", isActive);
        }

        private void DrawColoredRow(string label, string value, bool isPositive)
        {
            DrawWrappedRow(label, WrapWithColor(NormalizeValue(value), isPositive ? SuccessColor : FailureColor), _wrappedStatusStyle);
        }

        private void DrawWrappedRow(string label, string value)
        {
            DrawWrappedRow(label, NormalizeValue(value), _wrappedLabelStyle);
        }

        private void DrawWrappedRow(string label, string value, GUIStyle valueStyle)
        {
            var content = EditorGUIUtility.TrTextContent(value);
            var valueWidth = Mathf.Max(140f, EditorGUIUtility.currentViewWidth - EditorGUIUtility.labelWidth - 48f);
            var height = Mathf.Max(EditorGUIUtility.singleLineHeight, valueStyle.CalcHeight(content, valueWidth));
            var rowRect = EditorGUILayout.GetControlRect(false, height);
            rowRect = EditorGUI.IndentedRect(rowRect);

            var labelRect = new Rect(rowRect.x, rowRect.y, EditorGUIUtility.labelWidth - 4f, EditorGUIUtility.singleLineHeight);
            var valueRect = new Rect(rowRect.x + EditorGUIUtility.labelWidth, rowRect.y, rowRect.width - EditorGUIUtility.labelWidth, height);

            EditorGUI.LabelField(labelRect, label);
            EditorGUI.LabelField(valueRect, content, valueStyle);
        }

        private void DrawButtonGrid(params ButtonDefinition[] buttons)
        {
            var contentWidth = Mathf.Max(1f, position.width - 40f);
            var columns = Mathf.Clamp(Mathf.FloorToInt(contentWidth / 150f), 1, 3);

            for (var index = 0; index < buttons.Length; index += columns)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    for (var column = 0; column < columns && index + column < buttons.Length; column++)
                    {
                        var button = buttons[index + column];
                        if (DrawButton(button))
                        {
                            button.Action();
                        }
                    }
                }
            }
        }

        private bool DrawButton(ButtonDefinition button)
        {
            var previousColor = GUI.backgroundColor;
            if (button.BackgroundColor.HasValue)
            {
                GUI.backgroundColor = button.BackgroundColor.Value;
            }

            try
            {
                return GUILayout.Button(
                    button.Label,
                    _wrappedButtonStyle,
                    GUILayout.MinHeight(ButtonMinHeight),
                    GUILayout.ExpandWidth(true));
            }
            finally
            {
                GUI.backgroundColor = previousColor;
            }
        }

        private string InstallRequiredComponents()
        {
            var log = new StringBuilder();
            var hasWork = false;

            if (!_cliStatus.cliInstalled)
            {
                AppendInstallResult(log, "CLI", CliInstallationService.InstallCli());
                hasWork = true;
            }

            if (NeedsInstall(_codexSkills))
            {
                AppendInstallResult(log, "Codex Skills", SkillInstallationService.Install(SkillAgentTarget.Codex));
                hasWork = true;
            }

            if (NeedsInstall(_claudeSkills))
            {
                AppendInstallResult(log, "Claude Code Skills", SkillInstallationService.Install(SkillAgentTarget.ClaudeCode));
                hasWork = true;
            }

            return hasWork
                ? log.ToString().Trim()
                : "All required CLI and skill components are already installed.";
        }

        private bool AreRequiredComponentsInstalled()
        {
            return _cliStatus.cliInstalled && IsInstalled(_codexSkills.state) && IsInstalled(_claudeSkills.state);
        }

        private static bool NeedsInstall(SkillInstallStatus status)
        {
            return status.state != SkillInstallState.Installed;
        }

        private static void AppendInstallResult(StringBuilder log, string title, string result)
        {
            if (log.Length > 0)
            {
                log.AppendLine();
                log.AppendLine();
            }

            log.AppendLine(title);
            log.AppendLine(result);
        }

        private static bool IsInstalled(SkillInstallState state)
        {
            return state == SkillInstallState.Installed;
        }

        private static string NormalizeValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }

        private static string WrapWithColor(string value, Color color)
        {
            return "<color=#" + ColorUtility.ToHtmlStringRGB(color) + ">" + EscapeRichText(value) + "</color>";
        }

        private static string EscapeRichText(string value)
        {
            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }
    }
}
