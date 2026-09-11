using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using AlliePack;

namespace AlliePack.Tests
{
    /// <summary>
    /// End-to-end coverage of the [INSTALLDIR] branch in <see cref="InstallerBuilder.BuildGroupDir"/>:
    /// files are attached to the existing install directory rather than a second root,
    /// component flags and feature assignment survive, subfolders nest, a directory that
    /// structure: already created is reused, and external destinations still build their
    /// own root.  The helper-level tests in GroupDestinationTests cannot catch a
    /// regression in this path.
    /// </summary>
    public class InstallDirGroupBuildTests : IDisposable
    {
        private readonly string _dir;

        public InstallDirGroupBuildTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "alliepack-installdir-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            System.IO.File.WriteAllText(Path.Combine(_dir, "app.ini"), "[settings]");
            System.IO.File.WriteAllText(Path.Combine(_dir, "logo.png"), "png");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private InstallerBuilder MakeBuilder()
        {
            var resolver = new PathResolver(
                Path.Combine(_dir, "test.yaml"),
                new Dictionary<string, string>(),
                new Dictionary<string, string>());
            return new InstallerBuilder(
                new AlliePackConfig(),
                resolver,
                new SolutionResolver(resolver),
                new Options(),
                new List<string>());
        }

        private static FileGroupConfig Group(
            string id, string destination, string source,
            string? condition = null, bool permanent = false)
            => new FileGroupConfig
            {
                Id = id,
                DestinationDir = destination,
                Condition = condition,
                Permanent = permanent,
                Files = new List<FileGroupItem> { new FileGroupItem { Source = source } },
            };

        private static string NameOf(WixSharp.File f) => Path.GetFileName(f.Name);

        [Fact]
        public void InstallDirGroup_AttachesToInstallDir_AndReturnsNoNewRoot()
        {
            var install = new WixSharp.Dir("INSTALLDIR");

            var root = MakeBuilder().BuildGroupDir(
                Group("Config", "[INSTALLDIR]", "app.ini"), "[INSTALLDIR]", install, feature: null);

            // Null means "attached in place", not "no files" -- the caller adds nothing.
            Assert.Null(root);
            Assert.Single(install.Files);
            Assert.Equal("app.ini", NameOf(install.Files[0]));
            Assert.Empty(install.Dirs);
        }

        [Fact]
        public void InstallDirGroup_KeepsComponentFlags()
        {
            var install = new WixSharp.Dir("INSTALLDIR");

            MakeBuilder().BuildGroupDir(
                Group("Config", "[INSTALLDIR]", "app.ini", condition: "notExists", permanent: true),
                "[INSTALLDIR]", install, feature: null);

            string attrs = install.Files[0].AttributesDefinition;
            Assert.Contains("Component:NeverOverwrite=yes", attrs);
            Assert.Contains("Component:Permanent=yes", attrs);
        }

        [Fact]
        public void InstallDirGroup_WithoutFlags_LeavesComponentPlain()
        {
            var install = new WixSharp.Dir("INSTALLDIR");

            MakeBuilder().BuildGroupDir(
                Group("Assets", "[INSTALLDIR]", "logo.png"), "[INSTALLDIR]", install, feature: null);

            Assert.True(string.IsNullOrEmpty(install.Files[0].AttributesDefinition));
        }

        [Fact]
        public void InstallDirGroup_CarriesFeatureAssignment()
        {
            var install = new WixSharp.Dir("INSTALLDIR");
            var feature = new WixSharp.Feature("Extras");

            MakeBuilder().BuildGroupDir(
                Group("Assets", "[INSTALLDIR]", "logo.png"), "[INSTALLDIR]", install, feature);

            Assert.Contains(feature, install.Files[0].ActualFeatures);
        }

        [Fact]
        public void InstallDirSubfolder_NestsUnderInstallDir()
        {
            var install = new WixSharp.Dir("INSTALLDIR");

            var root = MakeBuilder().BuildGroupDir(
                Group("Help", @"[INSTALLDIR]\Help", "logo.png"),
                @"[INSTALLDIR]\Help", install, feature: null);

            Assert.Null(root);
            Assert.Empty(install.Files);
            Assert.Single(install.Dirs);
            Assert.Equal("Help", install.Dirs[0].Name);
            Assert.Single(install.Dirs[0].Files);
            Assert.Equal("logo.png", NameOf(install.Dirs[0].Files[0]));
        }

        [Fact]
        public void InstallDirSubfolder_ReusesDirectoryStructureAlreadyCreated()
        {
            // Stands in for a structure: entry that already placed a file in INSTALLDIR\Help.
            var structureFile = new WixSharp.File(Path.Combine(_dir, "app.ini"));
            var help = new WixSharp.Dir("Help") { Files = new[] { structureFile } };
            var install = new WixSharp.Dir("INSTALLDIR") { Dirs = new[] { help } };

            MakeBuilder().BuildGroupDir(
                Group("Help", @"[INSTALLDIR]\Help", "logo.png"),
                @"[INSTALLDIR]\help", install, feature: null);

            Assert.Single(install.Dirs);
            Assert.Same(help, install.Dirs[0]);
            Assert.Equal(2, help.Files.Length);
            Assert.Contains(help.Files, f => NameOf(f) == "app.ini");
            Assert.Contains(help.Files, f => NameOf(f) == "logo.png");
        }

        [Fact]
        public void ExternalDestination_StillBuildsItsOwnRoot_AndLeavesInstallDirAlone()
        {
            var install = new WixSharp.Dir("INSTALLDIR");

            var root = MakeBuilder().BuildGroupDir(
                Group("Config", @"[CommonAppDataFolder]\Acme", "app.ini"),
                @"[CommonAppDataFolder]\Acme", install, feature: null);

            Assert.NotNull(root);
            Assert.Equal("[CommonAppDataFolder]", root!.Name);
            Assert.Equal("Acme", root.Dirs[0].Name);
            Assert.Empty(install.Files);
            Assert.Empty(install.Dirs);
        }

        [Fact]
        public void InstallDirGroup_WithNoInstallDir_WarnsInsteadOfThrowing()
        {
            var root = MakeBuilder().BuildGroupDir(
                Group("Config", "[INSTALLDIR]", "app.ini"), "[INSTALLDIR]", installDir: null, feature: null);

            Assert.Null(root);
        }

        [Theory]
        [InlineData("[INSTALLDIR]", true)]
        [InlineData(@"[INSTALLDIR]\Help", true)]
        [InlineData(@"[installdir]\Help", true)]
        [InlineData(@"[CommonAppDataFolder]\Acme", false)]
        [InlineData("", false)]
        public void InstallDirDestination_IsRecognisedForReporting(string destPath, bool expected)
        {
            Assert.Equal(expected, InstallerBuilder.IsInstallDirDestination(destPath));
        }
    }
}
