using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace CubicEngine.UnityCli
{
    [CubicCliCommand(Group = "commands", Name = "commands", Description = "Discover, describe, preflight, and batch Cubic commands.")]
    internal sealed class CommandsCommand : ICubicCliCommandHandler
    {
        public IEnumerable<CommandDefinition> DescribeActions()
        {
            yield return new CommandDefinition
            {
                Action = "list",
                Description = "List available commands with metadata filtering.",
                Tags = CommandMetadata.Tags(CommandTags.Core, CommandTags.Diagnostics),
                Parameters = CommandMetadata.Parameters(
                    CommandMetadata.Parameter("group", "string", false, "Optional command group filter."),
                    CommandMetadata.Parameter("tag", "string", false, "Optional tag filter."),
                    CommandMetadata.Parameter("search", "string", false, "Text search filter."),
                    CommandMetadata.Parameter("includeUnsafe", "boolean", false, "Include commands tagged unsafe.", false))
            };
            yield return new CommandDefinition
            {
                Action = "describe",
                Description = "Describe one command in detail.",
                Tags = CommandMetadata.Tags(CommandTags.Core, CommandTags.Diagnostics),
                Parameters = CommandMetadata.Parameters(
                    CommandMetadata.Parameter("command", "string", true, "Full command name such as scene.hierarchy."))
            };
            yield return new CommandDefinition
            {
                Action = "preflight",
                Description = "Run preflight checks for one or more commands.",
                Tags = CommandMetadata.Tags(CommandTags.Diagnostics, CommandTags.Unsafe),
                SafetyLevel = CommandSafetyLevels.Destructive,
                SupportsBatch = false,
                Parameters = CommandMetadata.Parameters(
                    CommandMetadata.Parameter("calls", "array", true, "Array of { command, params?, requestId? } objects."))
            };
            yield return new CommandDefinition
            {
                Action = "batch",
                Description = "Execute multiple commands sequentially.",
                Tags = CommandMetadata.Tags(CommandTags.Diagnostics, CommandTags.Unsafe),
                SafetyLevel = CommandSafetyLevels.Destructive,
                SupportsBatch = false,
                Parameters = CommandMetadata.Parameters(
                    CommandMetadata.Parameter("calls", "array", true, "Array of { command, params?, requestId? } objects."),
                    CommandMetadata.Parameter("stopOnError", "boolean", false, "Stop after the first failed call.", true))
            };
        }

        public object Execute(string action, JObject parameters)
        {
            switch (action)
            {
                case "list":
                    return ListCommands(parameters);
                case "describe":
                    return DescribeCommand(parameters);
                case "preflight":
                    return Preflight(parameters);
                case "batch":
                    return Batch(parameters);
                default:
                    return new CommandErrorResponse("Unsupported commands action '" + action + "'.");
            }
        }

        private static object ListCommands(JObject parameters)
        {
            var definitions = CommandRouter.ListCommands(
                parameters.Value<string>("group"),
                parameters.Value<string>("tag"),
                parameters.Value<string>("search"),
                parameters.Value<bool?>("includeUnsafe") ?? false);

            return new CommandSuccessResponse("Command list.", new
            {
                count = definitions.Count,
                commands = definitions
            });
        }

        private static object DescribeCommand(JObject parameters)
        {
            var command = parameters.Value<string>("command");
            if (string.IsNullOrWhiteSpace(command))
            {
                return new CommandErrorResponse("A command name is required.");
            }

            var definition = CommandRouter.DescribeCommand(command);
            if (definition == null)
            {
                return new CommandErrorResponse("Could not find command '" + command + "'.");
            }

            return new CommandSuccessResponse("Command description.", definition);
        }

        private static object Preflight(JObject parameters)
        {
            var calls = ParseCalls(parameters);
            if (calls == null)
            {
                return new CommandErrorResponse("A calls array is required.");
            }

            var results = calls
                .Select(call => CommandRouter.Preflight(call))
                .ToList();

            return new CommandSuccessResponse("Preflight results.", new
            {
                count = results.Count,
                canExecute = results.All(result => result.canExecute),
                results
            });
        }

        private static object Batch(JObject parameters)
        {
            var calls = ParseCalls(parameters);
            if (calls == null)
            {
                return new CommandErrorResponse("A calls array is required.");
            }

            var stopOnError = parameters.Value<bool?>("stopOnError") ?? true;
            var results = new List<object>();
            var halted = false;

            foreach (var call in calls)
            {
                if (string.Equals(call.command, "commands.batch", System.StringComparison.OrdinalIgnoreCase))
                {
                    var nestedError = new CommandErrorResponse("Nested commands.batch calls are not supported.");
                    results.Add(BuildBatchResult(call, nestedError));
                    halted = stopOnError;
                    if (halted)
                    {
                        break;
                    }

                    continue;
                }

                var response = CommandRouter.Route(call);
                var result = BuildBatchResult(call, response);
                results.Add(result);

                var failed = response is CommandErrorResponse;
                if (failed && stopOnError)
                {
                    halted = true;
                    break;
                }
            }

            return new CommandSuccessResponse("Batch executed.", new
            {
                count = results.Count,
                requested = calls.Count,
                stopOnError,
                halted,
                results
            });
        }

        private static List<CommandRequest> ParseCalls(JObject parameters)
        {
            var callsArray = parameters["calls"] as JArray;
            if (callsArray == null)
            {
                return null;
            }

            var calls = new List<CommandRequest>();
            foreach (var token in callsArray)
            {
                if (!(token is JObject callObject))
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(callObject.Value<string>("command")))
                {
                    return null;
                }

                calls.Add(new CommandRequest
                {
                    command = callObject.Value<string>("command"),
                    @params = callObject["params"] as JObject,
                    requestId = callObject.Value<string>("requestId")
                });
            }

            return calls;
        }

        private static object BuildBatchResult(CommandRequest call, object response)
        {
            var payload = JObject.FromObject(response);
            return new
            {
                command = call.command,
                requestId = call.requestId,
                success = payload.Value<bool?>("success") ?? false,
                message = payload.Value<string>("message"),
                data = payload["data"],
                errors = payload["errors"]
            };
        }
    }
}
