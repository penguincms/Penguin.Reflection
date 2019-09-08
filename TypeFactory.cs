using Penguin.Debugging;
using Penguin.Reflection.Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace Penguin.Reflection
{
    /// <summary>
    /// A static class used to perform all kinds of type based reflections over a whitelist of assemblies for finding and resolving many kinds of queries
    /// </summary>
    public static class TypeFactory
    {
        static ConcurrentDictionary<string, Assembly> AssembliesByFullName { get; set; } = new ConcurrentDictionary<string, Assembly>();
        static ConcurrentDictionary<string, List<Assembly>> AssembliesThatReference { get; set; } = new ConcurrentDictionary<string, List<Assembly>>();
        /// <summary>
        /// Since everything is cached, we need to make sure ALL potential assemblies are loaded or we might end up missing classes because
        /// the assembly hasn't been loaded yet. Consider only loading whitelisted references if this is slow
        /// </summary>
        static TypeFactory()
        {
            StaticLogger.Log($"Penguin.Reflection: {Assembly.GetExecutingAssembly().GetName().Version}", StaticLogger.LoggingLevel.Call);

            List<string> failedCache = LoadFailedCache();
            List<string> blacklist = LoadBlacklistCache();
            Dictionary<string, Assembly> loadedPaths = new Dictionary<string, Assembly>();

            //Map out the loaded assemblies so we can find them by path
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if(a.IsDynamic)
                {
                    if(!loadedPaths.ContainsKey(a.Location))
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
                    if (!loadedPaths.TryGetValue(loadPath, out Assembly a)) {
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
                            a = LoadAssembly(loadPath, an);
                            AssembliesByFullName.TryAdd(an.FullName, a);
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
            foreach(KeyValuePair<string, Assembly> kvp in loadedPaths)
            {
                if(!SearchedPaths.Contains(kvp.Key))
                {
                    AddReferenceInformation(kvp.Value);
                }
            }

            StaticLogger.Log($"RE: {nameof(TypeFactory)} static initialization completed", StaticLogger.LoggingLevel.Final);

            File.WriteAllLines(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FAILED_CACHE), failedCache);
        }

        private static void AddReferenceInformation(Assembly a)
        {
            foreach (AssemblyName ani in a.GetReferencedAssemblies())
            {
                string AniName = ani.FullName;
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

        private static Assembly LoadNetCoreAssembly(string path)
        {
            return System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        }
        private static Assembly LoadNetFrameworkAssembly(AssemblyName an)
        {
            return AppDomain.CurrentDomain.Load(an);
        }

        private static bool? isNetFramework { get; set; }

        /// <summary>
        /// Returns true if the executing application is .NetFramework opposed to Core or Standard (or other)
        /// </summary>
        public static bool IsNetFramework { get
            {
                if(!isNetFramework.HasValue)
                {
                    isNetFramework = Assembly
                                    .GetEntryAssembly()?
                                    .GetCustomAttribute<TargetFrameworkAttribute>()?
                                    .FrameworkName
                                    .StartsWith(".NETFramework");
                }

                return isNetFramework.Value;
            }
        }
        private static Assembly LoadAssembly(string path, AssemblyName an)
        {

            string framework = Assembly
                .GetEntryAssembly()?
                .GetCustomAttribute<TargetFrameworkAttribute>()?
                .FrameworkName;

            if (IsNetFramework)
            {
                return LoadNetFrameworkAssembly(an);
            }
            else
            {
                return LoadNetCoreAssembly(path);
            }
        }
        /// <summary>
        /// Gets an assembly by its AssemblyName
        /// </summary>
        /// <param name="Name">The AssemblyName</param>
        /// <returns>The matching Assembly</returns>
        public static Assembly GetAssemblyByName(AssemblyName Name) => GetAssemblyByName(Name.FullName);

        /// <summary>
        /// Gets an assembly by its AssemblyName.FullName
        /// </summary>
        /// <param name="FullName">The AssemblyName.FullName</param>
        /// <returns>The matching Assembly</returns>
        public static Assembly GetAssemblyByName(string FullName)
        {
            if (!AssembliesByFullName.TryGetValue(FullName, out Assembly a))
            {
                a = AppDomain.CurrentDomain.GetAssemblies().First(aa => aa.GetName().FullName == FullName);
                AssembliesByFullName.TryAdd(FullName, a);
            }

            return a;
        }

        /// <summary>
        /// Gets all assemblies that are referenced recursively by the assembly containing the given type
        /// </summary>
        /// <param name="t">A type in the root assembly to search for </param>
        /// <returns>all assemblies that are referenced recursively by the assembly containing the given type</returns>
        public static IEnumerable<Assembly> GetReferencedAssemblies(Type t)
        {
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
        public static IEnumerable<Assembly> GetReferencedAssemblies(Assembly a) => GetReferencedAssemblies(a, new HashSet<AssemblyName>());

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

        /// <summary>
        /// Gets all assemblies that recursively reference the one containing the given type
        /// </summary>
        /// <param name="t">A type in the root assembly to search for </param>
        /// <returns>all assemblies that recursively reference the one containing the given type</returns>
        public static IEnumerable<Assembly> GetDependentAssemblies(Type t)
        {
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
        public static IEnumerable<Assembly> GetDependentAssemblies(Assembly a) => GetDependentAssemblies(a, new HashSet<Assembly>());

        private static IEnumerable<Assembly> GetDependentAssemblies(Assembly a, HashSet<Assembly> checkedAssemblies)
        {
            yield return a;

            if (AssembliesThatReference.TryGetValue(a.GetName().FullName, out List<Assembly> referencedBy))
            {
                foreach (Assembly ai in referencedBy)
                {
                    if (checkedAssemblies.Contains(ai))
                    {
                        continue;
                    }

                    checkedAssemblies.Add(ai);

                    foreach (Assembly aii in GetDependentAssemblies(ai, checkedAssemblies))
                    {
                        yield return aii;
                    }
                }
            }
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

        /// <summary>
        /// Gets all types in whitelisted assemblies that implement a given interface
        /// </summary>
        /// <typeparam name="T">The interface to check for</typeparam>
        /// <returns>All of the aforementioned types</returns>
        public static IEnumerable<Type> GetAllImplementations<T>() => GetAllImplementations(typeof(T));

        /// <summary>
        /// Gets all types in whitelisted assemblies that implement a given interface
        /// </summary>
        /// <param name="InterfaceType">The interface to check for</param>
        /// <returns>All of the aforementioned types</returns>
        public static IEnumerable<Type> GetAllImplementations(Type InterfaceType)
        {
            return GetDependentAssemblies(InterfaceType).GetAllTypes().Where(p => InterfaceType.IsAssignableFrom(p) && !p.IsAbstract).Distinct();
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
        /// Gets all types in the specified assembly (where not compiler generated)
        /// </summary>
        /// <param name="a">The assembly to check</param>
        /// <returns>All the types in the assembly</returns>
        public static IEnumerable<Type> GetAssemblyTypes(Assembly a)
        {
            if (!AssemblyTypes.ContainsKey(a.FullName))
            {
                StaticLogger.Log($"RE: Getting types for assembly {a.FullName}", StaticLogger.LoggingLevel.Call);

                List<Type> types = null;

                try
                {
                    types = a.GetTypes().Where(t => !Attribute.IsDefined(t, typeof(CompilerGeneratedAttribute), true)).ToList();
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

                AssemblyTypes.TryAdd(a.FullName, types);

                return types.ToList();
            }
            else
            {
                StaticLogger.Log($"RE: Using cached types for {a.FullName}", StaticLogger.LoggingLevel.Call);
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
        public static PropertyInfo[] GetProperties(Type t) => Cache.GetProperties(t);

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

        private static string FriendlyTypeName(Type t)
        {
            AssemblyName an = t.Assembly.GetName();

            return $"{t.FullName} [{an.Name} v{an.Version}]";
        }

        internal const string BLACKLIST_CACHE = "TypeFactory.BlackList.Cache";
        internal const string FAILED_CACHE = "TypeFactory.Failed.Cache";

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