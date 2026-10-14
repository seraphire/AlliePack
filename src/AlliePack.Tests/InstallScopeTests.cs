using System;
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
    /// installScope must reach the WiX Package Scope attribute.  Before this was covered,
    /// AlliePack set it through AttributesDefinition, which WixSharp_wix4 never emitted, so
    /// every package silently defaulted to perMachine and failed for non-admins.
    /// <para>
    /// The mapping tests need nothing; the WXS tests need no WiX toolchain; the MSI tests
    /// are skipped when wix.exe is not on PATH (see <see cref="WixRequiredFactAttribute"/>).
    /// </para>
    /// </summary>
    public class InstallScopeTests : IDisposable
    {
        private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";

        private readonly string _dir;

        public InstallScopeTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "alliepack-scope-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            System.IO.File.WriteAllText(Path.Combine(_dir, "app.txt"), "app");
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        // --- mapping -------------------------------------------------------------

        [Theory]
        [InlineData("perMachine",       InstallScope.perMachine)]
        [InlineData("perUserOrMachine", InstallScope.perUserOrMachine)]
        [InlineData("perUser",          InstallScope.perUserOrMachine)]
        [InlineData("PERUSERORMACHINE", InstallScope.perUserOrMachine)]
        public void Scope_MapsToWixScope(string scope, InstallScope expected)
        {
            Assert.Equal(expected, InstallerBuilder.ToWixInstallScope(scope, strict: false));
        }

        [Fact]
        public void PerUser_UnderStrict_Fails()
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => InstallerBuilder.ToWixInstallScope("perUser", strict: true));
            Assert.Contains("perUserOrMachine", ex.Message);
        }

        [Theory]
        [InlineData("perMachine",       InstallScope.perMachine)]
        [InlineData("perUserOrMachine", InstallScope.perUserOrMachine)]
        public void ExplicitScopes_UnderStrict_AreUnchanged(string scope, InstallScope expected)
        {
            Assert.Equal(expected, InstallerBuilder.ToWixInstallScope(scope, strict: true));
        }

        // --- WXS and MSI ---------------------------------------------------------

        /// <summary>
        /// Emits the WXS for a one-file package with the scope applied the way the build
        /// path applies it.  A plain Project, not a ManagedProject: ManagedProject packages
        /// its CA assembly, which fails when the test host shadow-copies WixSharp.dll.
        /// </summary>
        private string BuildWxs(string scope)
        {
            var root = new Dir(@"%ProgramFiles%\TestApp", new WixSharp.File(Path.Combine(_dir, "app.txt")));
            var project = new Project("TestApp", root)
            {
                GUID = new Guid("6c0f3f8e-2b8a-4f7e-9a51-0d4f3c2a7b19"),
                OutDir = _dir,
                OutFileName = "TestApp",
                UI = WUI.WixUI_ProgressOnly,
            };
            project.Scope = InstallerBuilder.ToWixInstallScope(scope, strict: false);

            // Drop the dialog set so the MSI compile does not need WixToolset.UI.wixext.
            project.WixSourceGenerated += doc =>
            {
                XNamespace ui = "http://wixtoolset.org/schemas/v4/wxs/ui";
                doc.Descendants(ui + "WixUI").Remove();
                doc.Descendants().Where(e => e.Name.LocalName == "UIRef").Remove();
            };

            Compiler.BuildWxs(project, Compiler.OutputType.MSI);
            return Path.Combine(_dir, "TestApp.wxs");
        }

        private string BuildMsi(string scope)
        {
            string wxs = BuildWxs(scope);
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

        private static string? PropertyValue(Inspector inspector, string name)
            => inspector.ReadRows("Property")
                        .Where(r => (string?)r["Property"] == name)
                        .Select(r => (string?)r["Value"])
                        .SingleOrDefault();

        [Theory]
        [InlineData("perMachine",       "perMachine")]
        [InlineData("perUserOrMachine", "perUserOrMachine")]
        [InlineData("perUser",          "perUserOrMachine")]
        public void PackageElement_CarriesScope(string scope, string expected)
        {
            var package = XDocument.Load(BuildWxs(scope)).Descendants(Wix + "Package").Single();

            Assert.Equal(expected, (string?)package.Attribute("Scope"));
        }

        [WixRequiredFact]
        public void PerMachine_SetsAllUsersToOne()
        {
            using var inspector = new Inspector(BuildMsi("perMachine"));

            Assert.Equal("1", PropertyValue(inspector, "ALLUSERS"));
            Assert.Null(PropertyValue(inspector, "MSIINSTALLPERUSER"));
        }

        [WixRequiredFact]
        public void PerUserOrMachine_DefaultsToPerUserInstall()
        {
            using var inspector = new Inspector(BuildMsi("perUserOrMachine"));

            Assert.Equal("2", PropertyValue(inspector, "ALLUSERS"));
            Assert.Equal("1", PropertyValue(inspector, "MSIINSTALLPERUSER"));
        }
    }
}
