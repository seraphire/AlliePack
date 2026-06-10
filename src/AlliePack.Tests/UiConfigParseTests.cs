using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using AlliePack;

namespace AlliePack.Tests
{
    public class UiConfigParseTests
    {
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

        // -----------------------------------------------------------------------
        // ui: absent
        // -----------------------------------------------------------------------

        [Fact]
        public void Ui_AbsentFromYaml_DefaultsToStandard()
        {
            var config = Parse(MinimalYaml);
            Assert.Equal("standard", config.Ui.Type);
        }

        [Fact]
        public void Ui_AbsentFromYaml_AllowInstallDirChange_DefaultsTrue()
        {
            var config = Parse(MinimalYaml);
            Assert.True(config.Ui.AllowInstallDirChange);
        }

        // -----------------------------------------------------------------------
        // ui: scalar shorthand
        // -----------------------------------------------------------------------

        [Fact]
        public void Ui_ScalarStandard_ParsesType()
        {
            var config = Parse(MinimalYaml + "ui: standard\n");
            Assert.Equal("standard", config.Ui.Type);
        }

        [Fact]
        public void Ui_ScalarCustom_ParsesType()
        {
            var config = Parse(MinimalYaml + "ui: custom\n");
            Assert.Equal("custom", config.Ui.Type);
        }

        [Fact]
        public void Ui_ScalarShorthand_AllowInstallDirChange_DefaultsTrue()
        {
            var config = Parse(MinimalYaml + "ui: standard\n");
            Assert.True(config.Ui.AllowInstallDirChange);
        }

        // -----------------------------------------------------------------------
        // ui: block form
        // -----------------------------------------------------------------------

        [Fact]
        public void Ui_Block_TypeParsesCorrectly()
        {
            var yaml = MinimalYaml + "ui:\n  type: custom\n";
            Assert.Equal("custom", Parse(yaml).Ui.Type);
        }

        [Fact]
        public void Ui_Block_AllowInstallDirChange_True()
        {
            var yaml = MinimalYaml + "ui:\n  type: standard\n  allowInstallDirChange: true\n";
            Assert.True(Parse(yaml).Ui.AllowInstallDirChange);
        }

        [Fact]
        public void Ui_Block_AllowInstallDirChange_False()
        {
            var yaml = MinimalYaml + "ui:\n  type: standard\n  allowInstallDirChange: false\n";
            Assert.False(Parse(yaml).Ui.AllowInstallDirChange);
        }

        [Fact]
        public void Ui_Block_TypeOmitted_DefaultsToStandard()
        {
            var yaml = MinimalYaml + "ui:\n  allowInstallDirChange: false\n";
            Assert.Equal("standard", Parse(yaml).Ui.Type);
        }
    }
}
