using System.Text;
using UnityEditor;
using UnityEngine;

namespace Cubix.UnityCli
{
    internal sealed class UnityCliSetupWindow : EditorWindow
    {
        private static readonly Color SuccessColor = new Color(0.28f, 0.68f, 0.34f);
        private static readonly Color FailureColor = new Color(0.82f, 0.34f, 0.34f);
        private static readonly Color SeparatorColor = new Color(0.35f, 0.35f, 0.35f, 0.35f);
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
        private GUIStyle _sectionBoxStyle;
        private GUIStyle _sectionTitleStyle;
        private GUIStyle _sectionBodyBoxStyle;

        private sealed class ButtonDefinition
        {
            public readonly string Label;
            public readonly System.Action Action;
            public readonly Color? BackgroundColor;
            public readonly bool Enabled;

            public ButtonDefinition(string label, System.Action action, Color? backgroundColor = null, bool enabled = true)
            {
                Label = label;
                Action = action;
                BackgroundColor = backgroundColor;
                Enabled = enabled;
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
                EditorGUILayout.Space(10f);
                DrawConnectionSection();
                EditorGUILayout.Space(10f);
                DrawCliSection();
                EditorGUILayout.Space(10f);
                DrawSkillsSection();
                EditorGUILayout.Space(10f);
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
            using (new EditorGUILayout.VerticalScope(_sectionBoxStyle))
            {
                EditorGUILayout.LabelField("Cubix Unity CLI", _sectionTitleStyle);
                DrawSectionDivider();
                EditorGUILayout.Space(4f);
                DrawWrappedRow("Loaded Package", PackageLayout.PackageVersion);
                DrawWrappedRow("Loaded Root", PackageLayout.PackageRoot);
                if (!string.IsNullOrWhiteSpace(PackageReloadService.StatusMessage) &&
                    (PackageReloadService.HasPendingReload || PackageLayout.HasLoadedPackageDrift))
                {
                    DrawWrappedRow("Reload Status", PackageReloadService.StatusMessage);
                }

                if (PackageLayout.HasLoadedPackageDrift)
                {
                    EditorGUILayout.HelpBox(
                        "The loaded Cubix Unity CLI package version does not match the resolved project package version. Use reload only as a fallback; if it still stays stale afterward, restart Unity.",
                        MessageType.Warning);
                }

                EditorGUILayout.Space(6f);
                DrawButtonGrid(
                    new ButtonDefinition(
                        PackageReloadService.HasPendingReload ? "Reload Pending" : "Reload Package Scripts",
                        RunPackageReloadAction,
                        null,
                        !PackageReloadService.HasPendingReload));
            }
        }

        private void DrawConnectionSection()
        {
            DrawSection("Connection", () =>
            {
                var snapshot = ConnectionService.GetSnapshot();
                DrawStateRow("Connected", snapshot.connected);
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
                    new ButtonDefinition("Connect", () =>
                    {
                        LogAction(ConnectionService.Connect() ? "Connected." : "Connection failed: " + ConnectionService.GetSnapshot().lastError);
                        RefreshAll();
                    }, snapshot.connected ? SuccessColor : FailureColor, !snapshot.connected),
                    new ButtonDefinition("Disconnect", () =>
                    {
                        ConnectionService.Disconnect();
                        LogAction("Disconnected.");
                        RefreshAll();
                    }, null, snapshot.connected)
                );
            });
        }

        private void DrawCliSection()
        {
            DrawSection("CLI", () =>
            {
                var cliNeedsAttention = CliNeedsAttention();
                DrawColoredRow("Python", _cliStatus.pythonAvailable ? _cliStatus.pythonVersion : "Missing", _cliStatus.pythonAvailable);
                DrawColoredRow("pip", _cliStatus.pipAvailable ? _cliStatus.pipVersion : "Missing", _cliStatus.pipAvailable);
                DrawColoredRow("pipx", _cliStatus.pipxAvailable ? _cliStatus.pipxVersion : "Missing", _cliStatus.pipxAvailable);
                DrawWrappedRow("Expected CLI", _cliStatus.expectedCliVersion);
                DrawColoredRow("cubix-cli Package", BuildCliPackageLabel(), _cliStatus.cliInstalled && _cliStatus.cliVersionMatches);
                DrawColoredRow("cubix-cli Command", BuildCliCommandLabel(), _cliStatus.cliCommandAvailable && _cliStatus.cliCommandVersionMatches);
                DrawStateRow("Top-level Test", _cliStatus.cliSupportsTestTopLevel);
                if (_cliStatus.cliInstalled && (!_cliStatus.cliVersionMatches || !_cliStatus.cliCommandVersionMatches))
                {
                    EditorGUILayout.HelpBox("Installed cubix-cli version does not match the expected CLI version. Reinstall CLI.", MessageType.Warning);
                }

                DrawButtonGrid(
                    new ButtonDefinition(
                        GetCliPrimaryActionLabel(),
                        RunCliPrimaryAction,
                        _cliStatus.cliInstalled && !cliNeedsAttention ? SuccessColor : FailureColor),
                    new ButtonDefinition("Copy Diagnostics", () =>
                    {
                        GUIUtility.systemCopyBuffer = BuildDiagnostics();
                        LogAction("Copied diagnostics to the clipboard.");
                    })
                );
            });
        }

