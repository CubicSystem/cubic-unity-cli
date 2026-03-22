using System;

namespace Cubix.UnityCli
{
    [Serializable]
    internal sealed class CommandSuccessResponse
    {
        public bool success = true;
        public string message;
        public object data;
        public object errors;

        public CommandSuccessResponse(string message, object data = null, object errors = null)
        {
            this.message = message;
            this.data = data;
            this.errors = errors;
        }
    }

    [Serializable]
    internal sealed class CommandErrorResponse
    {
        public bool success = false;
        public string message;
        public object data;
        public object errors;

        public CommandErrorResponse(string message, object data = null, object errors = null)
        {
            this.message = message;
            this.data = data;
            this.errors = errors;
        }
    }
}
