using System.Collections.Generic;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using AlliePack;

namespace AlliePack.Tests
{
    public class SigningConfigParseTests
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

        [Fact]
        public void Signing_AbsentFromYaml_IsNull()
        {
            var config = Parse("product:\n  name: Test\n  upgradeCode: 00000000-0000-0000-0000-000000000001\n");
            Assert.Null(config.Signing);
        }

        [Fact]
        public void Signing_Thumbprint_ParsesCorrectly()
        {
            var config = Parse(@"
product:
  name: Test
  upgradeCode: 00000000-0000-0000-0000-000000000001
signing:
  thumbprint: ABCDEF1234567890
  timestampUrl: http://timestamp.digicert.com
");
            Assert.NotNull(config.Signing);
            Assert.Equal("ABCDEF1234567890", config.Signing!.Thumbprint);
            Assert.Equal("http://timestamp.digicert.com", config.Signing.TimestampUrl);
            Assert.Null(config.Signing.Pfx);
            Assert.Null(config.Signing.PfxPassword);
            Assert.Null(config.Signing.SignToolPath);
        }

        [Fact]
        public void Signing_Pfx_ParsesCorrectly()
        {
            var config = Parse(@"
product:
  name: Test
  upgradeCode: 00000000-0000-0000-0000-000000000001
signing:
  pfx: certs/MyApp.pfx
  pfxPassword: '[SIGN_PASSWORD]'
  timestampUrl: http://timestamp.digicert.com
");
            Assert.NotNull(config.Signing);
            Assert.Equal("certs/MyApp.pfx", config.Signing!.Pfx);
            Assert.Equal("[SIGN_PASSWORD]", config.Signing.PfxPassword);
            Assert.Equal("http://timestamp.digicert.com", config.Signing.TimestampUrl);
            Assert.Null(config.Signing.Thumbprint);
        }

        [Fact]
        public void Signing_SignToolPath_ParsesCorrectly()
        {
            var config = Parse(@"
product:
  name: Test
  upgradeCode: 00000000-0000-0000-0000-000000000001
signing:
  thumbprint: ABC123
  signToolPath: C:/tools/signtool.exe
");
            Assert.Equal("C:/tools/signtool.exe", config.Signing!.SignToolPath);
        }

        [Fact]
        public void Signing_NoTimestampUrl_IsNull()
        {
            var config = Parse(@"
product:
  name: Test
  upgradeCode: 00000000-0000-0000-0000-000000000001
signing:
  thumbprint: ABC123
");
            Assert.Null(config.Signing!.TimestampUrl);
        }
    }
}
