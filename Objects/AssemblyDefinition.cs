using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace Penguin.Reflection.Objects
{
    internal class AssemblyDefinition
    {
        public List<Type> LoadedTypes { get; set; }
        public Assembly ContainingAssembly { get; set; }
    }
}
