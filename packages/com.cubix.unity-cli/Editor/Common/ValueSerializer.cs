using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Cubix.UnityCli
{
    internal static class ValueSerializer
    {
        public static object Serialize(object value, int depth = 0)
        {
            if (value == null)
            {
                return null;
            }

            if (depth > 4)
            {
                return value.ToString();
            }

            var type = value.GetType();
            if (type.IsPrimitive || value is string || value is decimal)
            {
                return value;
            }

            if (value is Enum)
            {
                return value.ToString();
            }

            if (value is Vector3 vector3)
            {
                return new { x = vector3.x, y = vector3.y, z = vector3.z };
            }

            if (value is Vector2 vector2)
            {
                return new { x = vector2.x, y = vector2.y };
            }

            if (value is Color color)
            {
                return new { r = color.r, g = color.g, b = color.b, a = color.a };
            }

            if (value is Quaternion rotation)
            {
                return new { x = rotation.x, y = rotation.y, z = rotation.z, w = rotation.w };
            }

            if (value is Matrix4x4 matrix)
            {
                return new
                {
                    m00 = matrix.m00,
                    m01 = matrix.m01,
                    m02 = matrix.m02,
                    m03 = matrix.m03,
                    m10 = matrix.m10,
                    m11 = matrix.m11,
                    m12 = matrix.m12,
                    m13 = matrix.m13,
                    m20 = matrix.m20,
                    m21 = matrix.m21,
                    m22 = matrix.m22,
                    m23 = matrix.m23,
                    m30 = matrix.m30,
                    m31 = matrix.m31,
                    m32 = matrix.m32,
                    m33 = matrix.m33
                };
            }

            if (value is UnityEngine.Object unityObject)
            {
                return new
                {
                    name = unityObject.name,
                    type = unityObject.GetType().Name,
                    instanceId = unityObject.GetInstanceID()
                };
            }

            if (value is IDictionary dictionary)
            {
                var output = new Dictionary<string, object>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    output[entry.Key.ToString()] = Serialize(entry.Value, depth + 1);
                }

                return output;
            }

            if (value is IEnumerable enumerable)
            {
                var output = new List<object>();
                var count = 0;
                foreach (var item in enumerable)
                {
                    output.Add(Serialize(item, depth + 1));
                    count++;
                    if (count >= 100)
                    {
                        output.Add("...truncated...");
                        break;
                    }
                }

                return output;
            }

            var members = new Dictionary<string, object>();
            var flags = BindingFlags.Instance | BindingFlags.Public;
            foreach (var field in type.GetFields(flags))
            {
                if (field.IsSpecialName)
                {
                    continue;
                }

                members[field.Name] = Serialize(field.GetValue(value), depth + 1);
            }

            foreach (var property in type.GetProperties(flags))
            {
                if (!property.CanRead || property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                if (members.ContainsKey(property.Name))
                {
                    continue;
                }

                try
                {
                    members[property.Name] = Serialize(property.GetValue(value, null), depth + 1);
                }
                catch
                {
                    members[property.Name] = "<unavailable>";
                }
            }

            return members;
        }
    }
}
