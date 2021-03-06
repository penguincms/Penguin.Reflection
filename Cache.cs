﻿using Penguin.Reflection.Abstractions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Reflection;

namespace Penguin.Reflection
{
    /// <summary>
    /// A static cache designed to help speed up attribute retrieval
    /// </summary>
    public static class TypeCache
    {
        internal static ConcurrentDictionary<MemberInfo, AttributeInstance[]> Attributes { get; set; } = new ConcurrentDictionary<MemberInfo, AttributeInstance[]>();

        internal static ConcurrentDictionary<Type, PropertyInfo[]> Properties { get; set; } = new ConcurrentDictionary<Type, PropertyInfo[]>();

        /// <summary>
        /// Gets the first attribute matching the specified type
        /// </summary>
        /// <typeparam name="T">The attribute type</typeparam>
        /// <param name="p">The member source</param>
        /// <returns>The first attribute matching the specified type</returns>
        public static T GetAttribute<T>(MemberInfo p) where T : Attribute
        {
            return GetCustomAttributes(p).First(a => a.Instance.GetType() == typeof(T)).Instance as T;
        }

        /// <summary>
        /// Gets all attribute instances from the current member
        /// </summary>
        /// <param name="p">The member source</param>
        /// <returns>All attribute instances from the current member</returns>
        public static AttributeInstance[] GetCustomAttributes(MemberInfo p)
        {
            Contract.Assert(p != null);

            if (Attributes.TryGetValue(p, out AttributeInstance[] cachedAttributes))
            {
                return cachedAttributes;
            }

            List<AttributeInstance> attributes = new List<AttributeInstance>();

            if (p is PropertyInfo)
            {
                PropertyInfo toCheck = p as PropertyInfo;
                bool inherited = false;

                do
                {
                    attributes.AddRange(toCheck.GetCustomAttributes(true).Select(a => new AttributeInstance(toCheck, a as Attribute, inherited)));
                    inherited = true;
                } while ((toCheck = toCheck.DeclaringType.BaseType?.GetProperty(toCheck.Name)) != null);
            }
            else if (p is Type)
            {
                Type toCheck = p as Type;

                do
                {
                    //This can be recursive to leverage the cache, if it needs to be,
                    //however the logic for "isinherited" would need to be changed to reflect that
                    foreach (object instance in p.GetCustomAttributes(false))
                    {
                        attributes.Add(new AttributeInstance(toCheck, instance as Attribute, p != toCheck));
                    }

                    toCheck = toCheck.BaseType;
                } while (toCheck != null);
            }
            else
            {
                throw new Exception($"Unsupported MemberInfo type {p.GetType()}");
            }

            AttributeInstance[] toReturn = attributes.ToArray();

            Attributes.TryAdd(p, toReturn);

            return toReturn;
        }

        /// <summary>
        /// Gets all the properties of the current type
        /// </summary>
        /// <param name="t">The type to search</param>
        /// <returns>All of the properties. All of them</returns>
        public static PropertyInfo[] GetProperties(Type t)
        {
            Contract.Assert(t != null);

            if (!Properties.TryGetValue(t, out PropertyInfo[] properties))
            {
                properties = t.GetProperties();
                Properties.TryAdd(t, properties);
            }

            return properties;
        }

        /// <summary>
        /// Checks to see if the given member contains an attribute of a specified type
        /// </summary>
        /// <typeparam name="T">The type to check for</typeparam>
        /// <param name="p">The member to check</param>
        /// <returns>Does the member declare this attribute?</returns>
        public static bool HasAttribute<T>(MemberInfo p) where T : Attribute
        {
            return GetCustomAttributes(p).Any(a => a.Instance.GetType() == typeof(T));
        }

        /// <summary>
        /// Checks to see if the given member contains an attribute of a specified type
        /// </summary>
        /// <param name="p">The member to check</param>
        /// <param name="t">The type to check for</param>
        /// <returns>Does the member declare this attribute?</returns>
        public static bool HasAttribute(MemberInfo p, Type t)
        {
            return GetCustomAttributes(p).Any(a => a.Instance.GetType() == t);
        }
    }
}