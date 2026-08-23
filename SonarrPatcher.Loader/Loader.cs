using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using SonarrPatcher.Common;

namespace SonarrPatcher
{
    public static class Loader
    {
        private static readonly Logger Log = new Logger("SonarrPatcher.Loader");

        public static void LoadAll()
        {
            LogSink.ClaimCanonical();

            var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            LoadFrom(directory);
        }

        public static IReadOnlyList<Assembly> LoadFrom(string directory)
        {
            var loaded = new List<Assembly>();
            var selfName = Assembly.GetExecutingAssembly().GetName().Name + ".dll";

            SonarrDependencyLoader.EnsureLoaded("0Harmony.dll", "Sonarr.Common.dll");

            foreach (var path in Directory.EnumerateFiles(directory, "*.dll")
                .Where(p => Path.GetFileName(p).StartsWith("SonarrPatcher", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileName(path);

                if (fileName.Equals(selfName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var name = AssemblyName.GetAssemblyName(path);
                    if (AssemblyLoadContext.Default.Assemblies.Any(a => a.GetName().FullName == name.FullName))
                    {
                        continue;
                    }

                    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                    loaded.Add(assembly);

                    var hookType = assembly.GetType("StartupHook");
                    var initializeForLoader = hookType?.GetMethod("InitializeForLoader", BindingFlags.Public | BindingFlags.Static);
                    if (initializeForLoader == null)
                    {
                        Log.Warn(fileName + " has no StartupHook.InitializeForLoader, skipped");
                        continue;
                    }

                    initializeForLoader.Invoke(null, null);
                    Log.Info("Loaded " + fileName);
                }
                catch (Exception ex)
                {
                    Log.Error("Failed to load " + fileName + ": " + ex);
                }
            }

            return loaded;
        }
    }
}
