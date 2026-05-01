using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using AlliePack;

namespace AlliePack.Tests
{
    public class SigningHelperTests
    {
        // Build a minimal PathResolver rooted at the system temp directory.
        // Passes rooted paths through unchanged; supports token substitution
        // via the defines dictionary.
        private static PathResolver MakeResolver(Dictionary<string, string>? defines = null)
        {
            string yamlPath = Path.Combine(Path.GetTempPath(), "test.yaml");
            return new PathResolver(
                yamlPath,
                aliases: new Dictionary<string, string>(),
                paths:   new Dictionary<string, string>(),
                defines: defines);
        }

        // -----------------------------------------------------------------------
        // BuildArgs -- thumbprint
        // -----------------------------------------------------------------------

        [Fact]
        public void BuildArgs_Thumbprint_ContainsShorthandFlag()
        {
            var signing = new SigningConfig { Thumbprint = "ABCDEF1234" };
            string args = SigningHelper.BuildArgs(signing, @"C:\out\MyApp.msi", MakeResolver());
            Assert.Contains("/sha1 ABCDEF1234", args);
        }

        [Fact]
        public void BuildArgs_Thumbprint_NoPfxFlags()
        {
            var signing = new SigningConfig { Thumbprint = "ABCDEF1234" };
            string args = SigningHelper.BuildArgs(signing, @"C:\out\MyApp.msi", MakeResolver());
            Assert.DoesNotContain("/f ", args);
            Assert.DoesNotContain("/p ", args);
        }

        [Fact]
        public void BuildArgs_Thumbprint_AlwaysIncludesFileDigest()
        {
            var signing = new SigningConfig { Thumbprint = "ABCDEF1234" };
            string args = SigningHelper.BuildArgs(signing, @"C:\out\MyApp.msi", MakeResolver());
            Assert.Contains("/fd sha256", args);
        }

        // -----------------------------------------------------------------------
        // BuildArgs -- PFX
        // -----------------------------------------------------------------------

        [Fact]
        public void BuildArgs_Pfx_ContainsPfxFlag()
        {
            var signing = new SigningConfig { Pfx = @"C:\certs\MyApp.pfx" };
            string args = SigningHelper.BuildArgs(signing, @"C:\out\MyApp.msi", MakeResolver());
            Assert.Contains(@"/f ""C:\certs\MyApp.pfx""", args);
        }

        [Fact]
        public void BuildArgs_Pfx_WithPassword_ContainsPasswordFlag()
        {
            var signing = new SigningConfig { Pfx = @"C:\certs\MyApp.pfx", PfxPassword = "s3cr3t" };
            string args = SigningHelper.BuildArgs(signing, @"C:\out\MyApp.msi", MakeResolver());
            Assert.Contains(@"/p ""s3cr3t""", args);
        }

        [Fact]
        public void BuildArgs_Pfx_PasswordViaToken_SubstitutedCorrectly()
        {
            var signing = new SigningConfig { Pfx = @"C:\certs\MyApp.pfx", PfxPassword = "[SIGN_PASSWORD]" };
            var resolver = MakeResolver(new Dictionary<string, string> { ["SIGN_PASSWORD"] = "injected" });
            string args = SigningHelper.BuildArgs(signing, @"C:\out\MyApp.msi", resolver);
            Assert.Contains(@"/p ""injected""", args);
        }

        [Fact]
        public void BuildArgs_Pfx_WithoutPassword_NoPasswordFlag()
        {
            var signing = new SigningConfig { Pfx = @"C:\certs\MyApp.pfx" };
            string args = SigningHelper.BuildArgs(signing, @"C:\out\MyApp.msi", MakeResolver());
            Assert.DoesNotContain("/p ", args);
        }

        // -----------------------------------------------------------------------
        // BuildArgs -- timestamp
        // -----------------------------------------------------------------------

        [Fact]
        public void BuildArgs_WithTimestamp_ContainsRfc3161Flags()
        {
            var signing = new SigningConfig
            {
                Thumbprint   = "ABCDEF1234",
                TimestampUrl = "http://timestamp.digicert.com",
            };
            string args = SigningHelper.BuildArgs(signing, @"C:\out\MyApp.msi", MakeResolver());
            Assert.Contains("/tr http://timestamp.digicert.com", args);
            Assert.Contains("/td sha256", args);
        }

        [Fact]
        public void BuildArgs_WithoutTimestamp_NoTimestampFlags()
        {
            var signing = new SigningConfig { Thumbprint = "ABCDEF1234" };
            string args = SigningHelper.BuildArgs(signing, @"C:\out\MyApp.msi", MakeResolver());
            Assert.DoesNotContain("/tr", args);
            Assert.DoesNotContain("/td", args);
        }

        // -----------------------------------------------------------------------
        // BuildArgs -- output path
        // -----------------------------------------------------------------------

        [Fact]
        public void BuildArgs_MsiPath_IsQuotedAndLast()
        {
            var signing = new SigningConfig { Thumbprint = "ABCDEF1234" };
            string msi  = @"C:\out\My App.msi";
            string args = SigningHelper.BuildArgs(signing, msi, MakeResolver());
            Assert.EndsWith($@" ""{msi}""", args);
        }

        // -----------------------------------------------------------------------
        // BuildArgs -- validation errors
        // -----------------------------------------------------------------------

        [Fact]
        public void BuildArgs_NeitherThumbprintNorPfx_Throws()
        {
            var signing = new SigningConfig();
            var ex = Assert.Throws<InvalidOperationException>(
                () => SigningHelper.BuildArgs(signing, @"C:\out\MyApp.msi", MakeResolver()));
            Assert.Contains("thumbprint", ex.Message);
            Assert.Contains("pfx", ex.Message);
        }

        [Fact]
        public void BuildArgs_BothThumbprintAndPfx_Throws()
        {
            var signing = new SigningConfig { Thumbprint = "ABC", Pfx = @"C:\cert.pfx" };
            var ex = Assert.Throws<InvalidOperationException>(
                () => SigningHelper.BuildArgs(signing, @"C:\out\MyApp.msi", MakeResolver()));
            Assert.Contains("thumbprint", ex.Message);
            Assert.Contains("pfx", ex.Message);
        }

        // -----------------------------------------------------------------------
        // FindSignTool -- config path validation
        // -----------------------------------------------------------------------

        [Fact]
        public void FindSignTool_ConfigPathDoesNotExist_ThrowsWithPath()
        {
            var signing = new SigningConfig { SignToolPath = @"C:\does\not\exist\signtool.exe" };
            var ex = Assert.Throws<InvalidOperationException>(
                () => SigningHelper.FindSignTool(signing.SignToolPath, MakeResolver()));
            Assert.Contains("signtool not found at configured path", ex.Message);
        }

        [Fact]
        public void FindSignTool_ConfigPathExists_ReturnsPath()
        {
            // Write a placeholder file so the existence check passes.
            string tempPath = Path.Combine(Path.GetTempPath(), "signtool_test.exe");
            System.IO.File.WriteAllText(tempPath, "placeholder");
            try
            {
                string result = SigningHelper.FindSignTool(tempPath, MakeResolver());
                Assert.Equal(tempPath, result);
            }
            finally
            {
                System.IO.File.Delete(tempPath);
            }
        }
    }
}
