using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using AlliePack;

namespace AlliePack.Tests
{
    public class ProductConfigParseTests
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

        private const string MinimalProductYaml =
            "product:\n  name: Test\n  upgradeCode: 00000000-0000-0000-0000-000000000001\n";

        // -----------------------------------------------------------------------
        // Add/Remove Programs detail lines
        // -----------------------------------------------------------------------

        [Fact]
        public void ArpFields_AbsentFromYaml_AreNull()
        {
            var config = Parse(MinimalProductYaml);
            Assert.Null(config.Product.Comments);
            Assert.Null(config.Product.Contact);
            Assert.Null(config.Product.HelpLink);
            Assert.Null(config.Product.SupportLink);
            Assert.Null(config.Product.UpdateLink);
        }

        [Fact]
        public void ArpFields_PresentInYaml_ParseCorrectly()
        {
            var yaml = @"
product:
  name: Test
  upgradeCode: 00000000-0000-0000-0000-000000000001
  comments: 'A migration system'
  contact: 'Example LLC'
  helpLink: 'https://example.com/help'
  supportLink: 'https://example.com'
  updateLink: 'https://example.com/releases'
";
            var config = Parse(yaml);
            Assert.Equal("A migration system",            config.Product.Comments);
            Assert.Equal("Example LLC",                   config.Product.Contact);
            Assert.Equal("https://example.com/help",      config.Product.HelpLink);
            Assert.Equal("https://example.com",           config.Product.SupportLink);
            Assert.Equal("https://example.com/releases",  config.Product.UpdateLink);
        }

        // -----------------------------------------------------------------------
        // installDir
        // -----------------------------------------------------------------------

        [Fact]
        public void InstallDir_AbsentFromYaml_IsNull()
        {
            var config = Parse(MinimalProductYaml);
            Assert.Null(config.Product.InstallDir);
        }

        [Fact]
        public void InstallDir_ScalarValue_ParsesCorrectly()
        {
            var yaml = @"
product:
  name: Test
  upgradeCode: 00000000-0000-0000-0000-000000000001
  installDir: '[ProgramFiles]\MyCompany\MyApp'
";
            var config = Parse(yaml);
            Assert.NotNull(config.Product.InstallDir);
            Assert.Equal("[ProgramFiles]\\MyCompany\\MyApp", config.Product.InstallDir!.Resolve(System.Array.Empty<string>()));
        }

        [Fact]
        public void InstallDir_ConditionalMap_ResolvesPerFlag()
        {
            var yaml = @"
product:
  name: Test
  upgradeCode: 00000000-0000-0000-0000-000000000001
  installDir:
    PerUser:    '[LocalAppDataFolder]\MyApp'
    PerMachine: '[ProgramFiles]\MyApp'
    _else:      '[LocalAppDataFolder]\MyApp'
";
            var config = Parse(yaml);
            Assert.NotNull(config.Product.InstallDir);

            Assert.Equal("[ProgramFiles]\\MyApp",        config.Product.InstallDir!.Resolve(new[] { "PerMachine" }));
            Assert.Equal("[LocalAppDataFolder]\\MyApp", config.Product.InstallDir!.Resolve(new[] { "PerUser" }));
        }
    }
}
