using System;
using System.IO;
using System.Reflection;

namespace SonarrPatcher.Common
{
    /// <summary>
    /// Resolves paths relative to the hook assembly's deployed directory.
    /// <para>
    /// The loader always loads hook assemblies from disk
    /// (<see cref="AssemblyLoadContext.LoadFromAssemblyPath"/>), so
    /// <see cref="Assembly.Location"/> is the deployed path; the
    /// <see cref="AppContext.BaseDirectory"/> fallback only guards against exotic
    /// loading scenarios where <c>Location</c> is empty.
    /// </para>
    /// <para>
    /// Common sources are compile-linked into every hook assembly, so
    /// <see cref="Assembly.GetExecutingAssembly"/> here is the calling patch/loader
    /// assembly itself and the directory is exactly where the patch DLL is deployed.
    /// </para>
    /// </summary>
    internal static class Paths
    {
        /// <summary>
        /// Directory containing the hook assembly (e.g. the deployed patch DLL).
        /// </summary>
        public static string Directory { get; } = ComputeDirectory();

        /// <summary>
        /// Returns <see cref="Directory"/> when no paths are given, otherwise resolves
        /// the given relative paths under <see cref="Directory"/>.
        /// </summary>
        public static string Resolve(params string[] paths)
        {
            var all = new string[paths.Length + 1];
            all[0] = Directory;
            Array.Copy(paths, 0, all, 1, paths.Length);
            return Path.Combine(all);
        }

        private static string ComputeDirectory()
        {
            var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            return string.IsNullOrWhiteSpace(directory) ? AppContext.BaseDirectory : directory;
        }
    }
}
