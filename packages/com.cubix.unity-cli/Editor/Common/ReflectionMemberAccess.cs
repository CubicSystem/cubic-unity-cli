using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Cubix.UnityCli
{
    internal static class ReflectionMemberAccess
    {
        private const BindingFlags MemberFlags = BindingFlags.Instance | BindingFlags.Public;
        private static readonly Dictionary<Type, HashSet<string>> BlockedReadablePropertiesByType =
            new Dictionary<Type, HashSet<string>>
            {
                {
                    typeof(Transform),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "localToWorldMatrix",
                        "worldToLocalMatrix",
                        "transformHandle"
                    }
                }
            };

        public static IEnumerable<string> ListMembers(Type type)
        {
            var names = new HashSet<string>();
            foreach (var field in type.GetFields(MemberFlags))
            {
                if (!field.IsSpecialName)
                {
                    names.Add(field.Name);
                }
            }

            foreach (var property in type.GetProperties(MemberFlags))
            {
                if (property.CanRead &&
                    property.GetIndexParameters().Length == 0 &&
                    !IsBlockedReadableProperty(type, property.Name))
                {
                    names.Add(property.Name);
                }
            }

            return names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
        }

        public static object ReadMember(object instance, string memberName)
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }

            if (string.IsNullOrWhiteSpace(memberName))
            {
                throw new ArgumentException("Member name is required.", nameof(memberName));
            }

            var type = instance.GetType();
            var field = type.GetField(memberName, MemberFlags | BindingFlags.IgnoreCase);
            if (field != null)
            {
                return field.GetValue(instance);
            }

            if (IsBlockedReadableProperty(type, memberName))
            {
                throw new InvalidOperationException(
                    "Reading member '" + memberName + "' on type '" + type.FullName + "' is disabled for snapshot safety.");
            }

            var property = type.GetProperty(memberName, MemberFlags | BindingFlags.IgnoreCase);
            if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
            {
                return property.GetValue(instance, null);
            }

            throw new InvalidOperationException("Could not resolve member '" + memberName + "' on type '" + type.FullName + "'.");
        }

        public static void WriteMembers(object instance, JObject values)
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }

            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            foreach (var property in values.Properties())
            {
                WriteMember(instance, property.Name, property.Value);
            }
        }

        public static void WriteMember(object instance, string memberName, JToken value)
        {
            if (instance == null)
            {
                throw new ArgumentNullException(nameof(instance));
            }

            var type = instance.GetType();
            var field = type.GetField(memberName, MemberFlags | BindingFlags.IgnoreCase);
            if (field != null)
            {
                field.SetValue(instance, ReflectionValueConverter.ConvertTo(value, field.FieldType));
                return;
            }

            var property = type.GetProperty(memberName, MemberFlags | BindingFlags.IgnoreCase);
            if (property != null && property.CanWrite && property.GetIndexParameters().Length == 0)
            {
                property.SetValue(instance, ReflectionValueConverter.ConvertTo(value, property.PropertyType), null);
                return;
            }

            throw new InvalidOperationException("Could not write member '" + memberName + "' on type '" + type.FullName + "'.");
        }

        private static bool IsBlockedReadableProperty(Type type, string memberName)
        {
            foreach (var entry in BlockedReadablePropertiesByType)
            {
                if (entry.Key.IsAssignableFrom(type) && entry.Value.Contains(memberName))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
