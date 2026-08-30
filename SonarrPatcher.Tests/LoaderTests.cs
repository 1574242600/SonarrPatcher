using System;
using System.IO;
using System.Linq;
using SonarrPatcher;
using Xunit;

namespace SonarrPatcher.Tests
{
    public class LoaderTests : IDisposable
    {
        private readonly string _tempDir;

        public LoaderTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "SonarrPatcher.LoaderTests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_tempDir, true);
            }
            catch
            {
            }
        }

        private static string BaseDir => AppContext.BaseDirectory;

        [Fact]
        public void LoadFrom_LoadsSonarrPatcherDllAndRunsStartupHook()
        {
            File.Copy(Path.Combine(BaseDir, "SonarrPatcher.TestTarget.dll"), Path.Combine(_tempDir, "SonarrPatcher.TestTarget.dll"), true);
            File.Copy(Path.Combine(BaseDir, "SonarrPatcher.Loader.dll"), Path.Combine(_tempDir, "SonarrPatcher.Loader.dll"), true);

            var assemblies = Loader.LoadFrom(_tempDir);

            var target = assemblies.FirstOrDefault(a => a.GetName().Name == "SonarrPatcher.TestTarget");
            Assert.NotNull(target);
            var invoked = (bool)target.GetType("StartupHook").GetField("Invoked").GetValue(null);
            Assert.True(invoked);
            Assert.DoesNotContain(assemblies, a => a.GetName().Name == "SonarrPatcher.Loader");
        }

        [Fact]
        public void LoadFrom_IgnoresNonSonarrPatcherDlls()
        {
            File.WriteAllText(Path.Combine(_tempDir, "Unrelated.dll"), "not a managed assembly");

            var assemblies = Loader.LoadFrom(_tempDir);

            Assert.Empty(assemblies);
        }

        [Fact]
        public void LoadFrom_EmptyDirectory_ReturnsEmpty()
        {
            var assemblies = Loader.LoadFrom(_tempDir);

            Assert.Empty(assemblies);
        }
    }
}
