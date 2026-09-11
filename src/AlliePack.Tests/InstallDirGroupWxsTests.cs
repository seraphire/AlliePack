using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using WixSharp;
using Xunit;
using AlliePack;

namespace AlliePack.Tests
{
    /// <summary>
    /// Serialization-level coverage for [INSTALLDIR] groups: the object graph produced by
    /// <see cref="InstallerBuilder.BuildGroupDir"/> is handed to WixSharp and the emitted
    /// WXS is asserted.  The object-graph tests in InstallDirGroupBuildTests cannot catch a
    /// WixSharp serialization regression -- notably a reused directory emitting two
    /// Directory elements, or component flags being dropped on the way out.
    /// <para>
    /// <c>Compiler.BuildWxs</c> produces the XML only; it does not invoke wix.exe, so these
    /// tests need no WiX toolchain.  Compiling the WXS to an MSI and inspecting its tables
    /// is a separate concern -- see GAP-13.
    /// </para>
    /// </summary>
    public class InstallDirGroupWxsTests : IDisposable
    {
        private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";

        private readonly string _dir;

        public InstallDirGroupWxsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "alliepack-wxs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            System.IO.File.WriteAllText(Path.Combine(_dir, "app.ini"), "[settings]");
            System.IO.File.WriteAllText(Path.Combine(_dir, "logo.png"), "png");
            System.IO.File.WriteAllText(Path.Combine(_dir, "readme.txt"), "readme");
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

        /// <summary>
        /// Builds the install tree the way the real build path does -- a structure: file
        /// already in INSTALLDIR\Help, then two groups attached to it -- and serializes it.
        /// </summary>
        private XDocument BuildWxs()
        {
            var builder = MakeBuilder();

            // Mirror how the build path assembles the install tree: a root chain whose
            // LEAF is the install directory, which is what is handed to BuildGroupDir.
            // Passing the root instead would attach group files a level too high, which
            // is the mistake these tests exist to catch.
            var help = new Dir("Help", new WixSharp.File(Path.Combine(_dir, "readme.txt")));
            var install = new Dir("TestApp", help);
            var root = new Dir("%ProgramFiles%", install);

            builder.BuildGroupDir(
                Group("Config", "[INSTALLDIR]", "app.ini", condition: "notExists", permanent: true),
                "[INSTALLDIR]", install, feature: null);

            builder.BuildGroupDir(
                Group("HelpAssets", @"[INSTALLDIR]\Help", "logo.png"),
                @"[INSTALLDIR]\Help", install, feature: null);

            // A plain Project, not a ManagedProject: nothing here needs managed custom
            // actions, and ManagedProject makes WixSharp package its CA assembly, which
            // fails when the test host shadow-copies WixSharp.dll.
            var project = new Project("TestApp", root)
            {
                GUID = new Guid("d29dab91-77c6-455e-924a-99b03300681a"),
                OutDir = _dir,
                OutFileName = "TestApp",
                UI = WUI.WixUI_ProgressOnly,
            };

            Compiler.BuildWxs(project, Compiler.OutputType.MSI);

            return XDocument.Load(Path.Combine(_dir, "TestApp.wxs"));
        }

        private static string FileNameOf(XElement fileElement)
            => Path.GetFileName((string?)fileElement.Attribute("Source") ?? string.Empty);

        [Fact]
        public void ReusedSubfolder_EmitsASingleDirectoryElement()
        {
            var doc = BuildWxs();

            var helpDirs = doc.Descendants(Wix + "Directory")
                              .Where(e => (string?)e.Attribute("Name") == "Help")
                              .ToList();

            Assert.Single(helpDirs);
        }

        [Fact]
        public void ReusedSubfolder_CarriesBothStructureAndGroupFiles()
        {
            var doc = BuildWxs();

            var help = doc.Descendants(Wix + "Directory")
                          .Single(e => (string?)e.Attribute("Name") == "Help");
            var names = help.Descendants(Wix + "File").Select(FileNameOf).ToList();

            Assert.Contains("readme.txt", names);
            Assert.Contains("logo.png", names);
        }

        [Fact]
        public void InstallDirGroupFile_IsEmittedInTheInstallDirectory()
        {
            var doc = BuildWxs();

            var installDir = doc.Descendants(Wix + "Directory")
                                .Single(e => (string?)e.Attribute("Name") == "TestApp");

            // Directly under the install directory, not in a nested one.
            var direct = installDir.Elements(Wix + "Component")
                                   .Elements(Wix + "File")
                                   .Select(FileNameOf)
                                   .ToList();

            Assert.Contains("app.ini", direct);
        }

        [Fact]
        public void InstallDirGroupFile_KeepsComponentFlagsInTheEmittedWxs()
        {
            var doc = BuildWxs();

            var component = doc.Descendants(Wix + "File")
                               .Single(f => FileNameOf(f) == "app.ini")
                               .Parent!;

            Assert.Equal("yes", (string?)component.Attribute("NeverOverwrite"));
            Assert.Equal("yes", (string?)component.Attribute("Permanent"));
        }

        [Fact]
        public void PlainGroupFile_GetsNoComponentFlags()
        {
            var doc = BuildWxs();

            var component = doc.Descendants(Wix + "File")
                               .Single(f => FileNameOf(f) == "logo.png")
                               .Parent!;

            Assert.Null(component.Attribute("NeverOverwrite"));
            Assert.Null(component.Attribute("Permanent"));
        }

        [Fact]
        public void EveryEmittedComponent_IsReferencedByAFeature()
        {
            var doc = BuildWxs();

            var declared = doc.Descendants(Wix + "Component")
                              .Select(c => (string?)c.Attribute("Id"))
                              .Where(id => id != null)
                              .ToHashSet();
            var referenced = doc.Descendants(Wix + "ComponentRef")
                                .Select(c => (string?)c.Attribute("Id"))
                                .Where(id => id != null)
                                .ToHashSet();

            Assert.Empty(declared.Except(referenced));
        }
    }
}
