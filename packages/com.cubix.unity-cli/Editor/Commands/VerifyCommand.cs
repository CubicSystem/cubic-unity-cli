using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Cubix.UnityCli
{
    [CubixCliCommand(Group = "verify", Name = "verify", Description = "Run Unity script verification workflow.")]
    internal sealed class VerifyCommand : ICubixCliCommandHandler
    {
        public IEnumerable<CommandDefinition> DescribeActions()
        {
            yield return new CommandDefinition
            {
                Action = "run",
                Description = "Reimport or refresh scripts, await compilation, and report compiler errors.",
                Tags = CommandMetadata.Tags(CommandTags.Core, CommandTags.Scripts, CommandTags.Diagnostics),
                Parameters = CommandMetadata.Parameters(
                    CommandMetadata.Parameter("path", "string", false, "Known AssetDatabase path for the changed script."),
                    CommandMetadata.Parameter("mode", "string", false, "Verification mode.", "refresh", "reimport", "refresh"),
                    CommandMetadata.Parameter("timeoutMs", "integer", false, "Maximum time to wait for compilation.", 180000))
            };
            yield return new CommandDefinition
            {
                Action = "status",
                Description = "Read the current verify job state.",
                Tags = CommandMetadata.Tags(CommandTags.Core, CommandTags.Diagnostics)
            };
        }

        public object Execute(string action, JObject parameters)
        {
            switch (action)
            {
                case "run":
                    return new CommandSuccessResponse("Verify started.", CompilationAwaiter.StartVerify(parameters));
                case "status":
                    return new CommandSuccessResponse("Verify status.", CompilationAwaiter.GetVerifyJob());
                default:
                    return new CommandErrorResponse("Unsupported verify action '" + action + "'.");
            }
        }
    }
}
