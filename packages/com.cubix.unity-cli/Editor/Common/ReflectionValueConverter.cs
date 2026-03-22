using System;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Cubix.UnityCli
{
    internal static class ReflectionValueConverter
    {
        public static object ConvertTo(JToken token, Type targetType)
        {
            if (targetType == null)
            {
                throw new ArgumentNullException(nameof(targetType));
            }

            if (token == null || token.Type == JTokenType.Null)
            {
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;
            }

            var effectiveType = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (effectiveType == typeof(string))
            {
                return token.Value<string>();
            }

            if (effectiveType == typeof(int))
            {
                return token.Value<int>();
            }

            if (effectiveType == typeof(float))
            {
                return token.Value<float>();
            }

            if (effectiveType == typeof(double))
            {
                return token.Value<double>();
            }

            if (effectiveType == typeof(bool))
            {
                return token.Value<bool>();
            }

            if (effectiveType == typeof(long))
            {
                return token.Value<long>();
            }

            if (effectiveType.IsEnum)
            {
                return Enum.Parse(effectiveType, token.Value<string>(), true);
            }

            if (effectiveType == typeof(Vector3))
            {
                return VectorParsing.ReadVector3(token, Vector3.zero);
            }

            if (effectiveType == typeof(Vector2))
            {
                var value = VectorParsing.ReadVector3(token, Vector3.zero);
                return new Vector2(value.x, value.y);
            }

            if (effectiveType == typeof(Color))
            {
                var value = VectorParsing.ReadVector3(token, Vector3.zero);
                var alpha = 1f;
                var array = token as JArray;
                if (array != null && array.Count > 3)
                {
                    alpha = array[3].Value<float>();
                }

                return new Color(value.x, value.y, value.z, alpha);
            }

            return token.ToObject(effectiveType);
        }
    }
}
