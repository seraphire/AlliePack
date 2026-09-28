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
    /// GAP-10: a <c>shortcuts:</c> or <c>services:</c> entry without <c>description:</c>
    /// must not emit <c>Description=""</c>.  WiX rejects empty attribute values (WIX0006),
    /// so a valid config would otherwise produce a WXS that fails <c>wix build</c>.
    /// <para>
    /// The WXS is produced with <c>Compiler.BuildWxs</c>, which does not invoke wix.exe,
    /// so these tests need no WiX toolchain (same approach as InstallDirGroupWxsTests).
    /// </para>
    /// </summary>
    public class DescriptionAttributeWxsTests : IDisposable
    {
        private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";

        private readonly string _dir;

        public DescriptionAttributeWxsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "alliepack-desc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            System.IO.File.WriteAllText(Path.Combine(_dir, "app.exe"), "exe");
            System.IO.File.WriteAllText(Path.Combine(_dir, "svc-plain.exe"), "exe");
            System.IO.File.WriteAllText(Path.Combine(_dir, "svc-described.exe"), "exe");
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

        private static ShortcutInfo Shortcut(string name, string description = "")
            => new ShortcutInfo { Name = name, Target = "[INSTALLDIR]app.exe", Folder = "desktop", Description = description };

        private static ServiceConfig Service(string name, string? description = null)
            => new ServiceConfig { Name = name, Executable = $"[INSTALLDIR]{name}.exe", Description = description };

        // -----------------------------------------------------------------------------------
        // Object graph
        // -----------------------------------------------------------------------------------

        [Fact]
        public void Shortcut_WithoutDescription_LeavesDescriptionUnset()
        {
            var shortcut = MakeBuilder().BuildShortcut(Shortcut("App"), "%Desktop%");

            Assert.Null(shortcut.Description);
        }

        [Fact]
        public void Shortcut_WithDescription_KeepsIt()
        {
            var shortcut = MakeBuilder().BuildShortcut(Shortcut("App", "Launch the app"), "%Desktop%");

            Assert.Equal("Launch the app", shortcut.Description);
        }

        [Fact]
        public void Service_WithoutDescription_LeavesDescriptionUnset()
        {
            // null, not the service name: WixSharp's Name setter would otherwise default it.
            var si = MakeBuilder().BuildServiceInstaller(Service("svc-plain"));

            Assert.Null(si.Description);
        }

        [Fact]
        public void Service_WithEmptyDescription_LeavesDescriptionUnset()
        {
            var si = MakeBuilder().BuildServiceInstaller(Service("svc-plain", ""));

            Assert.Null(si.Description);
        }

        [Fact]
        public void Service_WithDescription_KeepsIt()
        {
            var si = MakeBuilder().BuildServiceInstaller(Service("svc-described", "Does work"));

            Assert.Equal("Does work", si.Description);
        }

        // -----------------------------------------------------------------------------------
        // Emitted WXS
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Builds a tree with one described and one description-less entry of each kind,
        /// attached the way AttachShortcut and the services loop attach them, and serializes it.
        /// </summary>
        private XDocument BuildWxs()
        {
            var builder = MakeBuilder();

            var app = new WixSharp.File(Path.Combine(_dir, "app.exe"));
            app.Shortcuts = new[]
            {
                builder.BuildShortcut(Shortcut("Plain"), "%Desktop%"),
                builder.BuildShortcut(Shortcut("Described", "Launch the app"), "%Desktop%"),
            };

            var svcPlain = new WixSharp.File(Path.Combine(_dir, "svc-plain.exe"));
            svcPlain.ServiceInstallers = new IGenericEntity[]
            {
                builder.BuildServiceInstaller(Service("svc-plain")),
            };

            var svcDescribed = new WixSharp.File(Path.Combine(_dir, "svc-described.exe"));
            svcDescribed.ServiceInstallers = new IGenericEntity[]
            {
                builder.BuildServiceInstaller(Service("svc-described", "Does work")),
            };

            var install = new Dir("TestApp", app, svcPlain, svcDescribed);
            var root = new Dir("%ProgramFiles%", install);

            // Shortcut root directories are separate top-level entities, as in AttachShortcut.
            var desktop = new Dir("%Desktop%");

            // A plain Project, not a ManagedProject -- see InstallDirGroupWxsTests.
            var project = new Project("TestApp", root, desktop)
            {
                GUID = new Guid("5b0f4e0a-6f5d-4c1e-9f1b-3e2a7c9d8b41"),
                OutDir = _dir,
                OutFileName = "TestApp",
                UI = WUI.WixUI_ProgressOnly,
            };

            Compiler.BuildWxs(project, Compiler.OutputType.MSI);

            return XDocument.Load(Path.Combine(_dir, "TestApp.wxs"));
        }

        // WixSharp appends ".lnk" to shortcut names, so compare without the extension.
        private static XElement ShortcutNamed(XDocument doc, string name)
            => doc.Descendants(Wix + "Shortcut")
                  .Single(e => Path.GetFileNameWithoutExtension((string?)e.Attribute("Name") ?? string.Empty) == name);

        private static XElement ServiceNamed(XDocument doc, string name)
            => doc.Descendants(Wix + "ServiceInstall").Single(e => (string?)e.Attribute("Name") == name);

        [Fact]
        public void Wxs_ContainsNoEmptyDescriptionAttribute()
        {
            var doc = BuildWxs();

            var empty = doc.Descendants()
                           .Where(e => e.Attribute("Description") is XAttribute a && a.Value.Length == 0)
                           .Select(e => e.Name.LocalName)
                           .ToList();

            Assert.Empty(empty);
        }

        [Fact]
        public void Wxs_ShortcutWithoutDescription_OmitsTheAttribute()
        {
            var doc = BuildWxs();

            Assert.Null(ShortcutNamed(doc, "Plain").Attribute("Description"));
        }

        [Fact]
        public void Wxs_ShortcutWithDescription_EmitsIt()
        {
            var doc = BuildWxs();

            Assert.Equal("Launch the app", (string?)ShortcutNamed(doc, "Described").Attribute("Description"));
        }

        [Fact]
        public void Wxs_ServiceWithoutDescription_OmitsTheAttribute()
        {
            var doc = BuildWxs();

            Assert.Null(ServiceNamed(doc, "svc-plain").Attribute("Description"));
        }

        [Fact]
        public void Wxs_ServiceWithDescription_EmitsIt()
        {
            var doc = BuildWxs();

            Assert.Equal("Does work", (string?)ServiceNamed(doc, "svc-described").Attribute("Description"));
        }
    }
}
