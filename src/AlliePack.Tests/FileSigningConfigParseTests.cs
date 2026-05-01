using System.Collections.Generic;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using AlliePack;

namespace AlliePack.Tests
{
    public class FileSigningConfigParseTests
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

        private const string BaseYaml = @"
product:
  name: Test
  upgradeCode: 00000000-0000-0000-0000-000000000001
signing:
  thumbprint: ABC123
";

        [Fact]
        public void Files_AbsentFromYaml_IsNull()
        {
            var config = Parse(BaseYaml);
            Assert.Null(config.Signing!.Files);
        }

        [Fact]
        public void Files_EmptyBlock_UsesDefaults()
        {
            var config = Parse(BaseYaml + "  files:\n    mode: unsigned\n");
            var f = config.Signing!.Files!;
            Assert.Equal("unsigned", f.Mode);
            Assert.Null(f.Include);
            Assert.Empty(f.Exclude);
        }

        [Fact]
        public void Files_ModeAll_ParsesCorrectly()
        {
            var config = Parse(BaseYaml + "  files:\n    mode: all\n");
            Assert.Equal("all", config.Signing!.Files!.Mode);
        }

        [Fact]
        public void Files_Include_ParsesAsList()
        {
            var config = Parse(BaseYaml + @"  files:
    include: ['*.exe', '*.dll']
");
            var include = config.Signing!.Files!.Include;
            Assert.NotNull(include);
            Assert.Equal(2, include!.Count);
            Assert.Contains("*.exe", include);
            Assert.Contains("*.dll", include);
        }

        [Fact]
        public void Files_Exclude_ParsesAsList()
        {
            var config = Parse(BaseYaml + @"  files:
    exclude: ['*.resources.dll']
");
            Assert.Single(config.Signing!.Files!.Exclude);
            Assert.Equal("*.resources.dll", config.Signing.Files.Exclude[0]);
        }

        [Fact]
        public void Files_IncludeAndExclude_BothParse()
        {
            var config = Parse(BaseYaml + @"  files:
    mode: unsigned
    include: ['*.exe', '*.dll']
    exclude: ['*.resources.dll', '*.vshost.exe']
");
            var f = config.Signing!.Files!;
            Assert.Equal("unsigned", f.Mode);
            Assert.Equal(2, f.Include!.Count);
            Assert.Equal(2, f.Exclude.Count);
        }
    }
}
