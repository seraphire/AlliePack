using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Xunit;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using AlliePack;

namespace AlliePack.Tests
{
    public class VersionSourceTests
    {
        private static PathResolver MakeResolver(string? workDir = null)
        {
            string yamlPath = workDir != null
                ? Path.Combine(workDir, "test.yaml")
                : Path.Combine(Path.GetTempPath(), "test.yaml");
            return new PathResolver(
                yamlPath,
                aliases: new Dictionary<string, string>(),
                paths:   new Dictionary<string, string>());
        }

        private static AlliePackConfig ParseConfig(string yaml)
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .WithTypeConverter(new ConditionalStringConverter())
                .WithTypeConverter(new VersionSourceConverter())
                .Build();
            return deserializer.Deserialize<AlliePackConfig>(yaml);
        }

        // -----------------------------------------------------------------------
        // Literal version
        // -----------------------------------------------------------------------

        [Fact]
        public void Literal_ReturnsExactString()
        {
            var vs = new VersionSource("1.2.3.4");
            string result = vs.Resolve(MakeResolver());
            Assert.Equal("1.2.3.4", result);
        }

        [Fact]
        public void Literal_ParsedFromYaml_ReturnsExactString()
        {
            var config = ParseConfig(@"
product:
  name: Test
  upgradeCode: 00000000-0000-0000-0000-000000000001
  version: '2.5.0.0'
");
            string result = config.Product.Version.Resolve(MakeResolver());
            Assert.Equal("2.5.0.0", result);
        }

        // -----------------------------------------------------------------------
        // YAML parsing of version block forms
        // -----------------------------------------------------------------------

        [Fact]
        public void VersionBlock_FileVersion_ParsedFromYaml()
        {
            var config = ParseConfig(@"
product:
  name: Test
  upgradeCode: 00000000-0000-0000-0000-000000000001
  version:
    file: 'bin:MyApp.exe'
    source: file-version
");
            Assert.NotNull(config.Product.Version);
        }

        [Fact]
        public void VersionBlock_GitTag_ParsedFromYaml()
        {
            var config = ParseConfig(@"
product:
  name: Test
  upgradeCode: 00000000-0000-0000-0000-000000000001
  version:
    source: git-tag
    tagPrefix: v
");
            Assert.NotNull(config.Product.Version);
        }

        // -----------------------------------------------------------------------
        // File version -- reads from a real PE binary
        // -----------------------------------------------------------------------

        [Fact]
        public void FileVersion_FromClrDll_ReturnsFourPartVersion()
        {
            string clr = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "clr.dll");
            if (!File.Exists(clr))
            {
                // Skip gracefully if clr.dll isn't present in this runtime
                return;
            }

            var vs = VersionSource.ForFile(clr, "file-version");
            string result = vs.Resolve(MakeResolver());

            // Result must be non-empty and parseable as version-like (e.g. "4.8.9260.0")
            Assert.False(string.IsNullOrEmpty(result));
            Assert.Matches(@"^\d+\.\d+", result);
        }

        [Fact]
        public void ProductVersion_FromClrDll_ReturnsFourPartVersion()
        {
            string clr = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "clr.dll");
            if (!File.Exists(clr)) return;

            var vs = VersionSource.ForFile(clr, "product-version");
            string result = vs.Resolve(MakeResolver());

            Assert.False(string.IsNullOrEmpty(result));
        }

        // -----------------------------------------------------------------------
        // Git-tag version -- runs against the AlliePack repo itself
        // -----------------------------------------------------------------------

        private static string? GetRepoRoot()
        {
            // Walk up from test assembly location to find .git
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.Exists(Path.Combine(dir, ".git")))
                    return dir;
                string? parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent!;
            }
            return null;
        }

        private static bool GitIsAvailable()
        {
            try
            {
                var psi = new ProcessStartInfo("git", "--version")
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };
                using var p = Process.Start(psi);
                p?.WaitForExit();
                return p?.ExitCode == 0;
            }
            catch { return false; }
        }

        [Fact]
        public void GitTag_WithRepo_ReturnsFourPartVersion()
        {
            if (!GitIsAvailable()) return;

            string? repoRoot = GetRepoRoot();
            if (repoRoot == null) return;

            var vs = VersionSource.ForGit("v");
            string result = vs.Resolve(MakeResolver(repoRoot));

            // Must be in Major.Minor.Patch.N format
            Assert.Matches(@"^\d+\.\d+\.\d+\.\d+$", result);
        }

        [Fact]
        public void GitTag_InDirWithNoGit_ReturnsFallback()
        {
            if (!GitIsAvailable()) return;

            // Use a temp directory that has no git repo
            string tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tmp);
            try
            {
                var vs = VersionSource.ForGit("v");
                string result = vs.Resolve(MakeResolver(tmp));

                // Fallback is 0.0.0.N
                Assert.Matches(@"^0\.0\.0\.\d+$", result);
            }
            finally
            {
                Directory.Delete(tmp, recursive: true);
            }
        }
    }
}
