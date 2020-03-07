using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Penguin.Reflection
{
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

    public static class ReflectiveEnumerator
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
    {
        /// <summary>
        /// Gets all subtypes (inc specified) in current executing assembly
        /// </summary>
        /// <typeparam name="T">The root type to check for</typeparam>
        /// <returns>All the subtypes.</returns>
        public static IEnumerable<Type> GetEnumerableOfType<T>() where T : class
        {
            foreach (Type type in
                Assembly.GetAssembly(typeof(T)).GetTypes()
                .Where(myType => myType.IsClass && !myType.IsAbstract && myType.IsSubclassOf(typeof(T))))
            {
                yield return typeof(T);
            }
        }

        /// <summary>
        /// For the specified type, returns a list of new instances of every type or subtype
        /// </summary>
        /// <typeparam name="T">The root type to check for</typeparam>
        /// <param name="constructorArgs">Any applicable constuctor arguments</param>
        /// <returns>A list of new instances of every type or subtype</returns>
        public static IEnumerable<T> GetInstancesOfType<T>(params object[] constructorArgs) where T : class
        {
            foreach (Type type in GetEnumerableOfType<T>())
            {
                yield return (T)Activator.CreateInstance(type, constructorArgs);
            }
        }

        /// <summary>
        /// Gets all types from the executing assembly that implement an interface
        /// </summary>
        /// <typeparam name="T">The interface to check for</typeparam>
        /// <returns>All the types</returns>
        public static IEnumerable<Type> GetTypesThatImplementInterface<T>()
        {
            return Assembly.GetExecutingAssembly().GetTypes().Where(t => typeof(T).IsAssignableFrom(t) && !t.IsAbstract);
        }
    }
}