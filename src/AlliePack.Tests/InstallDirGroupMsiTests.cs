using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using MsiInspector;
using WixSharp;
using Xunit;
using AlliePack;

namespace AlliePack.Tests
{
    /// <summary>
    /// MSI-backed integration coverage for [INSTALLDIR] groups (GAP-13): the WXS is
    /// compiled with wix.exe and the File, Component and Directory tables of the resulting
    /// package are inspected with <see cref="Inspector"/>.
    /// <para>
    /// This is the level the object-graph and WXS tests cannot reach.  A defect that shows
    /// up only in the finished package -- a duplicate Directory row, a component landing
    /// under the wrong directory key, flags lost between WXS attributes and the bitfield in
    /// the Component table -- passes both of those suites and fails here.
    /// </para>
    /// <para>Skipped when wix.exe is not on PATH; see <see cref="WixRequiredFactAttribute"/>.</para>
    /// </summary>
    public class InstallDirGroupMsiTests : IDisposable
    {
        // Component table attribute bits (msidbComponentAttributes*).
        private const int Permanent      = 16;
        private const int NeverOverwrite = 128;

        private readonly string _dir;

        public InstallDirGroupMsiTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "alliepack-msi-" + Guid.NewGuid().ToString("N"));
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
        /// Assembles the install tree the way the build path does, attaches an
        /// [INSTALLDIR] group and an [INSTALLDIR]\Help group to it, emits the WXS and
        /// compiles it to an MSI.  Returns the MSI path.
        /// </summary>
        private string BuildMsi()
        {
            var builder = MakeBuilder();

            // Stands in for structure: content already placed in INSTALLDIR\Help.
            var help = new Dir("Help", new WixSharp.File(Path.Combine(_dir, "readme.txt")));
            var install = new Dir("TestApp", help);
            var root = new Dir("%ProgramFiles%", install);

            builder.BuildGroupDir(
                Group("Config", "[INSTALLDIR]", "app.ini", condition: "notExists", permanent: true),
                "[INSTALLDIR]", install, feature: null);

            builder.BuildGroupDir(
                Group("HelpAssets", @"[INSTALLDIR]\Help", "logo.png"),
                @"[INSTALLDIR]\Help", install, feature: null);

            var project = new Project("TestApp", root)
            {
                GUID = new Guid("d29dab91-77c6-455e-924a-99b03300681a"),
                OutDir = _dir,
                OutFileName = "TestApp",
                UI = WUI.WixUI_ProgressOnly,
            };

            // Drop the dialog set.  It would make the compile depend on
            // WixToolset.UI.wixext being registered on the machine, and this test is about
            // the directory and component tables, not the UI.
            project.WixSourceGenerated += doc =>
            {
                XNamespace ui = "http://wixtoolset.org/schemas/v4/wxs/ui";
                doc.Descendants(ui + "WixUI").Remove();
                doc.Descendants().Where(e => e.Name.LocalName == "UIRef").Remove();
            };

            Compiler.BuildWxs(project, Compiler.OutputType.MSI);

            string wxs = Path.Combine(_dir, "TestApp.wxs");
            string msi = Path.Combine(_dir, "TestApp.msi");

            // BuildWxs writes Source paths relative to the current directory, so wix must
            // run from there too.
            var psi = new ProcessStartInfo(WixTool.Path!)
            {
                Arguments = "build \"" + wxs + "\" -arch x86 -sw1044 -sw5437 -o \"" + msi + "\"",
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using (var p = Process.Start(psi)!)
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.True(p.ExitCode == 0,
                    "wix build failed (" + p.ExitCode + "):" + Environment.NewLine + stdout + Environment.NewLine + stderr);
            }

            return msi;
        }

        /// <summary>Directory key of the install directory, found by its DefaultDir name.</summary>
        private static string InstallDirKey(Inspector inspector)
            => inspector.Directories.Single(d => d.DefaultDir.Contains("TestApp")).DirectoryKey;

        private static string HelpDirKey(Inspector inspector)
            => inspector.Directories.Single(d => d.DefaultDir.Contains("Help")).DirectoryKey;

        [WixRequiredFact]
        public void InstallDirGroupFile_LandsInTheInstallDirectory()
        {
            using var inspector = new Inspector(BuildMsi());

            var file = inspector.Files.Single(f => f.FileName.EndsWith("app.ini", StringComparison.OrdinalIgnoreCase));
            var component = inspector.Components.Single(c => c.ComponentKey == file.ComponentKey);

            Assert.Equal(InstallDirKey(inspector), component.DirectoryKey);
        }

        [WixRequiredFact]
        public void InstallDirGroupFile_KeepsNeverOverwriteAndPermanentInTheComponentTable()
        {
            using var inspector = new Inspector(BuildMsi());

            var file = inspector.Files.Single(f => f.FileName.EndsWith("app.ini", StringComparison.OrdinalIgnoreCase));
            var component = inspector.Components.Single(c => c.ComponentKey == file.ComponentKey);

            Assert.Equal(NeverOverwrite, component.Attributes & NeverOverwrite);
            Assert.Equal(Permanent, component.Attributes & Permanent);
        }

        [WixRequiredFact]
        public void PlainGroupFile_GetsNeitherFlag()
        {
            using var inspector = new Inspector(BuildMsi());

            var file = inspector.Files.Single(f => f.FileName.EndsWith("logo.png", StringComparison.OrdinalIgnoreCase));
            var component = inspector.Components.Single(c => c.ComponentKey == file.ComponentKey);

            Assert.Equal(0, component.Attributes & (NeverOverwrite | Permanent));
        }

        [WixRequiredFact]
        public void ReusedSubfolder_ProducesASingleDirectoryRow()
        {
            using var inspector = new Inspector(BuildMsi());

            var helpRows = inspector.Directories.Where(d => d.DefaultDir.Contains("Help")).ToList();

            Assert.Single(helpRows);
        }

        [WixRequiredFact]
        public void ReusedSubfolder_HoldsBothStructureAndGroupFiles()
        {
            using var inspector = new Inspector(BuildMsi());

            var names = inspector.GetFilesIn(HelpDirKey(inspector))
                                 .Select(f => f.FileName)
                                 .ToList();

            Assert.Contains(names, n => n.EndsWith("readme.txt", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(names, n => n.EndsWith("logo.png", StringComparison.OrdinalIgnoreCase));
        }

        [WixRequiredFact]
        public void InstallDirectory_ResolvesUnderProgramFiles()
        {
            using var inspector = new Inspector(BuildMsi());

            string full = inspector.ResolveDirectoryFullPath(InstallDirKey(inspector));

            Assert.Contains("TestApp", full);
        }
    }
}
