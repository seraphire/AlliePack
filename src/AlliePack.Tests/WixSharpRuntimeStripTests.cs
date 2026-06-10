using System.Linq;
using System.Xml.Linq;
using Xunit;
using AlliePack;

namespace AlliePack.Tests
{
    /// <summary>
    /// Tests for <see cref="InstallerBuilder.StripWixSharpRuntime"/> (GAP-9): the WixSharp
    /// managed-runtime scaffolding is removed when nothing depends on it, and left fully
    /// intact when a managed custom action or the ManagedUI stack needs it.  Each method
    /// is exercised against a minimal in-memory WXS document so no WiX toolchain is required.
    /// </summary>
    public class WixSharpRuntimeStripTests
    {
        private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";

        private const string InitActionId = "WixSharp_InitRuntime_Action";
        private const string BinaryId     = "WixSharp_InitRuntime_Action_File";

        // -----------------------------------------------------------------------
        // Document builders
        // -----------------------------------------------------------------------

        /// <summary>Component with one File whose KeyPath is the WixSharp registry marker.</summary>
        private static XElement MakeFileComponent()
            => new XElement(Wix + "Component",
                new XAttribute("Id", "Component.README.txt"),
                new XElement(Wix + "File",
                    new XAttribute("Id", "README.txt"),
                    new XAttribute("Source", @"files\README.txt")),
                MakeMarker());

        /// <summary>CreateFolder-only component whose KeyPath is the WixSharp registry marker.</summary>
        private static XElement MakeFolderComponent()
            => new XElement(Wix + "Component",
                new XAttribute("Id", "Component.EmptyDirectory"),
                new XElement(Wix + "CreateFolder"),
                MakeMarker());

        private static XElement MakeMarker()
            => new XElement(Wix + "RegistryKey",
                new XAttribute("Root", "HKCU"),
                new XAttribute("Key", @"Software\WixSharp\Used"),
                new XElement(Wix + "RegistryValue",
                    new XAttribute("Value", "0"),
                    new XAttribute("Type", "string"),
                    new XAttribute("KeyPath", "yes")));

        /// <summary>
        /// Minimal WXS mirroring WixSharp's standard-UI output: init CA + Binary +
        /// InstallExecuteSequence entry + registry markers on two components.
        /// </summary>
        private static XDocument MakeStandardDoc()
            => new XDocument(
                new XElement(Wix + "Wix",
                    new XElement(Wix + "Package",
                        MakeFileComponent(),
                        MakeFolderComponent(),
                        new XElement(Wix + "CustomAction",
                            new XAttribute("Id", InitActionId),
                            new XAttribute("BinaryRef", BinaryId),
                            new XAttribute("DllEntry", InitActionId),
                            new XAttribute("Return", "check"),
                            new XAttribute("Execute", "immediate")),
                        new XElement(Wix + "Binary",
                            new XAttribute("Id", BinaryId),
                            new XAttribute("SourceFile", "WixSharp.CA.dll")),
                        new XElement(Wix + "InstallExecuteSequence",
                            new XElement(Wix + "Custom",
                                new XAttribute("Condition", " (1) "),
                                new XAttribute("Action", InitActionId),
                                new XAttribute("Before", "AppSearch"))))));

        // -----------------------------------------------------------------------
        // Strip path -- standard UI, no managed CAs
        // -----------------------------------------------------------------------

        [Fact]
        public void Strips_InitAction_Binary_And_SequenceEntry()
        {
            var doc = MakeStandardDoc();
            bool stripped = InstallerBuilder.StripWixSharpRuntime(doc);

            Assert.True(stripped);
            Assert.Empty(doc.Descendants(Wix + "CustomAction"));
            Assert.Empty(doc.Descendants(Wix + "Binary"));
            Assert.Empty(doc.Descendants(Wix + "Custom"));
        }

        [Fact]
        public void Removes_Empty_InstallExecuteSequence()
        {
            var doc = MakeStandardDoc();
            InstallerBuilder.StripWixSharpRuntime(doc);

            Assert.Empty(doc.Descendants(Wix + "InstallExecuteSequence"));
        }

        [Fact]
        public void Keeps_Sequence_With_Other_Entries()
        {
            var doc = MakeStandardDoc();
            doc.Descendants(Wix + "InstallExecuteSequence").First().Add(
                new XElement(Wix + "Custom",
                    new XAttribute("Action", "SomeOtherAction"),
                    new XAttribute("Before", "InstallFinalize")));

            InstallerBuilder.StripWixSharpRuntime(doc);

            var sequence = doc.Descendants(Wix + "InstallExecuteSequence").FirstOrDefault();
            Assert.NotNull(sequence);
            var entries = sequence!.Elements(Wix + "Custom").ToList();
            Assert.Single(entries);
            Assert.Equal("SomeOtherAction", entries[0].Attribute("Action")?.Value);
        }

        [Fact]
        public void Removes_Registry_Markers()
        {
            var doc = MakeStandardDoc();
            InstallerBuilder.StripWixSharpRuntime(doc);

            Assert.Empty(doc.Descendants(Wix + "RegistryKey"));
            Assert.Empty(doc.Descendants(Wix + "RegistryValue"));
        }

        [Fact]
        public void Promotes_KeyPath_To_File_When_Marker_Was_KeyPath()
        {
            var doc = MakeStandardDoc();
            InstallerBuilder.StripWixSharpRuntime(doc);

            var file = doc.Descendants(Wix + "File").Single();
            Assert.Equal("yes", file.Attribute("KeyPath")?.Value);
        }