        private void DrawSkillsSection()
        {
            DrawSection("Skills", () =>
            {
                DrawSkillGroup(
                    "Codex",
                    _codexSkills,
                    new ButtonDefinition(
                        GetSkillActionLabel("Codex", _codexSkills.state),
                        () => RunInstallAction(IsInstalled(_codexSkills.state)
                            ? SkillInstallationService.Remove(SkillAgentTarget.Codex)
                            : SkillInstallationService.Install(SkillAgentTarget.Codex)),
                        IsInstalled(_codexSkills.state) ? SuccessColor : FailureColor,
                        IsInstalled(_codexSkills.state) || _codexSkills.agentAvailable));

                EditorGUILayout.Space(8f);

                DrawSkillGroup(
                    "Claude Code",
                    _claudeSkills,
                    new ButtonDefinition(
                        GetSkillActionLabel("Claude Code", _claudeSkills.state),
                        () => RunInstallAction(IsInstalled(_claudeSkills.state)
                            ? SkillInstallationService.Remove(SkillAgentTarget.ClaudeCode)
                            : SkillInstallationService.Install(SkillAgentTarget.ClaudeCode)),
                        IsInstalled(_claudeSkills.state) ? SuccessColor : FailureColor,
                        IsInstalled(_claudeSkills.state) || _claudeSkills.agentAvailable));
            });
        }

        private void DrawSkillGroup(string label, SkillInstallStatus status, ButtonDefinition actionButton)
        {
            using (new EditorGUILayout.VerticalScope(_sectionBodyBoxStyle))
            {
                EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
                DrawSectionDivider();
                EditorGUILayout.Space(3f);
                DrawColoredRow("Status", status.state.ToString(), IsInstalled(status.state));
                DrawColoredRow("App", status.agentAvailable ? "Detected" : "Not Detected", status.agentAvailable);
                DrawWrappedRow("App Path", status.agentLocation);
                DrawWrappedRow("Skills Path", status.rootPath);
                foreach (var skill in status.skills)
                {
                    var detail = skill.state + (string.IsNullOrWhiteSpace(skill.installedVersion) ? string.Empty : " (" + skill.installedVersion + ")");
                    DrawColoredRow("  " + skill.name, detail, skill.state == SkillInstallState.Installed);
                }

                EditorGUILayout.Space(6f);
                DrawButtonGrid(actionButton);
            }
        }

