using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace Cubix.UnityCli
{
    internal static class ReflectionMemberAccess
    {
        private const BindingFlags MemberFlags = BindingFlags.Instance | BindingFlags.Public;

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
                if (property.CanRead && property.GetIndexParameters().Length == 0)
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
    }
}
