using System.Text;

namespace CubicEngine.UnityCli
{
    internal static class StringCaseUtility
    {
        public static string ToSnakeCase(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return input;
            }

            var builder = new StringBuilder(input.Length + 8);
            for (var index = 0; index < input.Length; index++)
            {
                var character = input[index];
                if (char.IsUpper(character))
                {
                    if (index > 0)
                    {
                        builder.Append('_');
                    }

                    builder.Append(char.ToLowerInvariant(character));
                }
                else
                {
                    builder.Append(character);
                }
            }

            return builder.ToString();
        }
    }
}