        private void DrawActionLog()
        {
            DrawSection("Action Log", () =>
            {
                EditorGUILayout.TextArea(
                    string.IsNullOrWhiteSpace(_actionLog) ? "No actions yet." : _actionLog,
                    _wrappedTextAreaStyle,
                    GUILayout.MinHeight(180f),
                    GUILayout.ExpandWidth(true));
            });
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

        private void RunPackageReloadAction()
        {
            PackageReloadService.RequestReload("Queued a Cubix Unity CLI package reload from the setup window.");
            LogAction(PackageReloadService.StatusMessage);
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
            builder.AppendLine("Package");
            builder.AppendLine("Loaded Version: " + PackageLayout.PackageVersion);
            builder.AppendLine("Loaded Root: " + PackageLayout.PackageRoot);
            builder.AppendLine("Project Version: " + PackageLayout.ProjectPackageVersion);
            builder.AppendLine("Lock Version: " + PackageLayout.ProjectLockPackageVersion);
            builder.AppendLine("Manifest Spec: " + PackageLayout.ProjectManifestDependencySpec);
            builder.AppendLine("Project Root: " + PackageLayout.ProjectPackageRoot);
            builder.AppendLine("Loaded Drift: " + PackageLayout.HasLoadedPackageDrift);
            builder.AppendLine("Reload Pending: " + PackageReloadService.HasPendingReload);
            builder.AppendLine("Reload Status: " + PackageReloadService.StatusMessage);
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
            builder.AppendLine("- top-level tests: cubix-cli test run / cubix-cli test status");
            builder.AppendLine("- local command count: " + CommandRouter.ListCommands(includeUnsafe: true).Count);
            return builder.ToString().Trim();
        }

        private string GetCliPrimaryActionLabel()
        {
            if (!_cliStatus.cliInstalled)
            {
                return "Install CLI";
            }

            return CliNeedsAttention() ? "Reinstall CLI" : "Uninstall CLI";
        }

        private void RunCliPrimaryAction()
        {
            if (!_cliStatus.cliInstalled)
            {
                RunInstallAction(CliInstallationService.InstallCli());
                return;
            }

            if (CliNeedsAttention())
            {
                RunInstallAction(CliInstallationService.UpdateCli());
                return;
            }

            RunInstallAction(CliInstallationService.UninstallCli());
        }

        private bool CliNeedsAttention()
        {
            return _cliStatus.cliInstalled &&
                   (!_cliStatus.cliVersionMatches ||
                    !_cliStatus.cliCommandAvailable ||
                    !_cliStatus.cliCommandVersionMatches ||
                    !_cliStatus.cliSupportsTestTopLevel);
        }

        private string BuildCliPackageLabel()
        {
            if (!_cliStatus.cliInstalled)
            {
                return "Missing";
            }

            var version = string.IsNullOrWhiteSpace(_cliStatus.cliVersion) ? "Installed" : _cliStatus.cliVersion;
            if (_cliStatus.cliVersionMatches)
            {
                return version;
            }

            return version + " (expected " + (_cliStatus.expectedCliVersion ?? "-") + ")";
        }

        private string BuildCliCommandLabel()
        {
            if (!_cliStatus.cliCommandAvailable)
            {
                return "Missing";
            }

            var version = string.IsNullOrWhiteSpace(_cliStatus.cliCommandVersion) ? "Available" : _cliStatus.cliCommandVersion;
            if (_cliStatus.cliCommandVersionMatches)
            {
                return version;
            }

            return version + " (expected " + (_cliStatus.expectedCliVersion ?? "-") + ")";
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

            if (_sectionBoxStyle == null)
            {
                _sectionBoxStyle = new GUIStyle(EditorStyles.helpBox)
                {
                    padding = new RectOffset(12, 12, 10, 12),
                    margin = new RectOffset(0, 0, 0, 0)
                };
            }

            if (_sectionTitleStyle == null)
            {
                _sectionTitleStyle = new GUIStyle(EditorStyles.boldLabel)
                {
                    fontSize = 12
                };
            }

            if (_sectionBodyBoxStyle == null)
            {
                _sectionBodyBoxStyle = new GUIStyle(EditorStyles.helpBox)
                {
                    padding = new RectOffset(10, 10, 8, 10),
                    margin = new RectOffset(0, 0, 0, 0)
                };
            }
        }

        private void DrawSection(string title, System.Action content)
        {
            using (new EditorGUILayout.VerticalScope(_sectionBoxStyle))
            {
                EditorGUILayout.LabelField(title, _sectionTitleStyle);
                DrawSectionDivider();
                EditorGUILayout.Space(4f);
                content();
            }
        }

        private void DrawSectionDivider()
        {
            var rect = EditorGUILayout.GetControlRect(false, 1f);
            EditorGUI.DrawRect(rect, SeparatorColor);
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
            using (new EditorGUI.DisabledScope(!button.Enabled))
            {
                var previousColor = GUI.backgroundColor;
                if (button.Enabled && button.BackgroundColor.HasValue)
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
        }

        private static bool IsInstalled(SkillInstallState state)
        {
            return state == SkillInstallState.Installed;
        }

        private static string GetSkillActionLabel(string agentName, SkillInstallState state)
        {
            if (state == SkillInstallState.Installed)
            {
                return "Uninstall " + agentName + " Skills";
            }

            if (state == SkillInstallState.Outdated)
            {
                return "Reinstall " + agentName + " Skills";
            }

            return "Install " + agentName + " Skills";
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
