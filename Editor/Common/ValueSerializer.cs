using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace CubicEngine.UnityCli
{
    internal static class ValueSerializer
    {
        private const int MaxSerializedItems = 128;
        private const double MaxSerializationMilliseconds = 50.0d;

        private sealed class SerializationContext
        {
            private readonly HashSet<object> _activeReferences = new HashSet<object>(ReferenceEqualityComparer.Instance);
            private readonly DateTime _startedAtUtc = DateTime.UtcNow;
            private int _remainingItems = MaxSerializedItems;

            public bool IsTimedOut => (DateTime.UtcNow - _startedAtUtc).TotalMilliseconds >= MaxSerializationMilliseconds;

            public bool TryConsumeItem()
            {
                if (_remainingItems <= 0)
                {
                    return false;
                }

                _remainingItems--;
                return true;
            }

            public bool TryEnter(object value)
            {
                if (!ShouldTrackReference(value))
                {
                    return true;
                }

                return _activeReferences.Add(value);
            }

            public void Exit(object value)
            {
                if (ShouldTrackReference(value))
                {
                    _activeReferences.Remove(value);
                }
            }

            private static bool ShouldTrackReference(object value)
            {
                return value != null &&
                    !(value is string) &&
                    !(value is ValueType) &&
                    !(value is UnityEngine.Object);
            }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }

        public static object Serialize(object value, int depth = 0)
        {
            return Serialize(value, depth, new SerializationContext());
        }

        private static object Serialize(object value, int depth, SerializationContext context)
        {
            if (context.IsTimedOut)
            {
                return "<time-budget-exceeded>";
            }

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
                if (!context.TryEnter(value))
                {
                    return "<cycle>";
                }

                var output = new Dictionary<string, object>();
                try
                {
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (context.IsTimedOut)
                        {
                            output["..."] = "<time-budget-exceeded>";
                            break;
                        }

                        if (!context.TryConsumeItem())
                        {
                            output["..."] = "<budget-exceeded>";
                            break;
                        }

                        output[entry.Key.ToString()] = Serialize(entry.Value, depth + 1, context);
                    }
                }
                finally
                {
                    context.Exit(value);
                }

                return output;
            }

            if (value is IEnumerable enumerable)
            {
                if (!context.TryEnter(value))
                {
                    return "<cycle>";
                }

                var output = new List<object>();
                try
                {
                    foreach (var item in enumerable)
                    {
                        if (context.IsTimedOut)
                        {
                            output.Add("<time-budget-exceeded>");
                            break;
                        }

                        if (!context.TryConsumeItem())
                        {
                            output.Add("<budget-exceeded>");
                            break;
                        }

                        output.Add(Serialize(item, depth + 1, context));
                    }
                }
                finally
                {
                    context.Exit(value);
                }

                return output;
            }

            if (!context.TryEnter(value))
            {
                return "<cycle>";
            }

            var members = new Dictionary<string, object>();
            try
            {
                foreach (var memberName in ReflectionMemberAccess.ListMembers(type))
                {
                    if (context.IsTimedOut)
                    {
                        members["..."] = "<time-budget-exceeded>";
                        break;
                    }

                    if (!context.TryConsumeItem())
                    {
                        members["..."] = "<budget-exceeded>";
                        break;
                    }

                    try
                    {
                        var memberValue = ReflectionMemberAccess.ReadMember(value, memberName);
                        members[memberName] = Serialize(memberValue, depth + 1, context);
                    }
                    catch
                    {
                        members[memberName] = "<unavailable>";
                    }
                }
            }
            finally
            {
                context.Exit(value);
            }

            return members;
        }
    }
}
