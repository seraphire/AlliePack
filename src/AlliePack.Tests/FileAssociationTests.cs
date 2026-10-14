using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using MsiInspector;
using WixSharp;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using AlliePack;

namespace AlliePack.Tests
{
    /// <summary>
    /// fileAssociations: YAML parsing, validation, the ProgId/Extension/Verb elements
    /// written into the WXS, and the Registry rows they compile to in a real MSI.
    /// </summary>
    public class FileAssociationTests : IDisposable
    {
        static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";

        private readonly string _dir;

        public FileAssociationTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "alliepack-assoc-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private static AlliePackConfig Parse(string yaml)
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .WithTypeConverter(new ConditionalStringConverter())
                .WithTypeConverter(new VersionSourceConverter())
                .WithTypeConverter(new UiConfigConverter())
                .Build();
            return deserializer.Deserialize<AlliePackConfig>(yaml);
        }

        private const string MinimalYaml =
            "product:\n  name: Test\n  upgradeCode: 00000000-0000-0000-0000-000000000001\n";

        private static XDocument NewDoc() => new XDocument(
            new XElement(Wix + "Wix",
                new XElement(Wix + "Package",
                    new XElement(Wix + "Component",
                        new XElement(Wix + "File", new XAttribute("Id", "apFile1"))),
                    new XElement(Wix + "Component",
                        new XElement(Wix + "File", new XAttribute("Id", "apFile2"))))));

        private static FileAssociation Assoc(params FileAssociationVerbConfig[] verbs) => new FileAssociation
        {
            Extension   = "myx",
            ProgId      = "MyApp.Doc",
            Description = "My Document",
            FileId      = "apFile1",
            IconFileId  = "apFile1",
            Verbs       = verbs.Any()
                ? verbs.ToList()
                : new List<FileAssociationVerbConfig> { new FileAssociationVerbConfig { Label = "&Open" } },
        };

        // -----------------------------------------------------------------------
        // Parsing
        // -----------------------------------------------------------------------

        [Fact]
        public void FullEntry_Parses()
        {
            var config = Parse(MinimalYaml +
                "fileAssociations:\n" +
                "  - extension: myx\n" +
                "    progId: MyApp.Doc\n" +
                "    description: My Document\n" +
                "    file: \"[INSTALLDIR]\\\\App.exe\"\n" +
                "    icon: \"[INSTALLDIR]\\\\doc.ico\"\n" +
                "    iconIndex: 2\n" +
                "    contentType: application/x-myx\n" +
                "    verbs:\n" +
                "      - id: edit\n" +
                "        label: \"&Edit\"\n" +
                "        args: \"/edit \\\"%1\\\"\"\n");

            var fa = Assert.Single(config.FileAssociations);
            Assert.Equal("myx", fa.Extension);
            Assert.Equal("MyApp.Doc", fa.ProgId);
            Assert.Equal("My Document", fa.Description);
            Assert.Equal("[INSTALLDIR]\\App.exe", fa.File);
            Assert.Equal("[INSTALLDIR]\\doc.ico", fa.Icon);
            Assert.Equal(2, fa.IconIndex);
            Assert.Equal("application/x-myx", fa.ContentType);
            var verb = Assert.Single(fa.Verbs);
            Assert.Equal("edit", verb.Id);
            Assert.Equal("&Edit", verb.Label);
            Assert.Equal("/edit \"%1\"", verb.Args);
        }

        [Fact]
        public void MinimalEntry_LeavesOptionalFieldsUnset()
        {
            var config = Parse(MinimalYaml +
                "fileAssociations:\n  - extension: myx\n    file: \"[INSTALLDIR]\\\\App.exe\"\n");

            var fa = Assert.Single(config.FileAssociations);
            Assert.Null(fa.ProgId);
            Assert.Null(fa.Icon);
            Assert.Equal(0, fa.IconIndex);
            Assert.Empty(fa.Verbs);
        }

        [Fact]
        public void Verb_ArgsDefaultToQuotedPercentOne()
        {
            var config = Parse(MinimalYaml +
                "fileAssociations:\n  - extension: myx\n    file: a.exe\n    verbs:\n      - id: open\n");
            Assert.Equal("\"%1\"", config.FileAssociations[0].Verbs[0].Args);
        }

