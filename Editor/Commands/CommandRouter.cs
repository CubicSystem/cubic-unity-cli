using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace CubicEngine.UnityCli
{
    internal interface ICubicCliCommandHandler
    {
        object Execute(string action, JObject parameters);
        IEnumerable<CommandDefinition> DescribeActions();
    }

    internal sealed class CommandRequest
    {
        public string command;
        public JObject @params;
        public string requestId;
    }

    internal static class CommandRouter
    {
        private sealed class HandlerRegistration
        {
            public CubicCliCommandAttribute Attribute { get; set; }
            public ICubicCliCommandHandler Handler { get; set; }
        }

        private static readonly Dictionary<string, HandlerRegistration> Handlers;

        static CommandRouter()
        {
            Handlers = new Dictionary<string, HandlerRegistration>(StringComparer.OrdinalIgnoreCase);

            var handlerTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(assembly =>
                {
                    try
                    {
                        return assembly.GetTypes();
                    }
                    catch
                    {
                        return Array.Empty<Type>();
                    }
                })
                .Where(type => !type.IsAbstract && typeof(ICubicCliCommandHandler).IsAssignableFrom(type));

            foreach (var handlerType in handlerTypes)
            {
                var attribute = (CubicCliCommandAttribute)Attribute.GetCustomAttribute(handlerType, typeof(CubicCliCommandAttribute));
                if (attribute == null || string.IsNullOrWhiteSpace(attribute.Group))
                {
                    continue;
                }

                if (!(Activator.CreateInstance(handlerType) is ICubicCliCommandHandler handler))
                {
                    continue;
                }

                Handlers[attribute.Group] = new HandlerRegistration
                {
                    Attribute = attribute,
                    Handler = handler
                };
            }
        }

        public static object Route(CommandRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.command))
            {
                return new CommandErrorResponse("A command name is required.");
            }

            var parts = request.command.Split(new[] { '.' }, 2, StringSplitOptions.RemoveEmptyEntries);
            var group = parts[0];
            var action = parts.Length > 1 ? parts[1] : "run";

            if (!TryGetHandler(group, out var handler))
            {
                return new CommandErrorResponse("Unknown command group '" + group + "'.", errors: new[] { request.command });
            }

            try
            {
                var result = handler.Handler.Execute(action, request.@params ?? new JObject());
                return result ?? new CommandSuccessResponse("OK");
            }
            catch (Exception exception)
            {
                return new CommandErrorResponse(
                    "Command '" + request.command + "' failed.",
                    errors: new[]
                    {
                        new
                        {
                            type = exception.GetType().FullName,
                            message = exception.Message,
                            stackTrace = exception.StackTrace
                        }
                    });
            }
        }

        public static IReadOnlyList<CommandDefinition> ListCommands(
            string group = null,
            string tag = null,
            string search = null,
            bool includeUnsafe = false)
        {
            var definitions = new List<CommandDefinition>();
            foreach (var pair in Handlers)
            {
                foreach (var definition in pair.Value.Handler.DescribeActions())
                {
                    definitions.Add(NormalizeDefinition(pair.Key, definition));
                }
            }

            var filtered = definitions.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(group))
            {
                filtered = filtered.Where(definition => string.Equals(definition.Group, group, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(tag))
            {
                filtered = filtered.Where(definition => definition.Tags.Any(existingTag => string.Equals(existingTag, tag, StringComparison.OrdinalIgnoreCase)));
            }

            if (!includeUnsafe)
            {
                filtered = filtered.Where(definition => !definition.Tags.Any(existingTag => string.Equals(existingTag, CommandTags.Unsafe, StringComparison.OrdinalIgnoreCase)));
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                filtered = filtered.Where(definition =>
                    (!string.IsNullOrWhiteSpace(definition.FullName) && definition.FullName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrWhiteSpace(definition.Description) && definition.Description.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            return filtered
                .OrderBy(definition => definition.Group, StringComparer.OrdinalIgnoreCase)
                .ThenBy(definition => definition.Action, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static CommandDefinition DescribeCommand(string command)
        {
            if (!TryParseCommand(command, out var group, out var action))
            {
                return null;
            }

            if (!TryGetHandler(group, out var registration))
            {
                return null;
            }

            return registration.Handler.DescribeActions()
                .Select(definition => NormalizeDefinition(group, definition))
                .FirstOrDefault(definition => string.Equals(definition.Action, action, StringComparison.OrdinalIgnoreCase));
        }

        public static CommandPreflightResult Preflight(CommandRequest request)
        {
            if (!TryParseCommand(request?.command, out var group, out var action))
            {
                return CommandMetadata.Result(request?.command ?? string.Empty, CommandMetadata.Issue("error", "A valid command name is required.", "invalid_command"));
            }

            if (!TryGetHandler(group, out var registration))
            {
                return CommandMetadata.Result(request.command, CommandMetadata.Issue("error", "Unknown command group '" + group + "'.", "unknown_group"));
            }

            if (!(registration.Handler is ICubicCliPreflightHandler preflightHandler))
            {
                return CommandMetadata.Success(request.command, "No preflight issues.");
            }

            var result = preflightHandler.Preflight(action, request.@params ?? new JObject()) ?? CommandMetadata.Success(request.command, "No preflight issues.");
            result.command = request.command;
            result.issues = result.issues ?? new List<CommandPreflightIssue>();
            result.canExecute = result.issues.All(issue => !string.Equals(issue.severity, "error", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(result.summary))
            {
                result.summary = result.issues.Count == 0 ? "No preflight issues." : string.Join(" ", result.issues.Select(issue => issue.message));
            }

            return result;
        }

        private static bool TryGetHandler(string group, out HandlerRegistration registration)
        {
            return Handlers.TryGetValue(group, out registration);
        }

        private static bool TryParseCommand(string command, out string group, out string action)
        {
            group = null;
            action = null;
            if (string.IsNullOrWhiteSpace(command))
            {
                return false;
            }

            var parts = command.Split(new[] { '.' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return false;
            }

            group = parts[0];
            action = parts.Length > 1 ? parts[1] : "run";
            return true;
        }

        private static CommandDefinition NormalizeDefinition(string group, CommandDefinition definition)
        {
            definition.Group = group;
            definition.Action = string.IsNullOrWhiteSpace(definition.Action) ? "run" : definition.Action;
            definition.FullName = string.IsNullOrWhiteSpace(definition.FullName) ? group + "." + definition.Action : definition.FullName;
            definition.Tags = definition.Tags ?? new List<string>();
            definition.Parameters = definition.Parameters ?? new List<CommandParameterDefinition>();
            definition.SafetyLevel = string.IsNullOrWhiteSpace(definition.SafetyLevel) ? CommandSafetyLevels.Safe : definition.SafetyLevel;
            return definition;
        }
    }
}
