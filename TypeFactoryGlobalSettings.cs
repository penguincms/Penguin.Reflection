namespace Penguin.Reflection
{
    /// <summary>
    /// Global settings for before the TypeFactory static constructor is called
    /// </summary>
    public static class TypeFactoryGlobalSettings
    {
        /// <summary>
        /// If true, doesn't attempt to skip assemblies for which the previous load has failed (Default false)
        /// </summary>
        public static bool DisableFailedLoadSkip { get; set; }
    }
}