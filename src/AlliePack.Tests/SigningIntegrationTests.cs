using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;
using AlliePack;

namespace AlliePack.Tests
{
    /// <summary>
    /// End-to-end signing tests that actually invoke signtool.exe.
    /// All tests skip gracefully when signtool is not found (e.g. SDK not installed).
    /// </summary>
    public class SigningIntegrationTests
    {
        private static PathResolver MakeResolver()
        {
            string yamlPath = Path.Combine(Path.GetTempPath(), "test.yaml");
            return new PathResolver(
                yamlPath,
                aliases: new Dictionary<string, string>(),
                paths:   new Dictionary<string, string>());
        }

        // Returns the path to signtool.exe, or null if it isn't available.
        private static string? TryFindSignTool()
        {
            try { return SigningHelper.FindSignTool(null, MakeResolver()); }
            catch { return null; }
        }

        // Creates a self-signed code-signing certificate entirely in memory,
        // exports it as a PFX byte array, and returns the bytes.
        // No certificate store is touched.
        private static byte[] CreateCodeSigningPfx(string password)
        {
            using var rsa = RSA.Create(2048);

            var req = new CertificateRequest(
                "CN=AlliePack Integration Test",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            // Code signing extended key usage
            req.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") },
                    critical: true));

            req.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature,
                    critical: true));

            var now  = DateTimeOffset.UtcNow.AddMinutes(-5);
            var end  = now.AddDays(365);
            using var cert = req.CreateSelfSigned(now, end);

            return cert.Export(X509ContentType.Pfx, password);
        }

        // -----------------------------------------------------------------------
        // PFX signing -- end-to-end: create PFX, sign a PE binary, verify
        // -----------------------------------------------------------------------

        [Fact]
        public void Sign_WithPfx_BinaryGetsAuthenticodeSignature()
        {
            string? signtool = TryFindSignTool();
            if (signtool == null) return; // signtool.exe not available -- skip

            const string password = "TestPass123!";
            byte[] pfxBytes = CreateCodeSigningPfx(password);

            string pfxPath    = Path.GetTempFileName() + ".pfx";
            string targetPath = Path.GetTempFileName() + ".dll";
            try
            {
                File.WriteAllBytes(pfxPath, pfxBytes);

                // Copy a known PE binary so we can mutate it safely
                string clr = Path.Combine(
                    RuntimeEnvironment.GetRuntimeDirectory(), "clr.dll");
                File.Copy(clr, targetPath, overwrite: true);

                var signing = new SigningConfig
                {
                    Pfx         = pfxPath,
                    PfxPassword = password,
                    // No timestampUrl -- we're signing offline with a test cert
                };

                SigningHelper.Sign(targetPath, signing, MakeResolver(), verbose: false);

                Assert.True(
                    SigningHelper.HasAuthenticodeSignature(targetPath),
                    "Expected an Authenticode signature after signing.");
            }
            finally
            {
                TryDelete(pfxPath);
                TryDelete(targetPath);
            }
        }

        // -----------------------------------------------------------------------
        // PFX signing -- wrong password should throw
        // -----------------------------------------------------------------------

        [Fact]
        public void Sign_WithWrongPfxPassword_Throws()
        {
            string? signtool = TryFindSignTool();
            if (signtool == null) return;

            byte[] pfxBytes = CreateCodeSigningPfx("CorrectPass!");

            string pfxPath    = Path.GetTempFileName() + ".pfx";
            string targetPath = Path.GetTempFileName() + ".dll";
            try
            {
                File.WriteAllBytes(pfxPath, pfxBytes);

                string clr = Path.Combine(
                    RuntimeEnvironment.GetRuntimeDirectory(), "clr.dll");
                File.Copy(clr, targetPath, overwrite: true);

                var signing = new SigningConfig
                {
                    Pfx         = pfxPath,
                    PfxPassword = "WrongPass!",
                };

                Assert.Throws<InvalidOperationException>(
                    () => SigningHelper.Sign(targetPath, signing, MakeResolver(), verbose: false));
            }
            finally
            {
                TryDelete(pfxPath);
                TryDelete(targetPath);
            }
        }

        // -----------------------------------------------------------------------
        // SignFiles -- signs individual files with mode: all
        // -----------------------------------------------------------------------

        [Fact]
        public void SignFiles_ModeAll_SignsEligibleFiles()
        {
            string? signtool = TryFindSignTool();
            if (signtool == null) return;

            const string password = "TestPass123!";
            byte[] pfxBytes = CreateCodeSigningPfx(password);

            string pfxPath    = Path.GetTempFileName() + ".pfx";
            string targetPath = Path.GetTempFileName() + ".dll";
            try
            {
                File.WriteAllBytes(pfxPath, pfxBytes);

                string clr = Path.Combine(
                    RuntimeEnvironment.GetRuntimeDirectory(), "clr.dll");
                File.Copy(clr, targetPath, overwrite: true);

                var signing = new SigningConfig
                {
                    Pfx         = pfxPath,
                    PfxPassword = password,
                    Files = new FileSigningConfig
                    {
                        Mode    = "all",
                        Include = new List<string> { "*.dll" },
                    }
                };

                var files = new List<ResolvedFile>
                {
                    new ResolvedFile { SourcePath = targetPath, RelativeDestinationPath = "clr.dll" }
                };

                SigningHelper.SignFiles(files, signing, MakeResolver(), verbose: false);

                Assert.True(
                    SigningHelper.HasAuthenticodeSignature(targetPath),
                    "Expected the file to be signed after SignFiles with mode: all.");
            }
            finally
            {
                TryDelete(pfxPath);
                TryDelete(targetPath);
            }
        }

        // -----------------------------------------------------------------------
        // PFX creation sanity check -- no store access required
        // -----------------------------------------------------------------------

        [Fact]
        public void CreateCodeSigningPfx_ProducesNonEmptyBytes()
        {
            byte[] pfxBytes = CreateCodeSigningPfx("AnyPassword");
            Assert.NotEmpty(pfxBytes);
        }

        [Fact]
        public void CreateCodeSigningPfx_CanRoundTripToCertificate()
        {
            const string password = "RoundTrip!";
            byte[] pfxBytes = CreateCodeSigningPfx(password);

            using var cert = new X509Certificate2(pfxBytes, password,
                X509KeyStorageFlags.EphemeralKeySet);

            Assert.Equal("CN=AlliePack Integration Test", cert.Subject);
            Assert.True(cert.HasPrivateKey);
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
