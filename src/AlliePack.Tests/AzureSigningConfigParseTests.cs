using System.Collections.Generic;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using AlliePack;

namespace AlliePack.Tests
{
    public class AzureSigningConfigParseTests
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
";

        private const string AzureBlock = @"  azure:
    endpoint: ""https://eus.codesigning.azure.net""
    account: MyAccount
    certificateProfile: MyProfile
    dlibPath: 'C:\Tools\x64\Azure.CodeSigning.Dlib.dll'
";

        // -----------------------------------------------------------------------
        // azure: block parsing
        // -----------------------------------------------------------------------

        [Fact]
        public void Azure_AbsentFromYaml_IsNull()
        {
            var config = Parse(BaseYaml + "  thumbprint: ABC123\n");
            Assert.Null(config.Signing!.Azure);
        }

        [Fact]
        public void Azure_RequiredFields_ParseCorrectly()
        {
            var config = Parse(BaseYaml + AzureBlock);
            var az = config.Signing!.Azure!;
            Assert.Equal("https://eus.codesigning.azure.net", az.Endpoint);
            Assert.Equal("MyAccount", az.Account);
            Assert.Equal("MyProfile", az.CertificateProfile);
            Assert.Equal(@"C:\Tools\x64\Azure.CodeSigning.Dlib.dll", az.DlibPath);
        }

        [Fact]
        public void Azure_CorrelationId_ParsesWhenPresent()
        {
            var config = Parse(BaseYaml + AzureBlock + "    correlationId: \"[BUILD_ID]\"\n");
            Assert.Equal("[BUILD_ID]", config.Signing!.Azure!.CorrelationId);
        }

        [Fact]
        public void Azure_CorrelationId_AbsentIsNull()
        {
            var config = Parse(BaseYaml + AzureBlock);
            Assert.Null(config.Signing!.Azure!.CorrelationId);
        }

        [Fact]
        public void Azure_DlibPath_AbsentIsNull()
        {
            string yaml = BaseYaml + @"  azure:
    endpoint: ""https://eus.codesigning.azure.net""
    account: MyAccount
    certificateProfile: MyProfile
";
            var config = Parse(yaml);
            Assert.Null(config.Signing!.Azure!.DlibPath);
        }

        [Fact]
        public void Azure_WithTimestampUrl_ParsesCorrectly()
        {
            var config = Parse(BaseYaml + AzureBlock + "  timestampUrl: \"http://timestamp.acs.microsoft.com\"\n");
            Assert.Equal("http://timestamp.acs.microsoft.com", config.Signing!.TimestampUrl);
        }

        // -----------------------------------------------------------------------
        // command: parsing
        // -----------------------------------------------------------------------

        [Fact]
        public void Command_AbsentFromYaml_IsNull()
        {
            var config = Parse(BaseYaml + "  thumbprint: ABC123\n");
            Assert.Null(config.Signing!.Command);
        }

        [Fact]
        public void Command_ParsesCorrectly()
        {
            var config = Parse(BaseYaml + "  command: 'AzureSignTool.exe sign \"{file}\"'\n");
            Assert.Equal("AzureSignTool.exe sign \"{file}\"", config.Signing!.Command);
        }

        [Fact]
        public void Command_SupportsTokenPlaceholder()
        {
            var config = Parse(BaseYaml + "  command: 'sign.exe -kvu \"[KV_URL]\" \"{file}\"'\n");
            Assert.Contains("[KV_URL]", config.Signing!.Command);
            Assert.Contains("{file}", config.Signing!.Command);
        }
    }
}
