using System.IO;
using SonarrPatcher.Common;
using Xunit;

namespace SonarrPatcher.Tests
{
    public class PathsTests
    {
        [Fact]
        public void Directory_IsCallingAssemblyDirectory()
        {
            Assert.Equal(
                Path.GetDirectoryName(typeof(PathsTests).Assembly.Location),
                Paths.Directory);
        }

        [Fact]
        public void Resolve_NoArgs_ReturnsPatchDirectory()
        {
            Assert.Equal(Paths.Directory, Paths.Resolve());
        }

        [Fact]
        public void Resolve_RelativePaths_AppendToPatchDirectory()
        {
            Assert.Equal(
                Path.Combine(Paths.Directory, "config", "anirss.subscribe.json"),
                Paths.Resolve("config", "anirss.subscribe.json"));
        }
    }
}
