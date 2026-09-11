using System.Collections.Generic;
using System.Linq;
using WixSharp;
using Xunit;
using AlliePack;

namespace AlliePack.Tests
{
    /// <summary>
    /// Tests for group destinations written as bracketed WiX paths, and for the
    /// [INSTALLDIR] anchoring that lets a group place files in the install folder
    /// (or a subfolder of it) rather than only outside it.
    /// </summary>
    public class GroupDestinationTests
    {
        private static Dictionary<string, string> NamedDirs()
            => new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                { "CONFIGDIR", "[CommonAppDataFolder]\\Acme" },
            };

        // -------------------------------------------------------------------
        // TryResolveGroupDestination
        // -------------------------------------------------------------------

        [Fact]
        public void NamedId_ResolvesThroughDirectoriesBlock()
        {
            Assert.True(InstallerBuilder.TryResolveGroupDestination("CONFIGDIR", NamedDirs(), out var path));
            Assert.Equal("[CommonAppDataFolder]\\Acme", path);
        }

        [Fact]
        public void UnknownId_DoesNotResolve()
        {
            Assert.False(InstallerBuilder.TryResolveGroupDestination("NOSUCHDIR", NamedDirs(), out _));
        }

        [Fact]
        public void EmptyDestination_DoesNotResolve()
        {
            Assert.False(InstallerBuilder.TryResolveGroupDestination("   ", NamedDirs(), out _));
        }

        [Theory]
        [InlineData("[INSTALLDIR]")]
        [InlineData("[INSTALLDIR]\\Help")]
        [InlineData("[CommonAppDataFolder]\\Acme")]
        public void BracketedPath_IsUsedAsWritten_WithoutADirectoriesEntry(string destination)
        {
            Assert.True(InstallerBuilder.TryResolveGroupDestination(destination, NamedDirs(), out var path));
            Assert.Equal(destination, path);
        }

        [Fact]
        public void BracketedPath_IsTrimmed()
        {
            Assert.True(InstallerBuilder.TryResolveGroupDestination("  [INSTALLDIR]  ", NamedDirs(), out var path));
            Assert.Equal("[INSTALLDIR]", path);
        }

        // -------------------------------------------------------------------
        // IsInstallDirToken
        // -------------------------------------------------------------------

        [Theory]
        [InlineData("[INSTALLDIR]", true)]
        [InlineData("[installdir]", true)]
        [InlineData("[InstallDir]", true)]
        [InlineData("INSTALLDIR", false)]
        [InlineData("[CommonAppDataFolder]", false)]
        public void InstallDirToken_MatchesOnlyTheBracketedInstallDir(string segment, bool expected)
        {
            Assert.Equal(expected, InstallerBuilder.IsInstallDirToken(segment));
        }

        // -------------------------------------------------------------------
        // GetOrAddChildDir
        // -------------------------------------------------------------------

        [Fact]
        public void GetOrAddChildDir_CreatesChildWhenAbsent()
        {
            var parent = new Dir("INSTALLDIR");

            var child = InstallerBuilder.GetOrAddChildDir(parent, "Help");

            Assert.Equal("Help", child.Name);
            Assert.Single(parent.Dirs);
            Assert.Same(child, parent.Dirs[0]);
        }

        [Fact]
        public void GetOrAddChildDir_ReusesExistingChild_SoStructureAndGroupShareOneDirectory()
        {
            var existing = new Dir("Help");
            var parent = new Dir("INSTALLDIR") { Dirs = new[] { existing } };

            var child = InstallerBuilder.GetOrAddChildDir(parent, "help");

            Assert.Same(existing, child);
            Assert.Single(parent.Dirs);
        }

        [Fact]
        public void GetOrAddChildDir_AppendsWithoutDroppingSiblings()
        {
            var parent = new Dir("INSTALLDIR") { Dirs = new[] { new Dir("Help") } };

            InstallerBuilder.GetOrAddChildDir(parent, "Templates");

            Assert.Equal(2, parent.Dirs.Length);
            Assert.Contains(parent.Dirs, d => d.Name == "Help");
            Assert.Contains(parent.Dirs, d => d.Name == "Templates");
        }

        [Fact]
        public void GetOrAddChildDir_NestsForMultiSegmentPaths()
        {
            var install = new Dir("INSTALLDIR");

            var help = InstallerBuilder.GetOrAddChildDir(install, "Help");
            var images = InstallerBuilder.GetOrAddChildDir(help, "Images");

            Assert.Single(install.Dirs);
            Assert.Same(help, install.Dirs[0]);
            Assert.Single(help.Dirs);
            Assert.Same(images, help.Dirs[0]);
        }
    }
}
