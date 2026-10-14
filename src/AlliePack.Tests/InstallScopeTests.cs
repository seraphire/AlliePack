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
        [InlineData("perUser",          InstallScope.perUser)]
        [InlineData("PERUSERORMACHINE", InstallScope.perUserOrMachine)]
        [InlineData("PerUser",          InstallScope.perUser)]
        public void Scope_MapsToWixScope(string scope, InstallScope expected)
        {
            Assert.Equal(expected, InstallerBuilder.ToWixInstallScope(scope));
        }

        // --- installDir ----------------------------------------------------------

        [Theory]
        // Default (Manufacturer\Name): perUser roots at %LOCALAPPDATA%\Programs.
        [InlineData("",                         false, false, @"[ProgramFilesFolder]\Acme\App")]
        [InlineData("",                         false, true,  @"[ProgramFiles64Folder]\Acme\App")]
        [InlineData("",                         true,  true,  @"[LocalAppDataFolder]\Programs\Acme\App")]
        [InlineData("",                         true,  false, @"[LocalAppDataFolder]\Programs\Acme\App")]
        // AlliePack's [ProgramFiles] token follows the scope.
        [InlineData(@"[ProgramFiles]\Acme\X",   false, true,  @"[ProgramFiles64Folder]\Acme\X")]
        [InlineData(@"[ProgramFiles]\Acme\X",   true,  true,  @"[LocalAppDataFolder]\Programs\Acme\X")]
        [InlineData("Acme/X",                   true,  true,  @"[LocalAppDataFolder]\Programs\Acme\X")]
        // Explicit WiX folder properties pass through, even when wrong for the scope.
        [InlineData(@"[ProgramFiles64Folder]\X", true, true,  @"[ProgramFiles64Folder]\X")]
        [InlineData(@"[AppDataFolder]\X",       true,  true,  @"[AppDataFolder]\X")]
        public void InstallPath_DependsOnScope(string configured, bool perUser, bool is64, string expected)
        {
            Assert.Equal(expected, InstallerBuilder.ResolveInstallPath(configured, @"Acme\App", perUser, is64));
        }

        // --- perUser admin requirements -----------------------------------------

        private static InstallerBuilder MakeBuilder(AlliePackConfig config, bool strict = false)
        {
            var resolver = new PathResolver(
                Path.Combine(Path.GetTempPath(), "test.yaml"),
                new Dictionary<string, string>(),
                new Dictionary<string, string>());
            return new InstallerBuilder(config, resolver, new SolutionResolver(resolver),
                new Options { Strict = strict }, new List<string>());
        }

        [Fact]
        public void PerUserAdminRequirements_CleanConfig_HasNone()
        {
            var config = new AlliePackConfig();
            config.Registry.Add(new RegistryConfig { Root = "HKCU", Key = @"Software\Acme" });
            config.Environment.Add(new EnvVarConfig { Name = "ACME_HOME" });

            var issues = MakeBuilder(config).FindPerUserAdminRequirements(@"[LocalAppDataFolder]\Programs\Acme");

            Assert.Empty(issues);
        }

        [Fact]
        public void PerUserAdminRequirements_FindsEverythingThatNeedsAdmin()
        {
            var config = new AlliePackConfig();
            config.Registry.Add(new RegistryConfig { Key = @"Software\Acme" });   // root defaults to HKLM
            config.Environment.Add(new EnvVarConfig { Name = "ACME", Scope = new ConditionalString("machine") });
            config.Services.Add(new ServiceConfig { Name = "AcmeSvc" });
            config.Directories.Add(new DirectoryConfig { Id = "Data", Path = new ConditionalString(@"[CommonAppDataFolder]\Acme") });
            config.Features.Add(new FeatureConfig
            {
                Id = "Extras",
                Registry = { new RegistryConfig { Root = "HKU", Key = @".DEFAULT\Acme" } },
            });

            var issues = MakeBuilder(config).FindPerUserAdminRequirements(@"[ProgramFiles64Folder]\Acme");

            Assert.Equal(6, issues.Count);
            Assert.Contains(issues, i => i.StartsWith("installDir"));
            Assert.Contains(issues, i => i.Contains("HKLM"));
            Assert.Contains(issues, i => i.Contains("'ACME'"));
            Assert.Contains(issues, i => i.Contains("AcmeSvc"));
            Assert.Contains(issues, i => i.Contains("directory 'Data'"));
            Assert.Contains(issues, i => i.StartsWith("feature 'Extras'"));
        }

        [Fact]
        public void PerUserAdminRequirements_WarnByDefault_FailUnderStrict()
        {
            var issues = new List<string> { "service 'AcmeSvc' installs a Windows service" };

            InstallerBuilder.ReportPerUserAdminRequirements(issues, strict: false);
            var ex = Assert.Throws<InvalidOperationException>(
                () => InstallerBuilder.ReportPerUserAdminRequirements(issues, strict: true));
            Assert.Contains("AcmeSvc", ex.Message);
        }

        // --- WXS and MSI ---------------------------------------------------------

        /// <summary>
        /// Emits the WXS for a one-file package with the scope applied the way the build
        /// path applies it.  A plain Project, not a ManagedProject: ManagedProject packages
        /// its CA assembly, which fails when the test host shadow-copies WixSharp.dll.
        /// </summary>
        private string BuildWxs(string scope)
        {
            // Root the install the way the build path does, so perUser lands under
            // [LocalAppDataFolder]\Programs and the others under [ProgramFilesFolder].
            string installPath = InstallerBuilder.ResolveInstallPath(
                "", "TestApp", scope.Equals("perUser", StringComparison.OrdinalIgnoreCase), is64: false);
            string[] parts = installPath.Split('\\');
            Dir leaf = new Dir(parts[parts.Length - 1], new WixSharp.File(Path.Combine(_dir, "app.txt")));
            Dir root = leaf;
            for (int i = parts.Length - 2; i >= 0; i--)
                root = new Dir(parts[i], root);

            var project = new Project("TestApp", root)
            {
                GUID = new Guid("6c0f3f8e-2b8a-4f7e-9a51-0d4f3c2a7b19"),
                OutDir = _dir,
                OutFileName = "TestApp",
                UI = WUI.WixUI_ProgressOnly,
            };
            project.Scope = InstallerBuilder.ToWixInstallScope(scope);

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
        [InlineData("perUser",          "perUser")]
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
    
        [WixRequiredFact]
        public void PerUser_LeavesAllUsersEmpty_AndInstallsUnderLocalAppDataPrograms()
        {
            using var inspector = new Inspector(BuildMsi("perUser"));

            // No ALLUSERS and no MSIINSTALLPERUSER: a plain per-user install, no elevation.
            Assert.Null(PropertyValue(inspector, "ALLUSERS"));
            Assert.Null(PropertyValue(inspector, "MSIINSTALLPERUSER"));

            var dirs = inspector.ReadRows("Directory").ToList();
            var app = dirs.Single(r => ((string?)r["DefaultDir"])?.Split('|').Last() == "TestApp");
            var programs = dirs.Single(r => (string?)r["Directory"] == (string?)app["Directory_Parent"]);
            Assert.Equal("Programs", ((string?)programs["DefaultDir"])?.Split('|').Last());
            Assert.Equal("LocalAppDataFolder", (string?)programs["Directory_Parent"]);
        }
    }
}
