﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Penguin.Reflection
{
    /// <summary>
    /// A static class used to perform all kinds of type based reflections over a whitelist of assemblies for finding and resolving many kinds of queries
    /// </summary>
    public static class TypeFactory
    {
        #region Properties

        /// <summary>
        /// Provides a log for debugging type loading
        /// </summary>
        public static Action<string> Log { get; set; }

        #endregion Properties

        #region Constructors

        internal static List<string> LoadFailedCache()
        {
            List<string> failedCache = new List<string>();

            if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FAILED_CACHE)))
            {
                failedCache = File.ReadAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FAILED_CACHE)).ToList();
            }

            return failedCache;
        }

        internal static List<string> LoadBlacklistCache()
        {
            try
            {
                string BlacklistFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BLACKLIST_CACHE);

                List<string> blacklist = new List<string>();

                if (File.Exists(BlacklistFile))
                {
                    blacklist = File.ReadAllLines(BlacklistFile).ToList();
                }
                else
                {
                    File.WriteAllText(BlacklistFile, "# Enter a regex expression to blacklist a DLL from being loaded" + System.Environment.NewLine);
                }

                return blacklist.Where(s => !s.StartsWith("#")).ToList();
            } catch(Exception)
            {
                return new List<string>();
            }
        }

        

        /// <summary>
        /// Since everything is cached, we need to make sure ALL potential assemblies are loaded or we might end up missing classes because
        /// the assembly hasn't been loaded yet. Consider only loading whitelisted references if this is slow
        /// </summary>
        static TypeFactory()
        {
            List<string> toLog = new List<string>()
            {
                $"Penguin.Reflection: {Assembly.GetExecutingAssembly().GetName().Version}"
            };


            List<string> failedCache = LoadFailedCache();
            List<string> blacklist = LoadBlacklistCache();

            List<Assembly> loadedAssemblies = AppDomain.CurrentDomain.GetAssemblies().ToList();
            string[] loadedPaths = loadedAssemblies.Where(a => !a.IsDynamic).Select(a => a.Location).ToArray();

            List<string> referencedPaths = new List<string>();

            Log?.Invoke($"Dynamically loading assemblys from {AppDomain.CurrentDomain.BaseDirectory}");
            toLog.Add($"Dynamically loading assemblys from {AppDomain.CurrentDomain.BaseDirectory}");

            referencedPaths.AddRange(Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "*.dll"));

            referencedPaths.AddRange(Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "*.exe").Where(s => s != System.Reflection.Assembly.GetEntryAssembly()?.Location));

            List<string> toLoad = referencedPaths.Where(r => !loadedPaths.Contains(r, StringComparer.InvariantCultureIgnoreCase)).ToList();

            foreach (string loadPath in toLoad)
            {
                
                if (failedCache.Contains(loadPath))
                {
                    continue;
                }
                //Check for blacklist
                string matchingLine = blacklist.FirstOrDefault(b => Regex.IsMatch(Path.GetFileName(loadPath), b));
                if (!string.IsNullOrWhiteSpace(matchingLine))
                {
                    string log = $"Skipping assembly due to blacklist match ({matchingLine}) {loadPath}";     
                    
                    Log?.Invoke(log);

                    toLog.Add(log);

                    continue;
                }
                

                try
                {
                    Log?.Invoke($"Dynamically loading assembly {loadPath}");
                    toLog.Add($"Dynamically loading assembly {loadPath}");
                    loadedAssemblies.Add(AppDomain.CurrentDomain.Load(AssemblyName.GetAssemblyName(loadPath)));
                }
                catch (Exception ex)
                {
                    Log?.Invoke(ex.Message);
                    Log?.Invoke(ex.StackTrace);
                    failedCache.Add(loadPath);
                }
            }

            File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FAILED_CACHE), failedCache);
        }

        #endregion Constructors

        #region Methods

        /// <summary>
        /// Gets all types in whitelisted assemblies that implement a given interface
        /// </summary>
        /// <typeparam name="T">The interface to check for</typeparam>
        /// <returns>All of the aforementioned types</returns>
        public static IEnumerable<Type> GetAllImplementations<T>() => GetAllTypes().Where(p => typeof(T).IsAssignableFrom(p) && !p.IsAbstract).Distinct();

        /// <summary>
        /// Gets all types in whitelisted assemblies that implement a given interface
        /// </summary>
        /// <param name="InterfaceType">The interface to check for</param>
        /// <returns>All of the aforementioned types</returns>
        public static IEnumerable<Type> GetAllImplementations(Type InterfaceType) => GetAllTypes().Where(p => InterfaceType.IsAssignableFrom(p) && !p.IsAbstract).Distinct();

        /// <summary>
        /// Gets all types from all whitelisted assemblies
        /// </summary>
        /// <returns></returns>
        public static IEnumerable<Type> GetAllTypes()
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (Type t in GetAssemblyTypes(a))
                {
                    Log?.Invoke($"Found type {t.Name}");
                    yield return t;
                }
            }
        }

        /// <summary>
        /// Gets all types in the specified assembly (where not compiler generated)
        /// </summary>
        /// <param name="a">The assembly to check</param>
        /// <returns>All the types in the assembly</returns>
        public static IEnumerable<Type> GetAssemblyTypes(Assembly a)
        {
            Log?.Invoke($"Getting types for assembly {a.FullName}");
            if (!AssemblyTypes.ContainsKey(a.FullName))
            {
                List<Type> types = null;

                try
                {
                    types = a.GetTypes().Where(t => !Attribute.IsDefined(t, typeof(CompilerGeneratedAttribute), true)).ToList();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    Log?.Invoke(ex.Message);
                    Console.WriteLine(ex.Message);
                    Console.WriteLine(ex.StackTrace);

                    try
                    {
                        types = new List<Type>();

                        foreach (Type t in ex.Types)
                        {
                            try
                            {
                                if (t != null && !Attribute.IsDefined(t, typeof(CompilerGeneratedAttribute), true))
                                {
                                    types.Add(t);
                                }
                            }
                            catch (Exception exxx)
                            {
                                Log?.Invoke("Failed to load type: " + exxx.Message);
                            }
                        }
                    }
                    catch (Exception exx)
                    {
                        Log?.Invoke("Failed to enumerate loaded types: " + exx.Message);
                    }
                }
                catch (Exception ex)
                {
                    Log?.Invoke(ex.Message);
                    Console.WriteLine(ex.Message);
                    Console.WriteLine(ex.StackTrace);
                    types = new List<Type>();
                }

                AssemblyTypes.TryAdd(a.FullName, types);

                return types.ToList();
            }
            else
            {
                return AssemblyTypes[a.FullName];
            }
        }

        /// <summary>
        /// Gets a list of all types derived from the current type
        /// </summary>
        /// <param name="t">The root type to check for</param>
        /// <returns>All of the derived types</returns>
        public static IEnumerable<Type> GetDerivedTypes(Type t)
        {
            if (t.IsInterface)
            {
                throw new ArgumentException($"Type to check for can not be interface as this method uses 'IsSubclassOf'. To search for interfaces use {nameof(GetAllImplementations)}");
            }

            Log?.Invoke($"Checking for derived types for {t.Name}");
            if (DerivedTypes.ContainsKey(t))
            {
                Log?.Invoke($"Using cached results");
                foreach (Type toReturn in DerivedTypes[t])
                {
                    yield return toReturn;
                }
            }
            else
            {
                List<Type> typesToReturn = new List<Type>();

                foreach (Type type in GetAllTypes())
                {
                    if (type.IsSubclassOf(t) && type.Module.ScopeName != "EntityProxyModule")
                    {
                        Log?.Invoke($"Type {type.AssemblyQualifiedName} derives from {t.AssemblyQualifiedName}");
                        typesToReturn.Add(type);
                    }
                    else
                    {
                        Log?.Invoke($"Type {type.AssemblyQualifiedName} does not derive from {t.AssemblyQualifiedName}");
                    }
                }

                DerivedTypes.TryAdd(t, typesToReturn.ToList());

                foreach (Type toReturn in typesToReturn)
                {
                    yield return toReturn;
                }
            }
        }

        /// <summary>
        /// Gets the most derived type of the specified type. For use when inheritence is used to determine
        /// the proper type to return
        /// </summary>
        /// <param name="t">The base type to check for (Ex DbContext)</param>
        /// <returns>The most derived type, or error if branching tree</returns>
        public static Type GetMostDerivedType(Type t)
        {
            return GetMostDerivedType(GetDerivedTypes(t).ToList(), t);
        }

        /// <summary>
        /// Gets the most derived type matching the base type, from a custom list of types
        /// </summary>
        /// <param name="types">The list of types to check</param>
        /// <param name="t">The base type to check for</param>
        /// <returns>The most derived type out of the list</returns>
        public static Type GetMostDerivedType(List<Type> types, Type t)
        {
            List<Type> BaseTypes = types.Select(it => it.BaseType).Where(b => b != null).Distinct().ToList();

            List<Type> toReturn = new List<Type>();

            void RemoveBase(Type tr)
            {
                if (tr is null)
                {
                    return;
                }

                RemoveBase(tr.BaseType);

                types.Remove(tr);
            }

            foreach (Type b in BaseTypes)
            {
                RemoveBase(b);
            }

            if (types.Count() > 1)
            {
                throw new Exception($"More than one terminating type found for base {t.FullName}");
            }

            return types.FirstOrDefault() ?? t;
        }

        /// <summary>
        /// Gets the properties of the type
        /// </summary>
        /// <param name="t">The type to get the properies of</param>
        /// <returns>All of the properties. All of them.</returns>
        public static PropertyInfo[] GetProperties(Type t) => GetProperties(t);

        /// <summary>
        /// Gets all the properties of the object
        /// </summary>
        /// <param name="o">The object to get the properties of</param>
        /// <returns>All of the properties. All of them.</returns>
        public static PropertyInfo[] GetProperties(object o) => GetProperties(GetType(o));

        /// <summary>
        /// Gets the type of the object. Currently strips off EntityProxy type to expose the underlying type.
        /// Should be altered to use a func system for custom resolutions
        /// </summary>
        /// <param name="o">The object to get the type of </param>
        /// <returns>The objects type</returns>
        public static Type GetType(object o)
        {
            if (o is null)
            {
                return null;
            }

            Type toReturn = new List<string>() { "EntityProxyModule", "RefEmit_InMemoryManifestModule" }.Contains(o.GetType().Module.ScopeName) ? o.GetType().BaseType : o.GetType();

            return toReturn;
        }

        /// <summary>
        /// Searches all whitelisted assemblies to find a type with the given full name
        /// </summary>
        /// <param name="name">The full name to check for</param>
        /// <param name="BaseType">An optional base type requirement</param>
        /// <param name="includeDerived">Whether or not to include types that inherit from the specified name type</param>
        /// <param name="targetNamespace">An optional restriction on the namespace of the search</param>
        /// <returns>A type matching the full name, or derived type</returns>
        public static Type GetTypeByFullName(string name, Type BaseType = null, bool includeDerived = false, string targetNamespace = "")
        {
            bool nameIsCached = TypeMapping.ContainsKey(name);
            bool namespaceIsCached = nameIsCached && TypeMapping[name].ContainsKey(targetNamespace);

            if (!nameIsCached || !namespaceIsCached)
            {
                List<Type> matching = GetTypeByFullName(name).ToList();

                if (includeDerived)
                {
                    List<Type> derivedTypes = new List<Type>();

                    derivedTypes.AddRange(matching.Select(m => GetDerivedTypes(m)).SelectMany(m => m));

                    matching.AddRange(derivedTypes);
                }

                matching = matching.Where(t => targetNamespace == string.Empty || targetNamespace == t.Namespace).Distinct().ToList();

                Type targetType = null;

                foreach (Type t in matching)
                {
                    if (BaseType != null && !t.IsSubclassOf(BaseType))
                    {
                        continue;
                    }

                    if (targetType == null || targetType.IsSubclassOf(t))
                    {
                        targetType = t;
                    }
                    else if (targetType != null && !targetType.IsSubclassOf(t) && !t.IsSubclassOf(targetType))
                    {
                        throw new Exception("Found multiple noninherited types that match name " + name);
                    }
                }

                if (nameIsCached)
                {
                    TypeMapping[name].TryAdd(targetNamespace, targetType);
                }
                else
                {
                    ConcurrentDictionary<string, Type> namespaceDictionary = new ConcurrentDictionary<string, Type>();
                    namespaceDictionary.TryAdd(targetNamespace, targetType);

                    TypeMapping.TryAdd(name, namespaceDictionary);
                }
            }

            return TypeMapping[name][targetNamespace];
        }

        /// <summary>
        /// Checks if the specified type declares a given attribute
        /// </summary>
        /// <param name="toCheck">The type to check</param>
        /// <param name="attribute">the attribute type to check for</param>
        /// <returns>Whether or not the attribute is declared on the type</returns>
        public static bool HasAttribute(MemberInfo toCheck, Type attribute) => Cache.HasAttribute(toCheck, attribute);

        /// <summary>
        /// Checks if an object has an attribute declared on its type
        /// </summary>
        /// <param name="o">The object to check</param>
        /// <param name="attribute">The attribute to check for</param>
        /// <returns>Whether or not the attribute is declared on the object type</returns>
        public static bool HasAttribute(object o, Type attribute) => HasAttribute(GetType(o), attribute);

        /// <summary>
        /// Retrieves the first matching attribute of the specified type
        /// </summary>
        /// <typeparam name="T">The base type to find</typeparam>
        /// <param name="toCheck">The member to check</param>
        /// <returns>The first matching attribute</returns>
        public static T RetrieveAttribute<T>(MemberInfo toCheck) where T : Attribute => toCheck.GetCustomAttribute<T>();

        /// <summary>
        /// Gets a list of all custom attributes on the member
        /// </summary>
        /// <typeparam name="T">The base type of the attributes to get</typeparam>
        /// <param name="toCheck">The member to retrieve the information for</param>
        /// <returns>all custom attributes</returns>
        public static List<T> RetrieveAttributes<T>(MemberInfo toCheck) where T : Attribute => toCheck.GetCustomAttributes<T>().ToList();

        #endregion Methods

        #region Fields

        internal const string FAILED_CACHE = "TypeFactory.Failed.Cache";
        internal const string BLACKLIST_CACHE = "TypeFactory.BlackList.Cache";

        #endregion Fields

        private static ConcurrentDictionary<string, ICollection<Type>> AssemblyTypes { get; set; } = new ConcurrentDictionary<string, ICollection<Type>>();

        private static ConcurrentDictionary<Type, ICollection<Type>> DerivedTypes { get; set; } = new ConcurrentDictionary<Type, ICollection<Type>>();

        private static ConcurrentDictionary<string, ConcurrentDictionary<string, Type>> TypeMapping { get; set; } = new ConcurrentDictionary<string, ConcurrentDictionary<string, Type>>();

        private static Type[] GetTypeByFullName(string className)
        {
            List<Type> returnVal = new List<Type>();

            foreach (Type t in GetAllTypes())
            {
                if (string.Equals(t.FullName, className, StringComparison.InvariantCultureIgnoreCase))
                {
                    returnVal.Add(t);
                }
            }

            return returnVal.ToArray();
        }
    }
}