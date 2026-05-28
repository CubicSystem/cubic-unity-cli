using System;

namespace CubicEngine.UnityCli
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    internal sealed class CubixCliCommandAttribute : Attribute
    {
        public string Name { get; set; }
        public string Group { get; set; }
        public string Description { get; set; }
    }
}
