using Penguin.Debugging;
using Penguin.Reflection.Extensions;
using Penguin.Reflection.Objects;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text.RegularExpressions;

namespace Penguin.Reflection
{
    /// <summary>
    /// A static class used to perform all kinds of type based reflections over a whitelist of assemblies for finding and resolving many kinds of queries
    /// </summary>
    public static class TypeFactory
    {
        /// <summary>
        /// Since everything is cached, we need to make sure ALL potential assemblies are loaded or we might end up missing classes because
        /// the assembly hasn't been loaded yet. Consider only loading whitelisted references if this is slow
        /// </summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "<Pending>")]
        static TypeFactory()
        {
            StaticLogger.Log($"Penguin.Reflection: {Assembly.GetExecutingAssembly().GetName().Version}", StaticLogger.LoggingLevel.Call);

            List<string> failedCache = LoadFailedCache();
            List<string> blacklist;

            if (!TypeFactoryGlobalSettings.DisableFailedLoadSkip)
            {
                blacklist = LoadBlacklistCache();
            }
            else
            {
                blacklist = new List<string>();
            }

            Dictionary<string, Assembly> loadedPaths = new Dictionary<string, Assembly>();

            //Map out the loaded assemblies so we can find them by path
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!a.IsDynamic)
                {
                    if (!loadedPaths.ContainsKey(a.Location))
                    {
                        loadedPaths.Add(a.Location, a);
                    }
                }
            }

            List<string> referencedPaths = new List<string>();

            List<string> searchPaths = new List<string>()
            {
                AppDomain.CurrentDomain.BaseDirectory
            };

