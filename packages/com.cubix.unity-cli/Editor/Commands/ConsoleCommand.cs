using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Cubix.UnityCli
{
    [CubixCliCommand(Group = "console", Name = "console", Description = "Read or clear Unity console entries.")]
    internal sealed class ConsoleCommand : ICubixCliCommandHandler
    {
        public IEnumerable<CommandDefinition> DescribeActions()
        {
            yield return new CommandDefinition
            {
                Action = "read",
                Description = "Read recent console entries or compiler errors.",
                Tags = CommandMetadata.Tags(CommandTags.Core, CommandTags.Console, CommandTags.Diagnostics),
                Parameters = CommandMetadata.Parameters(
                    CommandMetadata.Parameter("level", "string", false, "Optional severity filter.", null, "log", "warning", "error"),
                    CommandMetadata.Parameter("limit", "integer", false, "Maximum number of entries to return.", 50),
                    CommandMetadata.Parameter("source", "string", false, "Source log buffer to query.", null, "editor", "compiler"))
            };
            yield return new CommandDefinition
            {
                Action = "clear",
                Description = "Clear editor and compiler console history.",
                Tags = CommandMetadata.Tags(CommandTags.Console, CommandTags.Diagnostics, CommandTags.Unsafe),
                SafetyLevel = CommandSafetyLevels.Destructive
            };
        }

        public object Execute(string action, JObject parameters)
        {
            switch (action)
            {
                case "read":
                    var level = parameters.Value<string>("level");
                    var limit = parameters.Value<int?>("limit") ?? 50;
                    var source = parameters.Value<string>("source");
                    return new CommandSuccessResponse("Console entries.", new
                    {
                        entries = ConsoleStore.ReadMerged(level, limit, source)
                    });
                case "clear":
                    ConsoleStore.Clear();
                    return new CommandSuccessResponse("Console cleared.");
                default:
                    return new CommandErrorResponse("Unsupported console action '" + action + "'.");
            }
        }
    }
}
