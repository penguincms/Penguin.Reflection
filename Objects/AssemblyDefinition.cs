using System;
using System.Collections.Generic;
using System.Reflection;

namespace Penguin.Reflection.Objects
{
    internal class AssemblyDefinition
    {
        public Assembly ContainingAssembly { get; set; }

        public List<Type> LoadedTypes { get; set; }
    }
}