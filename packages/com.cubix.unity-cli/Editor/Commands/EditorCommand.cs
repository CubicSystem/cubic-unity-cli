using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine.SceneManagement;

namespace Cubix.UnityCli
{
    [CubixCliCommand(Group = "editor", Name = "editor", Description = "Control Unity editor state, play mode, menus, and refresh flows.")]
    internal sealed class EditorCommand : ICubixCliCommandHandler, ICubixCliPreflightHandler
    {
        private static readonly MethodInfo MenuItemExistsMethod = typeof(Editor).Assembly
            .GetType("UnityEditor.Menu")
            ?.GetMethod("MenuItemExists", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);

        private static readonly MethodInfo MenuGetEnabledMethod = typeof(Editor).Assembly
            .GetType("UnityEditor.Menu")
            ?.GetMethod("GetEnabled", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);

        public IEnumerable<CommandDefinition> DescribeActions()
        {
            yield return new CommandDefinition
            {
                Action = "state",
                Description = "Read editor state and active scene.",
                Tags = CommandMetadata.Tags(CommandTags.Core, CommandTags.Editor, CommandTags.Diagnostics)
            };
            yield return new CommandDefinition
            {
                Action = "play",
                Description = "Enter play mode.",
                Tags = CommandMetadata.Tags(CommandTags.Core, CommandTags.Editor)
            };
            yield return new CommandDefinition
            {
                Action = "stop",
                Description = "Exit play mode.",
                Tags = CommandMetadata.Tags(CommandTags.Core, CommandTags.Editor)
            };
            yield return new CommandDefinition
            {
                Action = "pause",
                Description = "Pause or resume play mode.",
                Tags = CommandMetadata.Tags(CommandTags.Editor),
                Parameters = CommandMetadata.Parameters(
                    CommandMetadata.Parameter("paused", "boolean", false, "Desired paused state. Toggles when omitted."))
            };
            yield return new CommandDefinition
            {
                Action = "menu",
                Description = "Validate or execute a Unity editor menu item.",
                Tags = CommandMetadata.Tags(CommandTags.Editor, CommandTags.Unsafe),
                SafetyLevel = CommandSafetyLevels.Destructive,
                SupportsPreflight = true,
                Parameters = CommandMetadata.Parameters(
                    CommandMetadata.Parameter("menuPath", "string", true, "Exact Unity menu path."),
                    CommandMetadata.Parameter("validateOnly", "boolean", false, "Only validate menu availability without executing.", false))
            };
            yield return new CommandDefinition
            {
                Action = "refresh",
                Description = "Refresh assets and optionally request script compilation.",
                Tags = CommandMetadata.Tags(CommandTags.Editor, CommandTags.Assets, CommandTags.Scripts),
                Parameters = CommandMetadata.Parameters(
                    CommandMetadata.Parameter("mode", "string", false, "Refresh scope.", "scripts", "assets", "scripts", "all"))
            };
        }

        public object Execute(string action, JObject parameters)
        {
            switch (action)
            {
                case "state":
                    return new CommandSuccessResponse("Editor state.", BuildState());
                case "play":
                    return RequestPlayModeTransition(true);
                case "stop":
                    return RequestPlayModeTransition(false);
                case "pause":
                    var paused = parameters.Value<bool?>("paused") ?? !EditorApplication.isPaused;
                    EditorApplication.isPaused = paused;
                    return new CommandSuccessResponse(paused ? "Paused play mode." : "Resumed play mode.", BuildState());
                case "menu":
                    return ExecuteMenu(parameters);
                case "refresh":
                    return Refresh(parameters);
                default:
                    return new CommandErrorResponse("Unsupported editor action '" + action + "'.");
            }
        }

        public CommandPreflightResult Preflight(string action, JObject parameters)
        {
            switch (action)
            {
                case "menu":
                    return PreflightMenu(parameters);
                default:
                    return CommandMetadata.Success("editor." + action, "No preflight issues.");
            }
        }

        private static object BuildState()
        {
            var scene = SceneManager.GetActiveScene();
            return new
            {
                isPlaying = EditorApplication.isPlaying,
                isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
                playModeTransitionPending = EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode,
                isPaused = EditorApplication.isPaused,
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating,
                activeScene = new
                {
                    name = scene.name,
                    path = scene.path,
                    isLoaded = scene.isLoaded
                }
            };
        }

