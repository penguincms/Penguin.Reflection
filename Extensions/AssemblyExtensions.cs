using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Reflection;

namespace Penguin.Reflection.Extensions
{
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

    public static class AssemblyExtensions
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
    {
        /// <summary>
        /// Gets all types from all assemblies in the list
        /// </summary>
        /// <param name="assemblies">The source assemblies to search</param>
        /// <returns>The types found in the assemblies</returns>
        public static IEnumerable<Type> GetAllTypes(this IEnumerable<Assembly> assemblies)
        {
            Contract.Assert(assemblies != null);

            foreach (Assembly a in assemblies)
            {
                foreach (Type t in TypeFactory.GetAssemblyTypes(a))
                {
                    yield return t;
                }
            }
        }
    }
}