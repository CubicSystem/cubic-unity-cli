using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace CubicEngine.UnityCli
{
    internal static class VectorParsing
    {
        public static Vector3 ReadVector3(JToken token, Vector3 fallback)
        {
            if (token == null)
            {
                return fallback;
            }

            if (token.Type == JTokenType.Array)
            {
                var array = (JArray)token;
                if (array.Count < 3)
                {
                    return fallback;
                }

                return new Vector3(
                    array[0].Value<float>(),
                    array[1].Value<float>(),
                    array[2].Value<float>());
            }

            if (token.Type == JTokenType.Object)
            {
                var obj = (JObject)token;
                return new Vector3(
                    obj.Value<float?>("x") ?? fallback.x,
                    obj.Value<float?>("y") ?? fallback.y,
                    obj.Value<float?>("z") ?? fallback.z);
            }

            if (token.Type == JTokenType.String)
            {
                var parts = token.Value<string>().Split(',');
                if (parts.Length < 3)
                {
                    return fallback;
                }

                float x;
                float y;
                float z;
                if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                    !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y) ||
                    !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                {
                    return fallback;
                }

                return new Vector3(x, y, z);
            }

            return fallback;
        }
    }
}