        private static object RequestPlayModeTransition(bool desiredIsPlaying)
        {
            if (EditorApplication.isPlaying == desiredIsPlaying)
            {
                if (!desiredIsPlaying)
                {
                    EditorApplication.isPaused = false;
                }

                return new CommandSuccessResponse(
                    desiredIsPlaying ? "Editor is already in play mode." : "Editor is already stopped.",
                    BuildTransitionState(desiredIsPlaying, false));
            }

            EditorApplication.delayCall += () =>
            {
                EditorApplication.isPaused = false;
                EditorApplication.isPlaying = desiredIsPlaying;
            };

            return new CommandSuccessResponse(
                desiredIsPlaying ? "Starting play mode." : "Stopping play mode.",
                BuildTransitionState(desiredIsPlaying, true));
        }

        private static object BuildTransitionState(bool requestedIsPlaying, bool transitionPending)
        {
            var scene = SceneManager.GetActiveScene();
            return new
            {
                isPlaying = EditorApplication.isPlaying,
                isPlayingOrWillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
                playModeTransitionPending = EditorApplication.isPlaying != EditorApplication.isPlayingOrWillChangePlaymode,
                isPaused = EditorApplication.isPaused,
                isCompiling = EditorApplication.isCompiling,
                isUpdating = EditorApplication.isUpdating,
                requestedIsPlaying,
                transitionPending,
                activeScene = new
                {
                    name = scene.name,
                    path = scene.path,
                    isLoaded = scene.isLoaded
                }
            };
        }

        private static object ExecuteMenu(JObject parameters)
        {
            var validation = ValidateMenu(parameters);
            if (!validation.canExecute)
            {
                return new CommandErrorResponse("Menu validation failed.", validation);
            }

            if (parameters.Value<bool?>("validateOnly") ?? false)
            {
                return new CommandSuccessResponse("Menu validation.", validation);
            }

            var menuPath = parameters.Value<string>("menuPath");
            var executed = EditorApplication.ExecuteMenuItem(menuPath);
            if (!executed)
            {
                return new CommandErrorResponse("Menu execution failed.", validation);
            }

            return new CommandSuccessResponse("Menu executed.", new
            {
                menuPath,
                executed = true,
                editor = BuildState()
            });
        }

        private static object Refresh(JObject parameters)
        {
            var mode = (parameters.Value<string>("mode") ?? "scripts").ToLowerInvariant();
            switch (mode)
            {
                case "assets":
                    AssetDatabase.Refresh();
                    break;
                case "scripts":
                    AssetDatabase.Refresh();
                    CompilationPipeline.RequestScriptCompilation();
                    break;
                case "all":
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                    CompilationPipeline.RequestScriptCompilation();
                    break;
                default:
                    return new CommandErrorResponse("Unsupported refresh mode '" + mode + "'.");
            }

            return new CommandSuccessResponse("Editor refresh completed.", new
            {
                mode,
                editor = BuildState()
            });
        }

        private static CommandPreflightResult PreflightMenu(JObject parameters)
        {
            return ValidateMenu(parameters);
        }

        private static CommandPreflightResult ValidateMenu(JObject parameters)
        {
            var menuPath = parameters.Value<string>("menuPath");
            if (string.IsNullOrWhiteSpace(menuPath))
            {
                return CommandMetadata.Result("editor.menu", CommandMetadata.Issue("error", "A menuPath is required.", "menu_path_required"));
            }

            var exists = InvokeMenuReflection(MenuItemExistsMethod, menuPath, defaultValue: false);
            var enabled = InvokeMenuReflection(MenuGetEnabledMethod, menuPath, defaultValue: exists);
            var issues = new List<CommandPreflightIssue>();
            if (!exists)
            {
                issues.Add(CommandMetadata.Issue("error", "Menu item '" + menuPath + "' was not found.", "menu_not_found"));
            }
            else if (!enabled)
            {
                issues.Add(CommandMetadata.Issue("error", "Menu item '" + menuPath + "' is currently disabled.", "menu_disabled"));
            }

            var result = CommandMetadata.Result("editor.menu", issues.ToArray());
            result.summary = issues.Count == 0 ? "Menu is available." : result.summary;
            result.issues.Insert(0, CommandMetadata.Issue("info", "Menu validation does not execute the menu item.", "validation_only"));
            return result;
        }

        private static bool InvokeMenuReflection(MethodInfo method, string menuPath, bool defaultValue)
        {
            if (method == null)
            {
                return defaultValue;
            }

            try
            {
                return (bool)method.Invoke(null, new object[] { menuPath });
            }
            catch
            {
                return defaultValue;
            }
        }
    }
}
