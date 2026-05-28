using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace CubicEngine.UnityCli
{
    [CubixCliCommand(Group = "test", Name = "test", Description = "Run Unity Test Runner suites and read their status.")]
    internal sealed class TestCommand : ICubixCliCommandHandler
    {
        public IEnumerable<CommandDefinition> DescribeActions()
        {
            yield return new CommandDefinition
            {
                Action = "run",
                Description = "Queue and run Unity tests through the Unity Test Runner.",
                Tags = CommandMetadata.Tags(CommandTags.Core, CommandTags.Tests, CommandTags.Diagnostics),
                Parameters = CommandMetadata.Parameters(
                    CommandMetadata.Parameter("platform", "string", true, "Test platform to run.", null, "EditMode", "PlayMode"),
                    CommandMetadata.Parameter("assemblyNames", "string[]", false, "Optional test assembly filters."),
                    CommandMetadata.Parameter("categoryNames", "string[]", false, "Optional NUnit category filters."),
                    CommandMetadata.Parameter("testNames", "string[]", false, "Optional exact test or fixture filters."),
                    CommandMetadata.Parameter("timeoutMs", "integer", false, "Maximum time to wait for the Unity test run.", 180000),
                    CommandMetadata.Parameter("resultsPath", "string", false, "Optional JSON results output path."))
            };
            yield return new CommandDefinition
            {
                Action = "status",
                Description = "Read the current Unity test run state.",
                Tags = CommandMetadata.Tags(CommandTags.Core, CommandTags.Tests, CommandTags.Diagnostics)
            };
        }

        public object Execute(string action, JObject parameters)
        {
            switch (action)
            {
                case "run":
                    return new CommandSuccessResponse("Unity test run queued.", TestRunController.StartRun(parameters));
                case "status":
                    return new CommandSuccessResponse("Unity test status.", TestRunController.GetCurrentJob());
                default:
                    return new CommandErrorResponse("Unsupported test action '" + action + "'.");
            }
        }
    }
}