        [Fact]
        public void FolderOnly_Component_Falls_Back_To_Directory_KeyPath()
        {
            var doc = MakeStandardDoc();
            InstallerBuilder.StripWixSharpRuntime(doc);

            // CreateFolder-only component: no element carries KeyPath; the directory
            // becomes the keypath by omission (valid MSI for CreateFolder components).
            var component = doc.Descendants(Wix + "Component")
                .Single(c => c.Attribute("Id")?.Value == "Component.EmptyDirectory");
            Assert.DoesNotContain(component.Descendants(),
                e => e.Attribute("KeyPath")?.Value == "yes");
            Assert.NotNull(component.Element(Wix + "CreateFolder"));
        }

        [Fact]
        public void Does_Not_Promote_KeyPath_When_File_Already_Has_One()
        {
            var doc = MakeStandardDoc();
            var component = doc.Descendants(Wix + "Component")
                .Single(c => c.Attribute("Id")?.Value == "Component.README.txt");
            component.Add(new XElement(Wix + "File",
                new XAttribute("Id", "second.txt"),
                new XAttribute("Source", @"files\second.txt"),
                new XAttribute("KeyPath", "yes")));

            InstallerBuilder.StripWixSharpRuntime(doc);

            // Exactly one KeyPath in the component -- the pre-existing one.
            var keyPaths = component.Descendants()
                .Where(e => e.Attribute("KeyPath")?.Value == "yes").ToList();
            Assert.Single(keyPaths);
            Assert.Equal("second.txt", keyPaths[0].Attribute("Id")?.Value);
        }

        [Fact]
        public void Marker_Without_KeyPath_Is_Removed_Without_Promotion()
        {
            var doc = MakeStandardDoc();
            foreach (var rv in doc.Descendants(Wix + "RegistryValue"))
                rv.Attribute("KeyPath")?.Remove();

            InstallerBuilder.StripWixSharpRuntime(doc);

            Assert.Empty(doc.Descendants(Wix + "RegistryKey"));
            var file = doc.Descendants(Wix + "File").Single();
            Assert.Null(file.Attribute("KeyPath"));
        }

        // -----------------------------------------------------------------------
        // Keep path -- something needs the runtime
        // -----------------------------------------------------------------------

        [Fact]
        public void Keeps_Everything_When_Another_CA_References_The_Binary()
        {
            var doc = MakeStandardDoc();
            // ManagedUI's CancelRequestHandler references the same runtime binary.
            doc.Descendants(Wix + "Package").First().Add(
                new XElement(Wix + "CustomAction",
                    new XAttribute("Id", "CancelRequestHandler"),
                    new XAttribute("BinaryRef", BinaryId),
                    new XAttribute("DllEntry", "CancelRequestHandler")));

            bool stripped = InstallerBuilder.StripWixSharpRuntime(doc);

            Assert.False(stripped);
            Assert.Single(doc.Descendants(Wix + "Binary"));
            Assert.Equal(2, doc.Descendants(Wix + "CustomAction").Count());
            Assert.Single(doc.Descendants(Wix + "Custom"));
            Assert.Equal(2, doc.Descendants(Wix + "RegistryKey").Count());
        }

        [Fact]
        public void Keeps_Everything_When_Another_Managed_CA_Assembly_Is_Present()
        {
            var doc = MakeStandardDoc();
            doc.Descendants(Wix + "Package").First().Add(
                new XElement(Wix + "Binary",
                    new XAttribute("Id", "MyProduct_CA_File"),
                    new XAttribute("SourceFile", "MyProduct.CA.dll")));

            bool stripped = InstallerBuilder.StripWixSharpRuntime(doc);

            Assert.False(stripped);
            Assert.Equal(2, doc.Descendants(Wix + "Binary").Count());
            Assert.Single(doc.Descendants(Wix + "CustomAction"));
        }

        [Fact]
        public void Keeps_Everything_When_EmbeddedUI_Is_Present()
        {
            var doc = MakeStandardDoc();
            doc.Descendants(Wix + "Package").First().Add(
                new XElement(Wix + "UI",
                    new XElement(Wix + "EmbeddedUI",
                        new XAttribute("Id", "WixSharp_EmbeddedUI_Asm"),
                        new XAttribute("SourceFile", "WixSharp.UI.CA.dll"))));

            bool stripped = InstallerBuilder.StripWixSharpRuntime(doc);

            Assert.False(stripped);
            Assert.Single(doc.Descendants(Wix + "Binary"),
                b => b.Attribute("SourceFile")?.Value == "WixSharp.CA.dll");
        }

        [Fact]
        public void NoOp_When_No_WixSharp_Binary_Exists()
        {
            var doc = new XDocument(
                new XElement(Wix + "Wix",
                    new XElement(Wix + "Package",
                        new XElement(Wix + "Binary",
                            new XAttribute("Id", "SomeAsset"),
                            new XAttribute("SourceFile", "banner.png")))));

            bool stripped = InstallerBuilder.StripWixSharpRuntime(doc);

            Assert.False(stripped);
            Assert.Single(doc.Descendants(Wix + "Binary"));
        }

        [Fact]
        public void Matches_Binary_By_FileName_Regardless_Of_Path()
        {
            var doc = MakeStandardDoc();
            doc.Descendants(Wix + "Binary").Single()
               .SetAttributeValue("SourceFile", @"..\staging\WixSharp.CA.dll");

            bool stripped = InstallerBuilder.StripWixSharpRuntime(doc);

            Assert.True(stripped);
            Assert.Empty(doc.Descendants(Wix + "Binary"));
        }
    }
}
