using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using AlliePack;

namespace AlliePack.Tests
{
    /// <summary>requires: -- a prerequisite found through a registry value, checked before install.</summary>
    public class RequirementTests
    {
        static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";

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

        private static XDocument Apply(params RequirementConfig[] reqs)
        {
            var doc = new XDocument(new XElement(Wix + "Wix", new XElement(Wix + "Package")));
            var extras = new InstallerExtras();
            extras.Requirements.AddRange(reqs);
            Assert.True(extras.HasAny);
            extras.Apply(doc);
            return doc;
        }

        private static RequirementConfig GmStudio() => new RequirementConfig
        {
            Name = "gmStudio",
            Registry = new RequirementRegistryConfig
            {
                Root = "HKLM", Key = @"Software\GreatMigrations", Value = "InstallDir", Bitness = 32,
            },
            File = "gmStudio.exe",
        };

        [Fact]
        public void Requires_Parses()
        {
            var config = Parse(MinimalYaml +
                "requires:\n" +
                "  - name: gmStudio\n" +
                "    registry:\n" +
                "      key: Software\\GreatMigrations\n" +
                "      value: InstallDir\n" +
                "      bitness: 32\n" +
                "    file: gmStudio.exe\n" +
                "    message: Install gmStudio first.\n");

            var req = Assert.Single(config.Requires);
            Assert.Equal("gmStudio", req.Name);
            Assert.Equal("HKLM", req.Registry.Root);          // default
            Assert.Equal(@"Software\GreatMigrations", req.Registry.Key);
            Assert.Equal("InstallDir", req.Registry.Value);
            Assert.Equal(32, req.Registry.Bitness);
            Assert.Equal("gmStudio.exe", req.File);
            Assert.Equal("Install gmStudio first.", req.Message);
        }

        [Fact]
        public void Requires_EmitsSearchAndLaunchCondition()
        {
            var doc = Apply(GmStudio());

            var prop = doc.Descendants(Wix + "Property").Single();
            Assert.Equal("APREQUIRES1", (string?)prop.Attribute("Id"));

            var reg = prop.Element(Wix + "RegistrySearch")!;
            Assert.Equal("HKLM", (string?)reg.Attribute("Root"));
            Assert.Equal(@"Software\GreatMigrations", (string?)reg.Attribute("Key"));
            Assert.Equal("InstallDir", (string?)reg.Attribute("Name"));
            Assert.Equal("directory", (string?)reg.Attribute("Type"));
            Assert.Equal("always32", (string?)reg.Attribute("Bitness"));
            Assert.Equal("gmStudio.exe", (string?)reg.Element(Wix + "FileSearch")!.Attribute("Name"));

            var launch = doc.Descendants(Wix + "Launch").Single();
            Assert.Equal("Installed OR APREQUIRES1", (string?)launch.Attribute("Condition"));
            Assert.Contains("gmStudio must be installed first", (string?)launch.Attribute("Message"));
        }

        [Fact]
        public void Requires_WithoutFileOrBitness_OmitsThem()
        {
            var req = GmStudio();
            req.File = null;
            req.Registry.Bitness = null;
            req.Registry.Value = null;
            req.Message = "custom";
            var doc = Apply(req);

            var reg = doc.Descendants(Wix + "RegistrySearch").Single();
            Assert.Null(reg.Attribute("Bitness"));
            Assert.Null(reg.Attribute("Name"));
            Assert.Empty(reg.Elements(Wix + "FileSearch"));
            Assert.Equal("custom", (string?)doc.Descendants(Wix + "Launch").Single().Attribute("Message"));
        }

        [Fact]
        public void Requires_Several_GetDistinctIds()
        {
            var second = GmStudio();
            second.Name = "Other";
            var doc = Apply(GmStudio(), second);

            Assert.Equal(new[] { "APREQUIRES1", "APREQUIRES2" },
                doc.Descendants(Wix + "Property").Select(p => (string?)p.Attribute("Id")));
            Assert.Equal(2, doc.Descendants(Wix + "Launch").Count());
            Assert.Equal(2, doc.Descendants(Wix + "FileSearch")
                .Select(f => (string?)f.Attribute("Id")).Distinct().Count());
        }

        [Theory]
        [InlineData("", "Software\\X", "HKLM", null, null, "name")]
        [InlineData("A", "", "HKLM", null, null, "registry.key")]
        [InlineData("A", "Software\\X", "HKEY_LOCAL_MACHINE", null, null, "registry.root")]
        [InlineData("A", "Software\\X", "HKLM", 16, null, "bitness")]
        [InlineData("A", "Software\\X", "HKLM", null, "bin\\a.exe", "bare file name")]
        public void Validate_RejectsBadEntries(
            string name, string key, string root, int? bitness, string? file, string expected)
        {
            var req = new RequirementConfig
            {
                Name = name,
                Registry = new RequirementRegistryConfig { Root = root, Key = key, Bitness = bitness },
                File = file,
            };
            var ex = Assert.Throws<InvalidOperationException>(
                () => InstallerBuilder.ValidateRequirements(new List<RequirementConfig> { req }));
            Assert.Contains(expected, ex.Message);
        }

        [Fact]
        public void Validate_AcceptsLowercaseRoot()
        {
            var req = GmStudio();
            req.Registry.Root = "hkcu";
            InstallerBuilder.ValidateRequirements(new List<RequirementConfig> { req });
            var reg = Apply(req).Descendants(Wix + "RegistrySearch").Single();
            Assert.Equal("HKCU", (string?)reg.Attribute("Root"));
        }
    }
}
