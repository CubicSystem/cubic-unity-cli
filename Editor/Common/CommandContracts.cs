using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CubicEngine.UnityCli
{
    internal static class CommandSafetyLevels
    {
        public const string Safe = "safe";
        public const string Destructive = "destructive";
        public const string ArbitraryCode = "arbitrary_code";
    }

    internal static class CommandTags
    {
        public const string Core = "core";
        public const string Editor = "editor";
        public const string Scene = "scene";
        public const string Object = "object";
        public const string Prefab = "prefab";
        public const string Runtime = "runtime";
        public const string Console = "console";
        public const string Assets = "assets";
        public const string Scripts = "scripts";
        public const string Tests = "tests";
        public const string Diagnostics = "diagnostics";
        public const string Unsafe = "unsafe";
        public const string Experimental = "experimental";
    }

    [Serializable]
    internal sealed class CommandParameterDefinition
    {
        [JsonProperty("name")]
        public string name;
        [JsonProperty("type")]
        public string type;
        [JsonProperty("required")]
        public bool required;
        [JsonProperty("description")]
        public string description;
        [JsonProperty("allowedValues")]
        public List<string> allowedValues = new List<string>();
        [JsonProperty("defaultValue")]
        public object defaultValue;
    }

    [Serializable]
    internal sealed class CommandDefinition
    {
        [JsonProperty("group")]
        public string Group { get; set; }
        [JsonProperty("action")]
        public string Action { get; set; }
        [JsonProperty("fullName")]
        public string FullName { get; set; }
        [JsonProperty("description")]
        public string Description { get; set; }
        [JsonProperty("tags")]
        public List<string> Tags { get; set; } = new List<string>();
        [JsonProperty("safetyLevel")]
        public string SafetyLevel { get; set; } = CommandSafetyLevels.Safe;
        [JsonProperty("supportsPreflight")]
        public bool SupportsPreflight { get; set; }
        [JsonProperty("supportsBatch")]
        public bool SupportsBatch { get; set; } = true;
        [JsonProperty("parameters")]
        public List<CommandParameterDefinition> Parameters { get; set; } = new List<CommandParameterDefinition>();
    }

    [Serializable]
    internal sealed class CommandPreflightIssue
    {
        [JsonProperty("severity")]
        public string severity;
        [JsonProperty("code")]
        public string code;
        [JsonProperty("message")]
        public string message;
    }

    [Serializable]
    internal sealed class CommandPreflightResult
    {
        [JsonProperty("command")]
        public string command;
        [JsonProperty("canExecute")]
        public bool canExecute;
        [JsonProperty("summary")]
        public string summary;
        [JsonProperty("issues")]
        public List<CommandPreflightIssue> issues = new List<CommandPreflightIssue>();
    }

    internal interface ICubicCliPreflightHandler
    {
        CommandPreflightResult Preflight(string action, JObject parameters);
    }

    internal static class CommandMetadata
    {
        public static List<string> Tags(params string[] tags)
        {
            return tags?
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
        }

        public static List<CommandParameterDefinition> Parameters(params CommandParameterDefinition[] parameters)
        {
            return parameters?.ToList() ?? new List<CommandParameterDefinition>();
        }

        public static CommandParameterDefinition Parameter(
            string name,
            string type,
            bool required,
            string description,
            object defaultValue = null,
            params string[] allowedValues)
        {
            return new CommandParameterDefinition
            {
                name = name,
                type = type,
                required = required,
                description = description,
                defaultValue = defaultValue,
                allowedValues = allowedValues?
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToList() ?? new List<string>()
            };
        }

        public static CommandPreflightIssue Issue(string severity, string message, string code = null)
        {
            return new CommandPreflightIssue
            {
                severity = severity,
                code = code,
                message = message
            };
        }

        public static CommandPreflightResult Success(string command, string summary)
        {
            return new CommandPreflightResult
            {
                command = command,
                canExecute = true,
                summary = summary
            };
        }

        public static CommandPreflightResult Result(string command, params CommandPreflightIssue[] issues)
        {
            var allIssues = issues?.Where(issue => issue != null).ToList() ?? new List<CommandPreflightIssue>();
            return new CommandPreflightResult
            {
                command = command,
                canExecute = allIssues.All(issue => !string.Equals(issue.severity, "error", StringComparison.OrdinalIgnoreCase)),
                summary = allIssues.Count == 0 ? "No preflight issues." : string.Join(" ", allIssues.Select(issue => issue.message)),
                issues = allIssues
            };
        }
    }
}
