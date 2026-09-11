using System;
using System.Linq;
using Xunit;

namespace MsiInspector.Tests
{
    /// <summary>
    /// Regression tests for integer column reading.
    /// <para>
    /// Column types were previously taken from the <c>_Columns</c> table, whose
    /// <c>Type</c> field is an integer bitfield (1282 for an i2 column), not the IDT
    /// notation the reader expects ("i2", "s72").  Reading it as a string produced a
    /// numeric string, every type check fell through to the string branch, and every
    /// integer column silently came back as 0 -- Component.Attributes, File.FileSize,
    /// File.Attributes and File.Sequence alike.  Silent zeros are the worst failure mode
    /// for an assertion library, so these pin the behaviour.
    /// </para>
    /// </summary>
    public class IntegerColumnTests
    {
        private const int Permanent      = 16;
        private const int NeverOverwrite = 128;

        private static string BuildFixture()
            => TestMsiBuilder.CreateMinimal(
                fixtureName: "integer-columns",
                directories: new[]
                {
                    new TestMsiBuilder.DirectoryEntry { Directory = "TARGETDIR", DefaultDir = "SourceDir" },
                    new TestMsiBuilder.DirectoryEntry { Directory = "INSTALLFOLDER", ParentDirectory = "TARGETDIR", DefaultDir = "MyApp" }
                },
                components: new[]
                {
                    new TestMsiBuilder.ComponentEntry
                    {
                        Component = "Config",
                        Directory = "INSTALLFOLDER",
                        Attributes = NeverOverwrite | Permanent,
                        KeyPath = "fileConfig"
                    },
                    new TestMsiBuilder.ComponentEntry
                    {
                        Component = "Plain",
                        Directory = "INSTALLFOLDER",
                        Attributes = 0,
                        KeyPath = "filePlain"
                    }
                },
                files: new[]
                {
                    new TestMsiBuilder.FileEntry { File = "fileConfig", Component = "Config", FileName = "app.ini", FileSize = 1234, Sequence = 1 },
                    new TestMsiBuilder.FileEntry { File = "filePlain",  Component = "Plain",  FileName = "logo.png", FileSize = 5678, Sequence = 2 }
                });

        [Fact]
        public void ComponentAttributes_AreReadAsIntegers()
        {
            using var inspector = new Inspector(BuildFixture());

            var config = inspector.Components.Single(c => c.ComponentKey == "Config");

            Assert.Equal(NeverOverwrite | Permanent, config.Attributes);
            Assert.Equal(NeverOverwrite, config.Attributes & NeverOverwrite);
            Assert.Equal(Permanent, config.Attributes & Permanent);
        }

        [Fact]
        public void ComponentAttributes_ZeroStaysZero()
        {
            using var inspector = new Inspector(BuildFixture());

            var plain = inspector.Components.Single(c => c.ComponentKey == "Plain");

            Assert.Equal(0, plain.Attributes);
        }

        [Fact]
        public void FileSizeAndSequence_AreReadAsIntegers()
        {
            using var inspector = new Inspector(BuildFixture());

            var config = inspector.Files.Single(f => f.FileKey == "fileConfig");
            var plain = inspector.Files.Single(f => f.FileKey == "filePlain");

            Assert.Equal(1234, config.FileSize);
            Assert.Equal(1, config.Sequence);
            Assert.Equal(5678, plain.FileSize);
            Assert.Equal(2, plain.Sequence);
        }

        [Fact]
        public void ColumnTypes_AreReportedInIdtNotation()
        {
            using var inspector = new Inspector(BuildFixture());

            var columns = inspector.GetColumns("Component");

            var attributes = columns.Single(c => c.Name == "Attributes");
            var component = columns.Single(c => c.Name == "Component");

            // Not "1282" / "11592", which is what the _Columns bitfield yields.
            Assert.StartsWith("i", attributes.IdtType, StringComparison.Ordinal);
            Assert.StartsWith("s", component.IdtType, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void NullableColumn_IsReportedUppercase()
        {
            using var inspector = new Inspector(BuildFixture());

            // Condition is S255 in the MSI schema: nullable string.
            var condition = inspector.GetColumns("Component").Single(c => c.Name == "Condition");

            Assert.StartsWith("S", condition.IdtType, StringComparison.Ordinal);
        }
    }
}
