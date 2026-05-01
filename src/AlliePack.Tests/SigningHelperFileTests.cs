using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using AlliePack;

namespace AlliePack.Tests
{
    public class SigningHelperFileTests
    {
        // -----------------------------------------------------------------------
        // IsSipSignable -- delegates to the same Windows SIP table signtool uses
        // -----------------------------------------------------------------------

        [Fact]
        public void IsSipSignable_PeExecutable_ReturnsTrue()
        {
            // cmd.exe is a standard PE file present on every Windows machine
            string cmd = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            Assert.True(SigningHelper.IsSipSignable(cmd));
        }

        [Fact]
        public void IsSipSignable_TextFile_ReturnsFalse()
        {
            string tmp = Path.GetTempFileName(); // creates a .tmp file (plain bytes, no SIP)
            try
            {
                File.WriteAllText(tmp, "hello world");
                Assert.False(SigningHelper.IsSipSignable(tmp));
            }
            finally { File.Delete(tmp); }
        }

        [Fact]
        public void IsSipSignable_NonExistentFile_ReturnsFalse()
        {
            Assert.False(SigningHelper.IsSipSignable(@"C:\does\not\exist.exe"));
        }

        // -----------------------------------------------------------------------
        // HasAuthenticodeSignature
        // -----------------------------------------------------------------------

        [Fact]
        public void HasAuthenticodeSignature_MicrosoftSignedBinary_ReturnsTrue()
        {
            // clr.dll carries an embedded Authenticode signature (cmd.exe is catalog-signed only)
            string clr = Path.Combine(
                System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "clr.dll");
            Assert.True(SigningHelper.HasAuthenticodeSignature(clr));
        }

        [Fact]
        public void HasAuthenticodeSignature_UnsignedFile_ReturnsFalse()
        {
            string tmp = Path.GetTempFileName();
            try
            {
                // Write a valid PE header stub -- enough for SIP detection but no signature
                File.WriteAllBytes(tmp, new byte[] { 0x4D, 0x5A, 0x00, 0x00 }); // MZ header
                Assert.False(SigningHelper.HasAuthenticodeSignature(tmp));
            }
            finally { File.Delete(tmp); }
        }

        [Fact]
        public void HasAuthenticodeSignature_TextFile_ReturnsFalse()
        {
            string tmp = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tmp, "not a binary");
                Assert.False(SigningHelper.HasAuthenticodeSignature(tmp));
            }
            finally { File.Delete(tmp); }
        }

        // -----------------------------------------------------------------------
        // MatchesFilename -- glob patterns against filename only
        // -----------------------------------------------------------------------

        [Theory]
        [InlineData("*.exe",            "MyApp.exe",            true)]
        [InlineData("*.dll",            "MyLib.dll",            true)]
        [InlineData("*.resources.dll",  "MyApp.resources.dll",  true)]
        [InlineData("*.exe",            "MyLib.dll",            false)]
        [InlineData("*.exe",            "MyApp.exe.config",     false)]
        [InlineData("My*.exe",          "MyApp.exe",            true)]
        [InlineData("My*.exe",          "OtherApp.exe",         false)]
        public void MatchesFilename_GlobPatterns(string pattern, string fileName, bool expected)
        {
            Assert.Equal(expected, SigningHelper.MatchesFilename(new[] { pattern }, fileName));
        }

        [Fact]
        public void MatchesFilename_MultiplePatterns_MatchesIfAnyMatches()
        {
            var patterns = new[] { "*.exe", "*.dll" };
            Assert.True(SigningHelper.MatchesFilename(patterns, "MyApp.exe"));
            Assert.True(SigningHelper.MatchesFilename(patterns, "MyLib.dll"));
            Assert.False(SigningHelper.MatchesFilename(patterns, "readme.txt"));
        }

        [Fact]
        public void MatchesFilename_EmptyPatterns_NoMatch()
        {
            Assert.False(SigningHelper.MatchesFilename(Array.Empty<string>(), "MyApp.exe"));
        }
    }
}
