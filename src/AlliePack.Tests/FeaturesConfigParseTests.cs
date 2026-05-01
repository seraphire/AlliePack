using System.Collections.Generic;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using AlliePack;

namespace AlliePack.Tests
{
    public class FeaturesConfigParseTests
    {
        private static AlliePackConfig Parse(string yaml)
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .WithTypeConverter(new ConditionalStringConverter())
                .WithTypeConverter(new VersionSourceConverter())
                .Build();
            return deserializer.Deserialize<AlliePackConfig>(yaml);
        }

        // -----------------------------------------------------------------------
        // features: absent
        // -----------------------------------------------------------------------

        [Fact]
        public void Features_AbsentFromYaml_IsEmptyList()
        {
            var config = Parse("product:\n  name: Test\n  upgradeCode: 00000000-0000-0000-0000-000000000001\n");
            Assert.Empty(config.Features);
        }

        // -----------------------------------------------------------------------
        // features: basic parsing
        // -----------------------------------------------------------------------

        private const string TwoFeatureYaml = @"
product:
  name: Test
  upgradeCode: 00000000-0000-0000-0000-000000000001
features:
  - id: MainApp
    name: Application
    default: true
    structure:
      - source: 'bin:MyApp.exe'
  - id: CliTools
    name: Command-line Tools
    description: Installs CLI tools
    default: false
    display: collapse
    structure:
      - source: 'bin:myapp-cli.exe'
";

        [Fact]
        public void Features_TwoFeatures_ParsesBothIds()
        {
            var config = Parse(TwoFeatureYaml);
            Assert.Equal(2, config.Features.Count);
            Assert.Equal("MainApp",  config.Features[0].Id);
            Assert.Equal("CliTools", config.Features[1].Id);
        }

        [Fact]
        public void Features_Names_ParseCorrectly()
        {
            var config = Parse(TwoFeatureYaml);
            Assert.Equal("Application",       config.Features[0].Name);
            Assert.Equal("Command-line Tools", config.Features[1].Name);
        }

        [Fact]
        public void Features_DefaultTrue_ParsesCorrectly()
        {
            var config = Parse(TwoFeatureYaml);
            Assert.True(config.Features[0].Default);
        }

        [Fact]
        public void Features_DefaultFalse_ParsesCorrectly()
        {
            var config = Parse(TwoFeatureYaml);
            Assert.False(config.Features[1].Default);
        }

        [Fact]
        public void Features_Description_ParsesCorrectly()
        {
            var config = Parse(TwoFeatureYaml);
            Assert.Equal("Installs CLI tools", config.Features[1].Description);
        }

        [Fact]
        public void Features_Structure_ParsesCorrectly()
        {
            var config = Parse(TwoFeatureYaml);
            Assert.Single(config.Features[0].Structure);
        }

        // -----------------------------------------------------------------------
        // features: with shortcuts
        // -----------------------------------------------------------------------

        private const string FeatureWithShortcutYaml = @"
product:
  name: Test
  upgradeCode: 00000000-0000-0000-0000-000000000001
features:
  - id: MainApp
    name: Application
    default: true
    structure:
      - source: 'bin:MyApp.exe'
    shortcuts:
      - name: My App
        target: '[INSTALLDIR]\MyApp.exe'
        folder: startmenu
";

        [Fact]
        public void Features_WithShortcuts_ShortcutParsed()
        {
            var config = Parse(FeatureWithShortcutYaml);
            Assert.Single(config.Features[0].Shortcuts);
            Assert.Equal("My App", config.Features[0].Shortcuts[0].Name);
        }

        // -----------------------------------------------------------------------
        // features: with registry entries
        // -----------------------------------------------------------------------

        private const string FeatureWithRegistryYaml = @"
product:
  name: Test
  upgradeCode: 00000000-0000-0000-0000-000000000001
features:
  - id: MainApp
    name: Application
    default: true
    registry:
      - root: HKLM
        key: 'SOFTWARE\MyApp'
        name: Version
        value: '1.0'
        type: string
";

        [Fact]
        public void Features_WithRegistry_RegistryEntryParsed()
        {
            var config = Parse(FeatureWithRegistryYaml);
            Assert.Single(config.Features[0].Registry);
            Assert.Equal("HKLM", config.Features[0].Registry[0].Root);
        }

        // -----------------------------------------------------------------------
        // features: with services
        // -----------------------------------------------------------------------

        private const string FeatureWithServiceYaml = @"
product:
  name: Test
  upgradeCode: 00000000-0000-0000-0000-000000000001
features:
  - id: ServerFeature
    name: Server
    default: false
    services:
      - name: MyAppWorker
        displayName: My App Worker
        executable: '[INSTALLDIR]\MyAppWorker.exe'
        start: auto
        account: LocalSystem
";

        [Fact]
        public void Features_WithService_ServiceParsed()
        {
            var config = Parse(FeatureWithServiceYaml);
            Assert.Single(config.Features[0].Services);
            Assert.Equal("MyAppWorker", config.Features[0].Services[0].Name);
            Assert.Equal("auto",        config.Features[0].Services[0].Start);
        }

        // -----------------------------------------------------------------------
        // features: with groups
        // -----------------------------------------------------------------------

        private const string FeatureWithGroupYaml = @"
product:
  name: Test
  upgradeCode: 00000000-0000-0000-0000-000000000001
directories:
  - id: PSMODDIR
    type: psmodules51
    subPath: MyApp
features:
  - id: PSModule
    name: PowerShell Integration
    default: false
    groups:
      - id: PSMod
        destinationDir: PSMODDIR
        files:
          - source: 'scripts:MyApp.psm1'
";

        [Fact]
        public void Features_WithGroups_GroupParsed()
        {
            var config = Parse(FeatureWithGroupYaml);
            Assert.Single(config.Features[0].Groups);
            Assert.Equal("PSMod", config.Features[0].Groups[0].Id);
        }

        // -----------------------------------------------------------------------
        // Top-level structure is independent of features
        // -----------------------------------------------------------------------

        private const string TopLevelAndFeatureYaml = @"
product:
  name: Test
  upgradeCode: 00000000-0000-0000-0000-000000000001
structure:
  - source: 'bin:MyApp.exe'
features:
  - id: Optional
    name: Optional Feature
    default: false
    structure:
      - source: 'bin:optional.exe'
";

        [Fact]
        public void TopLevelStructure_And_FeaturesStructure_AreSeparate()
        {
            var config = Parse(TopLevelAndFeatureYaml);
            Assert.Single(config.Structure);
            Assert.Single(config.Features);
            Assert.Single(config.Features[0].Structure);
        }
    }
}
