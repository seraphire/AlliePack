using System.Linq;
using System.Xml.Linq;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using AlliePack;

namespace AlliePack.Tests
{
    public class InstallerExtrasTests
    {
        static readonly XNamespace Wix  = "http://wixtoolset.org/schemas/v4/wxs";
        static readonly XNamespace Util = "http://wixtoolset.org/schemas/v4/wxs/util";

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
                        new XElement(Wix + "File", new XAttribute("Id", "apFile1"))))));

        // -----------------------------------------------------------------------
        // Parsing
        // -----------------------------------------------------------------------

        [Fact]
        public void Ui_Block_ParsesArtworkAndLaunchOnExit()
        {
            var config = Parse(MinimalYaml +
                "ui:\n" +
                "  type: standard\n" +
                "  banner: art/banner.bmp\n" +
                "  launchOnExit:\n" +
                "    file: \"[INSTALLDIR]\\\\App.exe\"\n" +
                "    text: Launch App\n" +
                "    checked: true\n" +
                "  dialog: art/dialog.bmp\n");

            Assert.Equal("art/banner.bmp", config.Ui.Banner);
            Assert.Equal("art/dialog.bmp", config.Ui.Dialog);
            Assert.NotNull(config.Ui.LaunchOnExit);
            Assert.Equal("[INSTALLDIR]\\App.exe", config.Ui.LaunchOnExit!.File);
            Assert.Equal("Launch App", config.Ui.LaunchOnExit.Text);
            Assert.True(config.Ui.LaunchOnExit.Checked);
        }

        [Fact]
        public void RunAfterInstall_DefaultCondition_SkipsUninstall()
        {
            var config = Parse(MinimalYaml +
                "runAfterInstall:\n  - file: \"[INSTALLDIR]\\\\App.exe\"\n    args: update\n");
            Assert.Single(config.RunAfterInstall);
            Assert.Equal("NOT REMOVE~=\"ALL\"", config.RunAfterInstall[0].Condition);
        }

        [Fact]
        public void XmlEdits_Parse()
        {
            var config = Parse(MinimalYaml +
                "xmlEdits:\n  - file: a.xml\n    path: /Root/@x\n    value: \"[INSTALLDIR]y\"\n");
            Assert.Single(config.XmlEdits);
            Assert.Equal("/Root/@x", config.XmlEdits[0].Path);
            Assert.Null(config.XmlEdits[0].Name);
        }

        [Fact]
        public void Environment_Part_DefaultsToAll()
        {
            var config = Parse(MinimalYaml + "environment:\n  - name: PATH\n    value: x\n");
            Assert.Equal("all", config.Environment[0].Part);
        }

        [Fact]
        public void Product_IconAndMajorUpgrade_Parse()
        {
            var config = Parse(MinimalYaml + "  icon: app.ico\n  majorUpgrade: true\n");
            Assert.Equal("app.ico", config.Product.Icon);
            Assert.True(config.Product.MajorUpgrade);
        }

        // -----------------------------------------------------------------------
        // WXS output
        // -----------------------------------------------------------------------

        [Fact]
        public void LaunchOnExit_AddsCheckboxActionAndFinishPublish()
        {
            var doc = NewDoc();
            var extras = new InstallerExtras { LaunchOnExit = ("apFile1", "Launch it", null, false) };
            extras.Apply(doc);

            var props = doc.Descendants(Wix + "Property").ToDictionary(p => (string)p.Attribute("Id")!, p => (string)p.Attribute("Value")!);
            Assert.Equal("Launch it", props["WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT"]);
            Assert.False(props.ContainsKey("WIXUI_EXITDIALOGOPTIONALCHECKBOX"));

            var ca = doc.Descendants(Wix + "CustomAction").Single();
            Assert.Equal("apFile1", (string?)ca.Attribute("FileRef"));
            Assert.Equal("", (string?)ca.Attribute("ExeCommand"));   // WiX requires the attribute

            var publish = doc.Descendants(Wix + "Publish").Single();
            Assert.Equal("ExitDialog", (string?)publish.Attribute("Dialog"));
            Assert.Equal("apLaunchOnExit", (string?)publish.Attribute("Value"));
        }

        [Fact]
        public void RunAfterInstall_ChainsAfterInstallFinalize()
        {
            var doc = NewDoc();
            var extras = new InstallerExtras();
            extras.RunAfterInstall.Add(("apFile1", "a", "c1"));
            extras.RunAfterInstall.Add(("apFile1", "b", "c2"));
            extras.Apply(doc);

            var customs = doc.Descendants(Wix + "Custom").ToList();
            Assert.Equal("InstallFinalize", (string?)customs[0].Attribute("After"));
            Assert.Equal("apRunAfterInstall1", (string?)customs[1].Attribute("After"));
            Assert.Equal("c2", (string?)customs[1].Attribute("Condition"));
        }

        [Fact]
        public void XmlEdit_LandsInTheFilesComponent()
        {
            var doc = NewDoc();
            var extras = new InstallerExtras();
            extras.XmlEdits.Add(("apFile1", "/Root/@x", null, "[INSTALLDIR]y"));
            extras.Apply(doc);

            var xml = doc.Descendants(Util + "XmlFile").Single();
            Assert.Equal("Component", xml.Parent!.Name.LocalName);
            Assert.Equal("[#apFile1]", (string?)xml.Attribute("File"));
            Assert.Equal("setValue", (string?)xml.Attribute("Action"));
            Assert.Null(xml.Attribute("Name"));
        }

        // Two installed files, each in its own component, under the given directory Ids.
        private static XDocument TwoFileDoc(string dir1, string dir2) => new XDocument(
            new XElement(Wix + "Wix",
                new XElement(Wix + "Package",
                    new XElement(Wix + "Directory", new XAttribute("Id", dir1),
                        new XElement(Wix + "Component",
                            new XElement(Wix + "File", new XAttribute("Id", "apFile1")))),
                    dir1 == dir2
                        ? null
                        : new XElement(Wix + "Directory", new XAttribute("Id", dir2)),
                    new XElement(Wix + "DirectoryRef", new XAttribute("Id", dir2),
                        new XElement(Wix + "Component",
                            new XElement(Wix + "File", new XAttribute("Id", "apFile2")))))));

        [Fact]
        public void CopyFiles_SameDestinationInOneDirectory_FromDifferentAnchors_Throws()
        {
            var doc = TwoFileDoc("INSTALLDIR", "INSTALLDIR");
            var extras = new InstallerExtras();
            extras.CopyFiles.Add(("apFile1", "VB6.EXE", "gmVB6.exe", "[INSTALLDIR]\\A.exe"));
            extras.CopyFiles.Add(("apFile2", "OTHER.EXE", "GMVB6.EXE", "[INSTALLDIR]\\B.exe"));

            var ex = Assert.Throws<System.InvalidOperationException>(() => extras.Apply(doc));
            Assert.Contains("INSTALLDIR", ex.Message);
            Assert.Contains("entry 1 (VB6.EXE -> gmVB6.exe, beside [INSTALLDIR]\\A.exe)", ex.Message);
            Assert.Contains("entry 2 (OTHER.EXE -> GMVB6.EXE, beside [INSTALLDIR]\\B.exe)", ex.Message);
            Assert.Empty(doc.Descendants(Wix + "CopyFile"));
        }

        [Fact]
        public void CopyFiles_SameDestinationInDifferentDirectories_EmitsOnePairEach()
        {
            var doc = TwoFileDoc("INSTALLDIR", "BINDIR");
            var extras = new InstallerExtras();
            extras.CopyFiles.Add(("apFile1", "VB6.EXE", "gmVB6.exe", "[INSTALLDIR]\\A.exe"));
            extras.CopyFiles.Add(("apFile2", "VB6.EXE", "gmVB6.exe", "[INSTALLDIR]\\bin\\B.exe"));
            extras.Apply(doc);

            var copies = doc.Descendants(Wix + "CopyFile").ToList();
            Assert.Equal(2, copies.Count);
            Assert.Equal("INSTALLDIR", (string?)copies[0].Attribute("DestinationDirectory"));
            Assert.Equal("BINDIR",     (string?)copies[1].Attribute("DestinationDirectory"));
            Assert.Equal("apFile1", (string?)copies[0].Parent!.Element(Wix + "File")!.Attribute("Id"));
            Assert.Equal("apFile2", (string?)copies[1].Parent!.Element(Wix + "File")!.Attribute("Id"));

            var removes = doc.Descendants(Wix + "RemoveFile").ToList();
            Assert.Equal(2, removes.Count);
            Assert.Equal("INSTALLDIR", (string?)removes[0].Attribute("Directory"));
            Assert.Equal("BINDIR",     (string?)removes[1].Attribute("Directory"));
        }

        [Fact]
        public void Artwork_AddsWixVariables()
        {
            var doc = NewDoc();
            new InstallerExtras { BannerBmp = @"C:\b.bmp", DialogBmp = @"C:\d.bmp" }.Apply(doc);

            var vars = doc.Descendants(Wix + "WixVariable").ToDictionary(v => (string)v.Attribute("Id")!, v => (string)v.Attribute("Value")!);
            Assert.Equal(@"C:\b.bmp", vars["WixUIBannerBmp"]);
            Assert.Equal(@"C:\d.bmp", vars["WixUIDialogBmp"]);
        }
    }
}