        // -----------------------------------------------------------------------
        // Validation
        // -----------------------------------------------------------------------

        private static FileAssociationConfig Cfg(string ext, string? progId = null, string file = "a.exe")
            => new FileAssociationConfig { Extension = ext, ProgId = progId, File = file };

        [Fact]
        public void LeadingDot_IsRejected()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                InstallerBuilder.ValidateFileAssociations(new List<FileAssociationConfig> { Cfg(".myx") }));
            Assert.Contains("leading dot", ex.Message);
        }

        [Fact]
        public void MissingFile_IsRejected()
        {
            Assert.Throws<InvalidOperationException>(() =>
                InstallerBuilder.ValidateFileAssociations(new List<FileAssociationConfig> { Cfg("myx", file: "") }));
        }

        [Fact]
        public void DuplicateExtension_IsRejected()
        {
            Assert.Throws<InvalidOperationException>(() =>
                InstallerBuilder.ValidateFileAssociations(
                    new List<FileAssociationConfig> { Cfg("myx", "A"), Cfg("MYX", "B") }));
        }

        [Fact]
        public void DuplicateProgId_IsRejected_IncludingTheDefault()
        {
            // The second entry's progId defaults to its extension, "a".
            Assert.Throws<InvalidOperationException>(() =>
                InstallerBuilder.ValidateFileAssociations(
                    new List<FileAssociationConfig> { Cfg("x", "a"), Cfg("a") }));
        }

        [Fact]
        public void DistinctEntries_Pass()
        {
            InstallerBuilder.ValidateFileAssociations(
                new List<FileAssociationConfig> { Cfg("myx"), Cfg("myy") });
        }

        // -----------------------------------------------------------------------
        // WXS output
        // -----------------------------------------------------------------------

        [Fact]
        public void ProgId_LandsInTheFilesComponent_NotAdvertised()
        {
            var doc = NewDoc();
            var extras = new InstallerExtras();
            extras.FileAssociations.Add(Assoc());
            extras.Apply(doc);

            var progId = doc.Descendants(Wix + "ProgId").Single();
            Assert.Equal("apFile1", (string?)progId.Parent!.Element(Wix + "File")!.Attribute("Id"));
            Assert.Equal("MyApp.Doc", (string?)progId.Attribute("Id"));
            Assert.Equal("My Document", (string?)progId.Attribute("Description"));
            Assert.Equal("apFile1", (string?)progId.Attribute("Icon"));
            Assert.Equal("0", (string?)progId.Attribute("IconIndex"));
            Assert.Null(progId.Attribute("Advertise"));

            var ext = progId.Element(Wix + "Extension")!;
            Assert.Equal("myx", (string?)ext.Attribute("Id"));
            Assert.Null(ext.Attribute("ContentType"));

            var verb = ext.Element(Wix + "Verb")!;
            Assert.Equal("open", (string?)verb.Attribute("Id"));
            Assert.Equal("&Open", (string?)verb.Attribute("Command"));
            Assert.Equal("\"%1\"", (string?)verb.Attribute("Argument"));
            Assert.Equal("apFile1", (string?)verb.Attribute("TargetFile"));
        }

        [Fact]
        public void SeparateIconFile_AndContentType_AndMultipleVerbs()
        {
            var doc = NewDoc();
            var assoc = Assoc(
                new FileAssociationVerbConfig { Id = "open", Label = "&Open" },
                new FileAssociationVerbConfig { Id = "print", Args = "/p \"%1\"" });
            assoc.IconFileId  = "apFile2";
            assoc.IconIndex   = 3;
            assoc.ContentType = "application/x-myx";
            var extras = new InstallerExtras();
            extras.FileAssociations.Add(assoc);
            extras.Apply(doc);

            var progId = doc.Descendants(Wix + "ProgId").Single();
            Assert.Equal("apFile2", (string?)progId.Attribute("Icon"));
            Assert.Equal("3", (string?)progId.Attribute("IconIndex"));
            Assert.Equal("application/x-myx", (string?)progId.Element(Wix + "Extension")!.Attribute("ContentType"));

            var verbs = progId.Descendants(Wix + "Verb").ToList();
            Assert.Equal(new[] { "open", "print" }, verbs.Select(v => (string?)v.Attribute("Id")));
            Assert.Null(verbs[1].Attribute("Command"));
            Assert.Equal("/p \"%1\"", (string?)verbs[1].Attribute("Argument"));
        }

        [Fact]
        public void UnknownFileId_IsSkipped()
        {
            var doc = NewDoc();
            var assoc = Assoc();
            assoc.FileId = "apMissing";
            var extras = new InstallerExtras();
            extras.FileAssociations.Add(assoc);
            extras.Apply(doc);

            Assert.Empty(doc.Descendants(Wix + "ProgId"));
        }

        // -----------------------------------------------------------------------
        // MSI output
        // -----------------------------------------------------------------------

        private string BuildMsi()
        {
            // A non-advertised ProgId only stores a reference to the file, so the exe
            // needs no real icon resource for the compile.
            string exe = Path.Combine(_dir, "App.exe");
            System.IO.File.WriteAllText(exe, "exe");

            var root = new Dir("%ProgramFiles%",
                new Dir("AssocApp", new WixSharp.File(exe) { Id = "apFile1" }));

            var project = new Project("AssocApp", root)
            {
                GUID = new Guid("5b0e7d2a-3c1f-4f7e-9a51-6f0c2b8d4e11"),
                OutDir = _dir,
                OutFileName = "AssocApp",
                UI = WUI.WixUI_ProgressOnly,
            };

            var extras = new InstallerExtras();
            extras.FileAssociations.Add(Assoc());
            project.WixSourceGenerated += extras.Apply;

            // Drop the dialog set so the compile does not depend on WixToolset.UI.wixext.
            project.WixSourceGenerated += doc =>
            {
                XNamespace ui = "http://wixtoolset.org/schemas/v4/wxs/ui";
                doc.Descendants(ui + "WixUI").Remove();
                doc.Descendants().Where(e => e.Name.LocalName == "UIRef").Remove();
            };

            Compiler.BuildWxs(project, Compiler.OutputType.MSI);

            string wxs = Path.Combine(_dir, "AssocApp.wxs");
            string msi = Path.Combine(_dir, "AssocApp.msi");

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

        /// <summary>Registry rows keyed by "Key|Name" -> (Root, Value, Component).</summary>
        private static Dictionary<string, (int Root, string Value, string Component)> RegistryRows(Inspector inspector)
            => inspector.Query("SELECT `Root`, `Key`, `Name`, `Value`, `Component_` FROM `Registry`")
                .ToDictionary(
                    r => (string)r["Key"]! + "|" + (string?)r["Name"],
                    r => (Convert.ToInt32(r["Root"]), (string?)r["Value"] ?? "", (string)r["Component_"]!));

        [WixRequiredFact]
        public void Msi_WritesExtensionProgIdOpenCommandAndIcon_UnderClassesRoot()
        {
            using var inspector = new Inspector(BuildMsi());
            var rows = RegistryRows(inspector);

            // Root 0 = HKCR, which Windows Installer maps to HKLM or HKCU Software\Classes
            // according to the install scope.
            Assert.Equal((0, "MyApp.Doc"), (rows[".myx|"].Root, rows[".myx|"].Value));
            Assert.Equal("My Document", rows["MyApp.Doc|"].Value);
            Assert.Equal("&Open", rows["MyApp.Doc\\shell\\open|"].Value);
            Assert.Equal("\"[#apFile1]\" \"%1\"", rows["MyApp.Doc\\shell\\open\\command|"].Value);
            Assert.Equal("\"[#apFile1]\",0", rows["MyApp.Doc\\DefaultIcon|"].Value);
            Assert.All(rows.Values, r => Assert.Equal(0, r.Root));

            // Every row belongs to the exe's component, so uninstalling it removes them.
            string component = inspector.Files.Single(f => f.FileKey == "apFile1").ComponentKey;
            Assert.All(rows.Values, r => Assert.Equal(component, r.Component));

            // Not advertised: the MSI's own ProgId and Extension tables stay empty.
            Assert.DoesNotContain("Extension", inspector.ListTables());
        }
    }
}
