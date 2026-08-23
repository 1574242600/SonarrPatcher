using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace SonarrPatcher.Common
{
    /// <summary>
    /// Loads assemblies (e.g. 0Harmony.dll, Sonarr.Common.dll) from the
    /// application base directory into the default AssemblyLoadContext, unless
    /// they are already loaded. Used by the standalone startup hooks and by the
    /// Loader so patches do not have to bootstrap dependencies themselves.
    /// </summary>
    internal static class SonarrDependencyLoader
    {
        public static void EnsureLoaded(params string[] fileNames)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;

            foreach (var fileName in fileNames)
            {
                var path = Path.Combine(baseDir, fileName);
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    var name = AssemblyName.GetAssemblyName(path);
                    var loaded = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().FullName == name.FullName);
                    if (!loaded)
                    {
                        AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                    }
                }
                catch
                {
                    // Not a loadable assembly; ignore.
                }
            }
        }
    }
}