            if (AppDomain.CurrentDomain.RelativeSearchPath != null)
            {
                searchPaths.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppDomain.CurrentDomain.RelativeSearchPath));
            }

            //We're going to add the paths to the loaded assemblies here so we can double
            //back and ensure we're building the dependencies for the loaded assemblies that
            //do NOT reside in the EXE/Bin directories
            HashSet<string> SearchedPaths = new HashSet<string>();

            foreach (string searchPath in searchPaths)
            {
                StaticLogger.Log($"RE: Dynamically loading assemblys from {searchPath}", StaticLogger.LoggingLevel.Call);

                referencedPaths.AddRange(Directory.GetFiles(searchPath, "*.dll"));

                referencedPaths.AddRange(Directory.GetFiles(searchPath, "*.exe"));

                foreach (string loadPath in referencedPaths)
                {
                    SearchedPaths.Add(loadPath);

                    //If we're not already loaded
                    if (!loadedPaths.TryGetValue(loadPath, out Assembly a))
                    {
                        if (failedCache.Contains(loadPath))
                        {
                            StaticLogger.Log($"RE: Skipping due {FAILED_CACHE}: {loadPath}", StaticLogger.LoggingLevel.Call);
                            continue;
                        }

                        //Check for blacklist
                        string matchingLine = blacklist.FirstOrDefault(b => Regex.IsMatch(Path.GetFileName(loadPath), b));
                        if (!string.IsNullOrWhiteSpace(matchingLine))
                        {
                            StaticLogger.Log($"RE: Skipping assembly due to blacklist match ({matchingLine}) {loadPath}", StaticLogger.LoggingLevel.Call);

                            continue;
                        }

                        StaticLogger.Log($"RE: Dynamically loading assembly {loadPath}", StaticLogger.LoggingLevel.Call);

                        try
                        {
                            AssemblyName an = AssemblyName.GetAssemblyName(loadPath);

                            a = LoadAssembly(loadPath, an, true);

                            AssembliesByName.TryAdd(an.Name, a);
                        }
                        catch (Exception ex)
                        {
                            StaticLogger.Log(ex.Message, StaticLogger.LoggingLevel.Call);
                            StaticLogger.Log(ex.StackTrace, StaticLogger.LoggingLevel.Call);

                            failedCache.Add(loadPath);
                        }
                    }

                    if (!(a is null))
                    {
                        AddReferenceInformation(a);
                    }
                }
            }

            //And now we double check to make sure we're not missing anything in the loaded
            //assemblies that were not found in our path discovery
            foreach (KeyValuePair<string, Assembly> kvp in loadedPaths)
            {
                if (!SearchedPaths.Contains(kvp.Key))
                {
                    AddReferenceInformation(kvp.Value);
                }
            }

            StaticLogger.Log($"RE: {nameof(TypeFactory)} static initialization completed", StaticLogger.LoggingLevel.Final);

            try
            {
                AppDomain.CurrentDomain.AssemblyLoad += CurrentDomain_AssemblyLoad;

                List<Assembly> CurrentlyLoadedAssemblies = AppDomain.CurrentDomain.GetAssemblies().ToList();

                foreach (Assembly assembly in CurrentlyLoadedAssemblies)
                {
                    if (!assembly.IsDynamic)
                    {
                        CheckLoadingPath(assembly.Location);
                    }
                }
            }
            catch (SecurityException ex)
            {
                StaticLogger.Log($"RE: A security exception was thrown attempting to subscribe to assembly load events: {ex.Message}", StaticLogger.LoggingLevel.Final);
            }

            if (!TypeFactoryGlobalSettings.DisableFailedLoadSkip)
            {
                File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FAILED_CACHE), failedCache);
            }
        }

        private static void CurrentDomain_AssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            if (!args.LoadedAssembly.IsDynamic)
            {
                CheckLoadingPath(args.LoadedAssembly.Location);
            }
        }

        /// <summary>
        /// Gets all types in whitelisted assemblies that implement a given interface
        /// </summary>
        /// <typeparam name="T">The interface to check for</typeparam>
        /// <param name="IncludeAbstract">If true, the result set will include abstract types</param>
        /// <returns>All of the aforementioned types</returns>
        public static IEnumerable<Type> GetAllImplementations<T>(bool IncludeAbstract = false)
        {
            return GetAllImplementations(typeof(T), IncludeAbstract);
        }

        /// <summary>
        /// Gets all types in whitelisted assemblies that implement a given interface
        /// </summary>
        /// <param name="InterfaceType">The interface to check for, will also search for implementations of open generics</param>
        /// <param name="IncludeAbstract">If true, the result set will include abstract types</param>
        /// <returns>All of the aforementioned types</returns>
        public static IEnumerable<Type> GetAllImplementations(Type InterfaceType, bool IncludeAbstract = false)
        {
            if (InterfaceType is null)
            {
                throw new ArgumentNullException(nameof(InterfaceType));
            }

            IEnumerable<Type> candidates = GetDependentAssemblies(InterfaceType).GetAllTypes().Distinct();

            if (InterfaceType.IsGenericTypeDefinition)
            {
                foreach (Type t in candidates)
                {
                    if (!IncludeAbstract && t.IsAbstract)
                    {
                        continue;
                    }

                    bool isValid = t.GetInterfaces().Any(x =>
                      x.IsGenericType &&
                      x.GetGenericTypeDefinition() == InterfaceType);

                    if (isValid)
                    {
                        yield return t;
                    }
                }
            }
            else
            {
                foreach (Type t in candidates.Where(p => InterfaceType.IsAssignableFrom(p) && (IncludeAbstract || !p.IsAbstract)))
                {
                    yield return t;
                }
            }
        }

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
                    yield return t;
                }
            }
        }

        /// <summary>
        /// Gets an assembly by its AssemblyName
        /// </summary>
        /// <param name="Name">The AssemblyName</param>
        /// <returns>The matching Assembly</returns>
        public static Assembly GetAssemblyByName(AssemblyName Name)
        {
            Contract.Assert(Name != null);
            return GetAssemblyByName(Name.Name);
        }

        /// <summary>
        /// Gets an assembly by its AssemblyName.Name
        /// </summary>
        /// <param name="Name">The AssemblyName.Name</param>
        /// <returns>The matching Assembly</returns>
        public static Assembly GetAssemblyByName(string Name)
        {
            if (!AssembliesByName.TryGetValue(Name, out Assembly a))
            {
                a = AppDomain.CurrentDomain.GetAssemblies().First(aa => aa.GetName().Name == Name);
                AssembliesByName.TryAdd(Name, a);
            }

            return a;
        }

        /// <summary>
        /// Gets all types in the specified assembly (where not compiler generated)
        /// </summary>
        /// <param name="a">The assembly to check</param>
        /// <returns>All the types in the assembly</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "<Pending>")]
        public static IEnumerable<Type> GetAssemblyTypes(Assembly a)
        {
            Contract.Assert(a != null);

            if (!AssemblyTypes.TryGetValue(a.FullName, out AssemblyDefinition b))
            {
                StaticLogger.Log($"RE: Getting types for assembly {a.FullName}", StaticLogger.LoggingLevel.Call);

                List<Type> types = null;

                try
                {
                    types = a.GetTypes().Where(t => !Attribute.IsDefined(t, typeof(CompilerGeneratedAttribute), true)).Distinct().ToList();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    StaticLogger.Log(ex.Message, StaticLogger.LoggingLevel.Call);
                    StaticLogger.Log(ex.StackTrace, StaticLogger.LoggingLevel.Call);

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
                                StaticLogger.Log("RE: Failed to load type: " + exxx.Message, StaticLogger.LoggingLevel.Call);
                            }
                        }
                    }
                    catch (Exception exx)
                    {
                        StaticLogger.Log("RE: Failed to enumerate loaded types: " + exx.Message, StaticLogger.LoggingLevel.Call);
                    }

                    types = types.Distinct().ToList();
                }
                catch (Exception ex)
                {
                    StaticLogger.Log(ex.Message, StaticLogger.LoggingLevel.Call);
                    StaticLogger.Log(ex.StackTrace, StaticLogger.LoggingLevel.Call);
                    types = new List<Type>();
                }

                if (StaticLogger.Level != StaticLogger.LoggingLevel.None)
                {
                    foreach (Type t in types)
                    {
                        StaticLogger.Log($"RE: Found type {t.FullName}", StaticLogger.LoggingLevel.Call);
                    }
                }

                AssemblyTypes.TryAdd(a.FullName, new AssemblyDefinition() { ContainingAssembly = a, LoadedTypes = types });

                return types.ToList();
            }
            else
            {
                StaticLogger.Log($"RE: Using cached types for {a.FullName}", StaticLogger.LoggingLevel.Call);

                return b.LoadedTypes;
            }
        }

        /// <summary>
        /// Gets all assemblies that recursively reference the one containing the given type
        /// </summary>
        /// <param name="t">A type in the root assembly to search for </param>
        /// <returns>all assemblies that recursively reference the one containing the given type</returns>
        public static IEnumerable<Assembly> GetDependentAssemblies(Type t)
        {
            Contract.Assert(t != null);
            Assembly root = t.Assembly;

            foreach (Assembly a in GetDependentAssemblies(root))
            {
                yield return a;
            }
        }

        /// <summary>
        /// Gets all assemblies that recursively reference the given one
        /// </summary>
        /// <param name="a">The root assembly to search for </param>
        /// <returns>all assemblies that recursively reference the one containing the given type</returns>
        public static IEnumerable<Assembly> GetDependentAssemblies(Assembly a)
        {
            Contract.Assert(a != null);
            return GetDependentAssemblies(a, new HashSet<Assembly>());
        }

        /// <summary>
        /// Gets a list of all types derived from the current type
        /// </summary>
        /// <param name="t">The root type to check for</param>
        /// <returns>All of the derived types</returns>
        public static IEnumerable<Type> GetDerivedTypes(Type t)
        {
            if (t is null)
            {
                throw new ArgumentNullException(nameof(t));
            }

            if (t.IsInterface)
            {
                throw new ArgumentException($"Type to check for can not be interface as this method uses 'IsSubclassOf'. To search for interfaces use {nameof(GetAllImplementations)}");
            }

            if (DerivedTypes.ContainsKey(t))
            {
                StaticLogger.Log($"RE: Using cached derived types for {FriendlyTypeName(t)}", StaticLogger.LoggingLevel.Method);
                foreach (Type toReturn in DerivedTypes[t])
                {
                    yield return toReturn;
                }
            }
            else
            {
                StaticLogger.Log($"RE: Searching for derived types for {FriendlyTypeName(t)}", StaticLogger.LoggingLevel.Call);

                List<Type> typesToReturn = new List<Type>();

                foreach (Type type in GetDependentAssemblies(t).GetAllTypes())
                {
                    if (type.IsSubclassOf(t) && type.Module.ScopeName != "EntityProxyModule")
                    {
                        if (StaticLogger.Level != StaticLogger.LoggingLevel.None)
                        {
                            StaticLogger.Log($"RE: --{t.Name} :: {FriendlyTypeName(type)}", StaticLogger.LoggingLevel.Call);
                        }

                        typesToReturn.Add(type);
                    }
                    else
                    {
                        if (StaticLogger.Level != StaticLogger.LoggingLevel.None)
                        {
                            StaticLogger.Log($"RE: --{t.Name} !: {FriendlyTypeName(type)}", StaticLogger.LoggingLevel.Call);
                        }
                    }
                }

                DerivedTypes.TryAdd(t, typesToReturn.ToList());

                StaticLogger.Log("RE: Finished type search", StaticLogger.LoggingLevel.Method);

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
            if (t is null)
            {
                throw new ArgumentNullException(nameof(t));
            }

            return GetMostDerivedType(GetDerivedTypes(t).ToList(), t);
        }

        /// <summary>
        /// Gets the most derived type matching the base type, from a custom list of types
        /// </summary>
        /// <param name="types">The list of types to check</param>
        /// <param name="t">The base type to check for</param>
        /// <returns>The most derived type out of the list</returns>
        public static Type GetMostDerivedType(IEnumerable<Type> types, Type t)
        {
            if (types is null)
            {
                throw new ArgumentNullException(nameof(types));
            }

            return GetMostDerivedType(types.ToList(), t);
        }

        /// <summary>
        /// Gets the most derived type matching the base type, from a custom list of types
        /// </summary>
        /// <param name="types">The list of types to check</param>
        /// <param name="t">The base type to check for</param>
        /// <returns>The most derived type out of the list</returns>
        public static Type GetMostDerivedType(List<Type> types, Type t)
        {
            if (types is null)
            {
                throw new ArgumentNullException(nameof(types));
            }

            if (t is null)
            {
                throw new ArgumentNullException(nameof(t));
            }

            List<Type> toProcess = types.Where(tt => t.IsAssignableFrom(tt)).ToList();

            foreach (Type toCheckA in toProcess.ToList())
            {
                foreach (Type toCheckB in toProcess.ToList())
                {
                    if (toCheckA != toCheckB && toCheckA.IsAssignableFrom(toCheckB))
                    {
                        toProcess.Remove(toCheckA);
                        break;
                    }
                }
            }

            if (toProcess.Count > 1)
            {
                throw new Exception($"More than one terminating type found for base {t.FullName}");
            }

            return toProcess.FirstOrDefault() ?? t;
        }

        /// <summary>
        /// Gets the properties of the type
        /// </summary>
        /// <param name="t">The type to get the properies of</param>
        /// <returns>All of the properties. All of them.</returns>
        public static PropertyInfo[] GetProperties(Type t)
        {
            return TypeCache.GetProperties(t);
        }

        /// <summary>
        /// Gets all the properties of the object
        /// </summary>
        /// <param name="o">The object to get the properties of</param>
        /// <returns>All of the properties. All of them.</returns>
        public static PropertyInfo[] GetProperties(object o)
        {
            return GetProperties(GetType(o));
        }

        /// <summary>
        /// Gets all assemblies that are referenced recursively by the assembly containing the given type
        /// </summary>
        /// <param name="t">A type in the root assembly to search for </param>
        /// <returns>all assemblies that are referenced recursively by the assembly containing the given type</returns>
        public static IEnumerable<Assembly> GetReferencedAssemblies(Type t)
        {
            Contract.Assert(t != null);
            Assembly root = t.Assembly;

            foreach (Assembly a in GetDependentAssemblies(root))
            {
                yield return a;
            }
        }

        /// <summary>
        /// Gets all assemblies that are referenced recursively by the assembly one
        /// </summary>
        /// <param name="a">The root assembly to search for </param>
        /// <returns>all assemblies that are referenced recursively by the assembly one</returns>
        public static IEnumerable<Assembly> GetReferencedAssemblies(Assembly a)
        {
            Contract.Assert(a != null);
            return GetReferencedAssemblies(a, new HashSet<AssemblyName>());
        }

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

                matching = matching.Where(t => string.IsNullOrEmpty(targetNamespace) || targetNamespace == t.Namespace).Distinct().ToList();

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
        public static bool HasAttribute(MemberInfo toCheck, Type attribute)
        {
            return TypeCache.HasAttribute(toCheck, attribute);
        }

        /// <summary>
        /// Checks if an object has an attribute declared on its type
        /// </summary>
        /// <param name="o">The object to check</param>
        /// <param name="attribute">The attribute to check for</param>
        /// <returns>Whether or not the attribute is declared on the object type</returns>
        public static bool HasAttribute(object o, Type attribute)
        {
            return HasAttribute(GetType(o), attribute);
        }

        /// <summary>
        /// Retrieves the first matching attribute of the specified type
        /// </summary>
        /// <typeparam name="T">The base type to find</typeparam>
        /// <param name="toCheck">The member to check</param>
        /// <returns>The first matching attribute</returns>
        public static T RetrieveAttribute<T>(MemberInfo toCheck) where T : Attribute
        {
            return toCheck.GetCustomAttribute<T>();
        }

        /// <summary>
        /// Gets a list of all custom attributes on the member
        /// </summary>
        /// <typeparam name="T">The base type of the attributes to get</typeparam>
        /// <param name="toCheck">The member to retrieve the information for</param>
        /// <returns>all custom attributes</returns>
        public static List<T> RetrieveAttributes<T>(MemberInfo toCheck) where T : Attribute
        {
            return toCheck.GetCustomAttributes<T>().ToList();
        }

        internal const string BLACKLIST_CACHE = "TypeFactory.BlackList.Cache";
        internal const string FAILED_CACHE = "TypeFactory.Failed.Cache";

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "<Pending>")]
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

                return blacklist.Where(s => s.Length > 0 && s[0] != '#').ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        internal static List<string> LoadFailedCache()
        {
            List<string> failedCache = new List<string>();

            if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FAILED_CACHE)))
            {
                failedCache = File.ReadAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FAILED_CACHE)).ToList();
            }

            return failedCache;
        }

        private static readonly HashSet<string> CurrentlyLoadedAssemblies = new HashSet<string>();
        private static readonly ConcurrentDictionary<string, Assembly> AssembliesByName = new ConcurrentDictionary<string, Assembly>();
        private static readonly ConcurrentDictionary<string, List<Assembly>> AssembliesThatReference = new ConcurrentDictionary<string, List<Assembly>>();
        private static readonly ConcurrentDictionary<string, AssemblyDefinition> AssemblyTypes = new ConcurrentDictionary<string, AssemblyDefinition>();
        private static readonly ConcurrentDictionary<Type, ICollection<Type>> DerivedTypes = new ConcurrentDictionary<Type, ICollection<Type>>();
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Type>> TypeMapping = new ConcurrentDictionary<string, ConcurrentDictionary<string, Type>>();

        private static void AddReferenceInformation(Assembly a)
        {
            foreach (AssemblyName ani in a.GetReferencedAssemblies())
            {
                string AniName = ani.Name;
                if (AssembliesThatReference.TryGetValue(AniName, out List<Assembly> matches))
                {
                    matches.Add(a);
                }
                else
                {
                    AssembliesThatReference.TryAdd(AniName, new List<Assembly> { a });
                }
            }
        }

        private static string FriendlyTypeName(Type t)
        {
            AssemblyName an = t.Assembly.GetName();

            return $"{t.FullName} [{an.Name} v{an.Version}]";
        }

        private static IEnumerable<Assembly> GetDependentAssemblies(Assembly a, HashSet<Assembly> checkedAssemblies, string Prefix = "")
        {
            yield return a;

            if (AssembliesThatReference.TryGetValue(a.GetName().Name, out List<Assembly> referencedBy))
            {
                foreach (Assembly ai in referencedBy)
                {
                    if (checkedAssemblies.Contains(ai))
                    {
                        continue;
                    }

                    checkedAssemblies.Add(ai);

                    foreach (Assembly aii in GetDependentAssemblies(ai, checkedAssemblies, "----" + Prefix))
                    {
                        if (StaticLogger.IsListening)
                        {
                            StaticLogger.Log($"{Prefix} Dependency {aii.GetName().Name}", StaticLogger.LoggingLevel.Call);
                        }

                        yield return aii;
                    }
                }
            }
        }

        private static IEnumerable<Assembly> GetReferencedAssemblies(Assembly a, HashSet<AssemblyName> checkedNames)
        {
            yield return a;

            foreach (AssemblyName an in a.GetReferencedAssemblies())
            {
                if (checkedNames.Contains(an))
                {
                    continue;
                }

                checkedNames.Add(an);

                foreach (Assembly ai in GetReferencedAssemblies(GetAssemblyByName(an), checkedNames))
                {
                    yield return ai;
                }
            }
        }

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

        private static void CheckLoadingPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (!CurrentlyLoadedAssemblies.Contains(path))
            {
                CurrentlyLoadedAssemblies.Add(path);
            }
            else
            {
                try
                {
                    throw new Exception($"The assembly found at {path} is being loaded, however it appears to have already been loaded. Loading the same assembly more than once causes type resolution issues and is a fatal error");
                }
                catch (Exception)
                {
                }
            }
        }

        private static Assembly LoadAssembly(string path, AssemblyName an, bool skipDuplicateCheck = false)
        {
            if (!skipDuplicateCheck)
            {
                CheckLoadingPath(path);
            }
#if NET48
            return AppDomain.CurrentDomain.Load(an);
#else
            Debug.WriteLine(an?.ToString());
            return System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
#endif
        }
    }
}