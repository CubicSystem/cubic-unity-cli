using System.Collections.Generic;
using System.Linq;

namespace CubicEngine.UnityCli
{
    internal static class ToolDiscovery
    {
        public static IReadOnlyList<object> GetCommandMetadata()
        {
            return CommandRouter.ListCommands(includeUnsafe: true)
                .Select(definition => (object)definition)
                .ToList();
        }
    }
}
