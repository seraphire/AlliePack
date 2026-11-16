using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;
using AlliePack;

namespace AlliePack.Tests
{
    /// <summary>
    /// signing.files.mode, signing.package, entry sign:, --sign-files / --sign-package /
    /// --sign-cert / --no-sign, choosing the only code-signing certificate, staging,
    /// batching, stopping at the first failure, build.ps1.
    /// </summary>
    public class SigningScopeTests
    {
        private static PathResolver MakeResolver() =>
            new PathResolver(
                Path.Combine(Path.GetTempPath(), "test.yaml"),
                aliases: new Dictionary<string, string>(),
                variables: new Dictionary<string, string>());

        private static SigningConfig WithMode(string mode) =>
            new SigningConfig { Thumbprint = "AB", Files = new FileSigningConfig { Mode = mode } };

        private static readonly string CertC = new string('C', 40);

        // -----------------------------------------------------------------------
        // ApplyOverrides -- files.mode and package
        // -----------------------------------------------------------------------

        [Fact]
        public void NoBlock_NoFlags_NothingByDefault_EntriesCanStillOptIn()
        {
            var s = SigningHelper.ApplyOverrides(null, new Options());
            Assert.False(s!.SignPackage);
            Assert.Equal(FileSignMode.Never, s.ModeFor(null));
            Assert.Equal(FileSignMode.Always, s.ModeFor(FileSignMode.Always));
        }

        [Fact]
        public void BlockOnly_FilesIfUnsigned_PackageSigned_SameKey()
        {
            var s = SigningHelper.ApplyOverrides(new SigningConfig { Thumbprint = "AB" }, new Options());
            Assert.Equal(FileSignMode.IfUnsigned, s!.ModeFor(null));
            Assert.True(s.SignPackage);
            Assert.True(s.PackageInherits);
            Assert.Equal("AB", s.Package.Thumbprint);
        }

        [Theory]
        [InlineData("always", "Always")]
        [InlineData("all", "Always")]
        [InlineData("ifUnsigned", "IfUnsigned")]
        [InlineData("unsigned", "IfUnsigned")]
        [InlineData("never", "Never")]
        [InlineData("NEVER", "Never")]
        public void FilesMode_Values(string mode, string expected)
        {
            Assert.Equal(expected, SigningHelper.ApplyOverrides(WithMode(mode), new Options())!.DefaultFileMode.ToString());
        }

        [Fact]
        public void FilesModeNever_StillSignsThePackage()
        {
            var s = SigningHelper.ApplyOverrides(WithMode("never"), new Options());
            Assert.True(s!.SignPackage);
            Assert.Equal(FileSignMode.Never, s.ModeFor(null));
            Assert.Equal(FileSignMode.IfUnsigned, s.ModeFor(FileSignMode.IfUnsigned));
        }

        [Fact]
        public void PackageSignFalse_LeavesTheMsiUnsigned()
        {
            var cfg = new SigningConfig { Thumbprint = "AB", Package = new PackageSigningConfig { Sign = false } };
            var s = SigningHelper.ApplyOverrides(cfg, new Options());
            Assert.False(s!.SignPackage);
            Assert.Equal(FileSignMode.IfUnsigned, s.ModeFor(null));
        }

        [Fact]
        public void PackageOwnKey_TakesTimestampFromTheBlockWhenUnset()
        {
            var cfg = new SigningConfig
            {
                Thumbprint = "AA", TimestampUrl = "http://ts", SignToolPath = @"C:\st.exe",
                Package = new PackageSigningConfig { Thumbprint = "BB" },
            };
            var s = SigningHelper.ApplyOverrides(cfg, new Options());
            Assert.False(s!.PackageInherits);
            Assert.Equal("AA", s.Provider.Thumbprint);
            Assert.Equal("BB", s.Package.Thumbprint);
            Assert.Equal("http://ts", s.Package.TimestampUrl);
            Assert.Equal(@"C:\st.exe", s.Package.SignToolPath);
        }

        [Fact]
        public void PackageOwnKey_OwnTimestampWins()
        {
            var cfg = new SigningConfig
            {
                Thumbprint = "AA", TimestampUrl = "http://ts",
                Package = new PackageSigningConfig { Pfx = "p.pfx", TimestampUrl = "http://ts2" },
            };
            var s = SigningHelper.ApplyOverrides(cfg, new Options());
            Assert.Equal("p.pfx", s!.Package.Pfx);
            Assert.Null(s.Package.Thumbprint);
            Assert.Equal("http://ts2", s.Package.TimestampUrl);
        }

        [Fact]
        public void PackageTimestamp_WithoutItsOwnKey_IsStillUsed()
        {
            var cfg = new SigningConfig
            {
                Thumbprint = "AA", TimestampUrl = "http://ts",
                Package = new PackageSigningConfig { TimestampUrl = "http://ts2", SignToolPath = @"C:\st.exe" },
            };
            var s = SigningHelper.ApplyOverrides(cfg, new Options());
            Assert.True(s!.PackageInherits);
            Assert.Equal("AA", s.Package.Thumbprint);
            Assert.Equal("http://ts2", s.Package.TimestampUrl);
            Assert.Equal(@"C:\st.exe", s.Package.SignToolPath);
            Assert.Equal("http://ts", s.Provider.TimestampUrl);
        }

        [Fact]
        public void SignCert_KeepsThePackagesOwnTimestamp()
        {
            var cfg = new SigningConfig
            {
                TimestampUrl = "http://ts",
                Package = new PackageSigningConfig { Thumbprint = "BB", TimestampUrl = "http://ts2" },
            };
            var s = SigningHelper.ApplyOverrides(cfg, new Options { SignCert = CertC });
            Assert.Equal(CertC, s!.Package.Thumbprint);
            Assert.Equal("http://ts2", s.Package.TimestampUrl);
        }

        [Fact]
        public void Script_PackageTimestamp_ExportedOnlyWhenWritten()
        {
            var rows = new List<(string, string, string)> { ("a.dll", @"signed\1\a.dll", "always") };
            var parts = InstallerBuilder.ComposeSigningScript(
                rows, new SigningConfig { Thumbprint = "AAA", TimestampUrl = "http://ts" },
                new SigningConfig { Thumbprint = "BBB", TimestampUrl = "http://ts" }, packageInherits: false);
            Assert.Contains("[string]$PackageTimestampUrl = ''", parts.Params);
        }

        [Fact]
        public void Script_FailuresStopTheScript_AndIfUnsignedIgnoresCatalogs()
        {
            var rows = new List<(string, string, string)> { ("src\\a.dll", @"signed\1\a.dll", "ifUnsigned") };
            var parts = InstallerBuilder.ComposeSigningScript(rows, new SigningConfig { Thumbprint = "AAA" }, null);
            Assert.Contains("Copy-Item -LiteralPath $src -Destination $dst -Force -ErrorAction Stop", parts.PreBuild);
            Assert.Contains("signtool could not be run", parts.PreBuild);
            Assert.Contains("-SignTool not found", parts.PreBuild);
            Assert.Contains("Test-EmbeddedSignature $p", parts.PreBuild);
            Assert.DoesNotContain("Get-AuthenticodeSignature -LiteralPath", parts.PreBuild);
        }

        [Fact]
        public void Script_UnsupportedPackageKey_SkipSigningStillReminds()
        {
            var rows = new List<(string, string, string)> { ("a.dll", @"signed\1\a.dll", "always") };
            var parts = InstallerBuilder.ComposeSigningScript(
                rows, new SigningConfig { Thumbprint = "AAA" }, new SigningConfig { Pfx = "p.pfx" }, packageInherits: false);
            Assert.DoesNotContain("$msiPath", parts.PostBuild);
            Assert.Contains("SkipSigning", parts.PostBuild);
        }

        [Fact]
        public void PackageBlockWithoutKey_InheritsTheFilesKey()
        {
            var cfg = new SigningConfig { Pfx = "f.pfx", Package = new PackageSigningConfig() };
            var s = SigningHelper.ApplyOverrides(cfg, new Options());
            Assert.True(s!.PackageInherits);
            Assert.Equal("f.pfx", s.Package.Pfx);
        }

        [Fact]
        public void FilesMode_KeepsIncludeExclude()
        {
            var cfg = new SigningConfig
            {
                Thumbprint = "AB",
                Files = new FileSigningConfig
                {
                    Mode = "always",
                    Include = new List<string> { "*.exe" },
                    Exclude = new List<string> { "x.exe" },
                },
            };
            var s = SigningHelper.ApplyOverrides(cfg, new Options());
            Assert.Equal(FileSignMode.Always, s!.DefaultFileMode);
            Assert.Equal(new[] { "*.exe" }, s.Include);
            Assert.Equal(new[] { "x.exe" }, s.Exclude);
        }

        [Fact]
        public void BadFilesMode_Throws_NamingTheSource()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                SigningHelper.ApplyOverrides(WithMode("everything"), new Options()));
            Assert.Contains("signing.files.mode", ex.Message);
            Assert.Contains("ifUnsigned", ex.Message);
        }

        [Fact]
        public void InPlace_FromConfigOrFlag()
        {
            Assert.False(SigningHelper.ApplyOverrides(new SigningConfig(), new Options())!.InPlace);
            Assert.True(SigningHelper.ApplyOverrides(new SigningConfig { InPlace = true }, new Options())!.InPlace);
            Assert.True(SigningHelper.ApplyOverrides(new SigningConfig(), new Options { SignInPlace = true })!.InPlace);
        }

        [Fact]
        public void ApplyOverrides_DoesNotChangeTheConfig()
        {
            var cfg = new SigningConfig { Thumbprint = "AB", Package = new PackageSigningConfig { Thumbprint = "BB" } };
            SigningHelper.ApplyOverrides(cfg, new Options { SignFiles = "always", SignCert = CertC });
            Assert.Null(cfg.Files);
            Assert.Equal("AB", cfg.Thumbprint);
            Assert.Equal("BB", cfg.Package.Thumbprint);
        }

        // -----------------------------------------------------------------------
        // sign: on entries
        // -----------------------------------------------------------------------

        [Theory]
        [InlineData("always", "Always")]
        [InlineData("ifUnsigned", "IfUnsigned")]
        [InlineData("never", "Never")]
        [InlineData("Never", "Never")]
        public void ParseFileMode_Values(string value, string expected)
        {
            Assert.Equal(expected, SigningHelper.ParseFileMode(value, "test").ToString());
        }

        [Fact]
        public void ParseFileMode_Bad_Throws_NamingTheSource()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => SigningHelper.ParseFileMode("yes", "sign: on group 'G'"));
            Assert.Contains("group 'G'", ex.Message);
            Assert.Contains("always, ifUnsigned or never", ex.Message);
        }

        [Fact]
        public void EntrySign_WinsOverFilesMode()
        {
            var s = SigningHelper.ApplyOverrides(WithMode("always"), new Options());
            Assert.Equal(FileSignMode.Never, s!.ModeFor(FileSignMode.Never));
            Assert.Equal(FileSignMode.Always, s.ModeFor(null));
        }

        [Fact]
        public void CliSignFiles_OverridesEntrySign()
        {
            var s = SigningHelper.ApplyOverrides(new SigningConfig(), new Options { SignFiles = "never" });
            Assert.True(s!.FileModeFromCommandLine);
            Assert.Equal(FileSignMode.Never, s.ModeFor(FileSignMode.Always));

            s = SigningHelper.ApplyOverrides(new SigningConfig(), new Options { SignFiles = "always" });
            Assert.Equal(FileSignMode.Always, s!.ModeFor(FileSignMode.Never));
        }

        // -----------------------------------------------------------------------
        // ApplyOverrides -- command line
        // -----------------------------------------------------------------------

        [Fact]
        public void CliSignFiles_OverridesFilesMode()
        {
            var s = SigningHelper.ApplyOverrides(WithMode("never"), new Options { SignFiles = "ifUnsigned" });
            Assert.Equal(FileSignMode.IfUnsigned, s!.DefaultFileMode);
        }

        [Theory]
        [InlineData("always", true)]
        [InlineData("ifUnsigned", true)]
        public void CliSignFiles_WithoutBlock_SignsFilesAndPackage(string mode, bool package)
        {
            var s = SigningHelper.ApplyOverrides(null, new Options { SignFiles = mode });
            Assert.Equal(package, s!.SignPackage);
            Assert.False(SigningHelper.HasProvider(s.Provider));   // Prepare picks the certificate
        }

        [Fact]
        public void CliSignFilesNever_WithoutBlock_SignsNothing()
        {
            Assert.Null(SigningHelper.ApplyOverrides(null, new Options { SignFiles = "never" }));
        }

        [Fact]
        public void CliSignFilesNever_WithBlock_StillSignsThePackage()
        {
            var s = SigningHelper.ApplyOverrides(new SigningConfig { Thumbprint = "AB" }, new Options { SignFiles = "never" });
            Assert.True(s!.SignPackage);
            Assert.Equal(FileSignMode.Never, s.ModeFor(FileSignMode.Always));
        }

        [Theory]
        [InlineData("no", false)]
        [InlineData("false", false)]
        [InlineData("YES", true)]
        public void CliSignPackage_OverridesPackageSign(string value, bool expected)
        {
            var cfg = new SigningConfig { Thumbprint = "AB", Package = new PackageSigningConfig { Sign = !expected } };
            Assert.Equal(expected, SigningHelper.ApplyOverrides(cfg, new Options { SignPackage = value })!.SignPackage);
        }

        [Fact]
        public void CliSignPackageYes_WithoutBlock_SignsOnlyThePackage()
        {
            var s = SigningHelper.ApplyOverrides(null, new Options { SignPackage = "yes" });
            Assert.True(s!.SignPackage);
            Assert.Equal(FileSignMode.Never, s.ModeFor(null));
        }

        [Fact]
        public void CliNeverAndNoPackage_SignsNothing()
        {
            Assert.Null(SigningHelper.ApplyOverrides(new SigningConfig { Thumbprint = "AB" },
                                                     new Options { SignFiles = "never", SignPackage = "no" }));
        }

        [Fact]
        public void BadCliValues_Throw_NamingTheFlag()
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                SigningHelper.ApplyOverrides(null, new Options { SignFiles = "bundle" }));
            Assert.Contains("--sign-files", ex.Message);
            ex = Assert.Throws<InvalidOperationException>(() =>
                SigningHelper.ApplyOverrides(null, new Options { SignPackage = "maybe" }));
            Assert.Contains("--sign-package", ex.Message);
        }

        [Fact]
        public void NoSign_TurnsSigningOff()
        {
            Assert.Null(SigningHelper.ApplyOverrides(WithMode("always"), new Options { NoSign = true }));
        }

        [Theory]
        [InlineData("always", null)]
        [InlineData(null, "yes")]
        public void NoSign_WithSigningFlags_Conflicts(string? files, string? package)
        {
            Assert.Throws<InvalidOperationException>(() =>
                SigningHelper.ApplyOverrides(null, new Options { NoSign = true, SignFiles = files, SignPackage = package }));
        }

        [Fact]
        public void SignCert_ReplacesProvider_AndCleansThumbprint()
        {
            var cfg = new SigningConfig { Pfx = @"C:\x.pfx", PfxPassword = "pw", Command = "x", TimestampUrl = "http://ts" };
            var s = SigningHelper.ApplyOverrides(cfg, new Options { SignCert = "\u200e4d e2 eb f5 db ea f2 ad 26 3a 90 b9 0f 17 6f 66 c1 14 c6 f6" });
            var p = s!.Provider;
            Assert.Equal("4DE2EBF5DBEAF2AD263A90B90F176F66C114C6F6", p.Thumbprint);
            Assert.Null(p.Pfx);
            Assert.Null(p.PfxPassword);
            Assert.Null(p.Command);
            Assert.Equal("http://ts", p.TimestampUrl);
            Assert.Equal(FileSignMode.IfUnsigned, s.DefaultFileMode);
        }

        [Fact]
        public void SignCert_ReplacesThePackageKeyToo()
        {
            var cfg = new SigningConfig { Thumbprint = "AA", Package = new PackageSigningConfig { Thumbprint = "BB" } };
            var s = SigningHelper.ApplyOverrides(cfg, new Options { SignCert = CertC });
            Assert.True(s!.PackageInherits);
            Assert.Equal(CertC, s.Package.Thumbprint);
        }

        [Fact]
        public void SignCert_WithoutBlock_IfUnsignedCopiesAndPackage()
        {
            var s = SigningHelper.ApplyOverrides(null, new Options { SignCert = CertC });
            Assert.Equal(FileSignMode.IfUnsigned, s!.DefaultFileMode);
            Assert.True(s.SignPackage);
            Assert.False(s.InPlace);
        }

        [Fact]
        public void SignCert_KeepsTheBlocksModeAndPackageSign()
        {
            var cfg = new SigningConfig
            {
                Files = new FileSigningConfig { Mode = "never" },
                Package = new PackageSigningConfig { Sign = false },
                InPlace = true,
            };
            var s = SigningHelper.ApplyOverrides(cfg, new Options { SignCert = CertC });
            Assert.Equal(FileSignMode.Never, s!.DefaultFileMode);
            Assert.False(s.SignPackage);
            Assert.True(s.InPlace);
        }

        [Theory]
        [InlineData("my cert")]
        [InlineData("ABCDEF12")]
        [InlineData("4DE2EBF5DBEAF2AD263A90B90F176F66C114C6FZ")]
        public void SignCert_NotAThumbprint_Throws(string value)
        {
            // An unusable value must not fall through to the automatic choice.
            var ex = Assert.Throws<InvalidOperationException>(() =>
                SigningHelper.ApplyOverrides(null, new Options { SignCert = value }));
            Assert.Contains("--sign-cert", ex.Message);
        }
        // -----------------------------------------------------------------------
        // SelectCertificate
        // -----------------------------------------------------------------------

        private static X509Certificate2 MakeCert(string cn, bool codeSigning = true, int notBeforeDays = -1, int notAfterDays = 365)
        {
            using var rsa = RSA.Create(2048);
            var req = new CertificateRequest($"CN={cn}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            string eku = codeSigning ? SigningHelper.CodeSigningOid : "1.3.6.1.5.5.7.3.1"; // else server auth
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid(eku) }, false));
            var now = DateTimeOffset.UtcNow;
            return req.CreateSelfSigned(now.AddDays(notBeforeDays), now.AddDays(notAfterDays));
        }

        [Fact]
        public void Select_OneUsable_ReturnsIt()
        {
            using var good = MakeCert("Good");
            using var web  = MakeCert("Web", codeSigning: false);
            var pick = SigningHelper.SelectCertificate(new[] { good, web }, DateTime.Now, out _);
            Assert.Equal(good.Thumbprint, pick!.Thumbprint);
        }

        [Fact]
        public void Select_IgnoresCertWithoutPrivateKey()
        {
            using var good = MakeCert("Good");
            using var keyless = new X509Certificate2(MakeCert("Keyless").RawData);
            Assert.False(keyless.HasPrivateKey);
            var pick = SigningHelper.SelectCertificate(new[] { good, keyless }, DateTime.Now, out _);
            Assert.Equal(good.Thumbprint, pick!.Thumbprint);
        }

        [Fact]
        public void Select_IgnoresExpiredAndNotYetValid()
        {
            using var good    = MakeCert("Good");
            using var expired = MakeCert("Expired", notBeforeDays: -30, notAfterDays: -1);
            using var future  = MakeCert("Future", notBeforeDays: 10, notAfterDays: 400);
            var pick = SigningHelper.SelectCertificate(new[] { expired, good, future }, DateTime.Now, out _);
            Assert.Equal(good.Thumbprint, pick!.Thumbprint);
        }

        [Fact]
        public void Select_None_ExplainsWhatToDo()
        {
            using var web = MakeCert("Web", codeSigning: false);
            Assert.Null(SigningHelper.SelectCertificate(new[] { web }, DateTime.Now, out string error));
            Assert.Contains("--sign-cert", error);
            Assert.Contains("hardware token", error);
        }

        [Fact]
        public void Select_Several_ListsThemAndPicksNone()
        {
            using var a = MakeCert("Alpha");
            using var b = MakeCert("Beta");
            Assert.Null(SigningHelper.SelectCertificate(new[] { a, b }, DateTime.Now, out string error));
            Assert.Contains(a.Thumbprint, error);
            Assert.Contains(b.Thumbprint, error);
            Assert.Contains("2 code-signing certificates", error);
        }

        // -----------------------------------------------------------------------
        // Hardware token detection
        // -----------------------------------------------------------------------

        [Theory]
        [InlineData("eToken Base Cryptographic Provider", true)]
        [InlineData("SafeNet Smart Card Key Storage Provider", true)]
        [InlineData("Microsoft Smart Card Key Storage Provider", true)]
        [InlineData("Microsoft Base Smart Card Crypto Provider", true)]
        [InlineData("Microsoft Enhanced RSA and AES Cryptographic Provider", false)]
        [InlineData("Microsoft Software Key Storage Provider", false)]
        [InlineData(null, false)]
        public void IsHardwareTokenProvider(string? provider, bool expected)
        {
            Assert.Equal(expected, SigningHelper.IsHardwareTokenProvider(provider));
        }

        [Fact]
        public void GetKeyProviderName_SoftwareKey_IsNotAToken()
        {
            using var cert = MakeCert("Soft");
            var pfx = new X509Certificate2(cert.Export(X509ContentType.Pfx, "pw"), "pw");
            Assert.False(SigningHelper.IsHardwareTokenProvider(SigningHelper.GetKeyProviderName(pfx)));
        }

        // -----------------------------------------------------------------------
        // Batching
        // -----------------------------------------------------------------------

        [Fact]
        public void PlanBatches_FirstFileAlone_ThenRestTogether()
        {
            var batches = SigningHelper.PlanBatches(new[] { "a", "b", "c", "d" }, 1000);
            Assert.Equal(2, batches.Count);
            Assert.Equal(new[] { "a" }, batches[0]);
            Assert.Equal(new[] { "b", "c", "d" }, batches[1]);
        }

        [Fact]
        public void PlanBatches_SplitsOnBudget()
        {
            // each path costs 10 + 3 = 13; budget 30 fits two per batch
            var paths = Enumerable.Range(0, 6).Select(i => new string((char)('a' + i), 10)).ToList();
            var batches = SigningHelper.PlanBatches(paths, 30);
            Assert.Equal(new[] { 1, 2, 2, 1 }, batches.Select(b => b.Count).ToArray());
            Assert.Equal(paths, batches.SelectMany(b => b).ToList());
        }

        [Fact]
        public void PlanBatches_Empty()
        {
            Assert.Empty(SigningHelper.PlanBatches(new string[0], 100));
        }

        [Fact]
        public void BuildArgs_SeveralFiles_QuotesEachOnce()
        {
            string args = SigningHelper.BuildArgs(
                new SigningConfig { Thumbprint = "AB" },
                new[] { @"C:\a b\x.dll", @"C:\y.exe" },
                MakeResolver());
            Assert.EndsWith("/fd sha256 \"C:\\a b\\x.dll\" \"C:\\y.exe\"", args);
        }

        // -----------------------------------------------------------------------
        // PlanFiles and StageCopies
        // -----------------------------------------------------------------------

        private static string CopyPe(string dir, string name)
        {
            string target = Path.Combine(dir, name);
            File.Copy(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "clr.dll"), target, overwrite: true);
            return target;
        }

        private static string NewDir(string tag)
        {
            string dir = Path.Combine(Path.GetTempPath(), $"alliepack-{tag}-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void TryDelete(string dir)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        private static EffectiveSigning Eff(string mode, List<string>? include = null, List<string>? exclude = null) =>
            SigningHelper.ApplyOverrides(
                new SigningConfig { Files = new FileSigningConfig { Mode = mode, Include = include, Exclude = exclude ?? new List<string>() } },
                new Options())!;

        [Fact]
        public void Plan_SameSourceTwice_ListedOnce_StrongerModeWins()
        {
            string dir = NewDir("plan");
            try
            {
                string a = CopyPe(dir, "a.dll");
                var plan = SigningHelper.PlanFiles(
                    new (string, FileSignMode?)[] { (a, FileSignMode.IfUnsigned), (a, FileSignMode.Always) },
                    Eff("never"), verbose: false);
                Assert.Single(plan.Files);
                Assert.Equal(FileSignMode.Always, plan.Files[0].Mode);
            }
            finally { TryDelete(dir); }
        }

        [Fact]
        public void Plan_DifferentSignModes_FailUnderStrict()
        {
            string dir = NewDir("plan");
            try
            {
                string a = CopyPe(dir, "a.dll");
                var ex = Assert.Throws<InvalidOperationException>(() => SigningHelper.PlanFiles(
                    new (string, FileSignMode?)[] { (a, FileSignMode.IfUnsigned), (a, FileSignMode.Always) },
                    Eff("never"), verbose: false, strict: true));
                Assert.Contains("--strict", ex.Message);
            }
            finally { TryDelete(dir); }
        }

        [Fact]
        public void Plan_NeverAndScopeDefault()
        {
            string dir = NewDir("plan");
            try
            {
                string a = CopyPe(dir, "a.dll"), b = CopyPe(dir, "b.dll"), c = CopyPe(dir, "c.dll");
                var plan = SigningHelper.PlanFiles(
                    new (string, FileSignMode?)[] { (a, FileSignMode.Never), (b, null), (c, FileSignMode.Always) },
                    Eff("ifUnsigned"), verbose: false);
                Assert.Equal(new[] { "b.dll", "c.dll" }, plan.Files.Select(f => Path.GetFileName(f.Source)).ToArray());
                Assert.Equal(new[] { FileSignMode.IfUnsigned, FileSignMode.Always }, plan.Files.Select(f => f.Mode).ToArray());
            }
            finally { TryDelete(dir); }
        }

        [Fact]
        public void Plan_ExcludeAppliesEvenToSignAlways()
        {
            string dir = NewDir("plan");
            try
            {
                string a = CopyPe(dir, "vendor.dll");
                var plan = SigningHelper.PlanFiles(
                    new (string, FileSignMode?)[] { (a, FileSignMode.Always) },
                    Eff("ifUnsigned", exclude: new List<string> { "vendor*.dll" }), verbose: false);
                Assert.Empty(plan.Files);
                Assert.Equal(1, plan.Filtered);
            }
            finally { TryDelete(dir); }
        }

        [Fact]
        public void Plan_SkipsUnsignableFiles()
        {
            string dir = NewDir("plan");
            try
            {
                string txt = Path.Combine(dir, "readme.txt");
                File.WriteAllText(txt, "hello");
                var plan = SigningHelper.PlanFiles(new (string, FileSignMode?)[] { (txt, null) }, Eff("always"), verbose: false);
                Assert.Empty(plan.Files);
                Assert.Equal(1, plan.Unsignable);
            }
            finally { TryDelete(dir); }
        }

        [Fact]
        public void Stage_CopiesKeepNames_SourcesUntouched_MapPointsAtCopies()
        {
            string dir = NewDir("stage");
            try
            {
                string src = Path.Combine(dir, "src");
                Directory.CreateDirectory(src);
                string a = CopyPe(src, "a.dll");
                File.SetAttributes(a, FileAttributes.ReadOnly);
                string sub = Path.Combine(src, "sub");
                Directory.CreateDirectory(sub);
                string a2 = CopyPe(sub, "a.dll");   // same name, different folder

                var plan = SigningHelper.PlanFiles(new (string, FileSignMode?)[] { (a, null), (a2, null) }, Eff("always"), verbose: false);
                string staging = Path.Combine(dir, "obj", "signed");
                Directory.CreateDirectory(staging);
                File.WriteAllText(Path.Combine(staging, "stale.txt"), "old");

                SigningHelper.StageCopies(plan, staging);

                Assert.False(File.Exists(Path.Combine(staging, "stale.txt")));
                string ca = plan.Map(a), ca2 = plan.Map(a2);
                Assert.NotEqual(ca, ca2);
                Assert.Equal("a.dll", Path.GetFileName(ca));
                Assert.Equal("a.dll", Path.GetFileName(ca2));
                Assert.StartsWith(staging, ca);
                Assert.False(File.GetAttributes(ca).HasFlag(FileAttributes.ReadOnly));
                Assert.Equal(Path.Combine(dir, "other.dll"), plan.Map(Path.Combine(dir, "other.dll")));
                File.SetAttributes(a, FileAttributes.Normal);
            }
            finally { TryDelete(dir); }
        }

        // -----------------------------------------------------------------------
        // SignFiles -- stop at the first failure
        // -----------------------------------------------------------------------

        [Fact]
        public void SignFiles_FailingCommand_StopsAfterFirstFile()
        {
            string dir = NewDir("failfast");
            try
            {
                string log = Path.Combine(dir, "calls.log");
                var files = new[] { "a.dll", "b.dll", "c.dll" }
                    .Select(n => (CopyPe(dir, n), (FileSignMode?)null)).ToList();
                var eff = SigningHelper.ApplyOverrides(
                    new SigningConfig { Files = new FileSigningConfig { Mode = "always" }, Command = $"echo {{file}}>>\"{log}\" & exit /b 1" }, new Options())!;
                var plan = SigningHelper.PlanFiles(files, eff, verbose: false);

                Assert.Throws<InvalidOperationException>(() =>
                    SigningHelper.SignFiles(plan, eff.Provider, MakeResolver(), verbose: false));

                Assert.Single(File.ReadAllLines(log));
            }
            finally { TryDelete(dir); }
        }

        // -----------------------------------------------------------------------
        // SignFiles -- signtool, end to end (skips without signtool)
        // -----------------------------------------------------------------------

        private static bool HaveSignTool()
        {
            try { SigningHelper.FindSignTool(null, MakeResolver()); return true; }
            catch { return false; }
        }

        [Fact]
        public void SignFiles_StagedBatch_SignsCopiesOnly_SkipsAlreadySignedForIfUnsigned()
        {
            if (!HaveSignTool()) return;

            string dir = NewDir("batch");
            try
            {
                using var cert = MakeCert("AlliePack Batch Test");
                string pfx = Path.Combine(dir, "test.pfx");
                File.WriteAllBytes(pfx, cert.Export(X509ContentType.Pfx, "pw"));
                var provider = new SigningConfig { Pfx = pfx, PfxPassword = "pw", Files = new FileSigningConfig { Mode = "always" } };

                // Pre-sign one source so ifUnsigned has something to skip.
                string pre = CopyPe(dir, "pre.dll");
                var preEff  = SigningHelper.ApplyOverrides(provider, new Options { SignInPlace = true })!;
                var prePlan = SigningHelper.PlanFiles(new (string, FileSignMode?)[] { (pre, null) }, preEff, verbose: false);
                SigningHelper.SignFiles(prePlan, preEff.Provider, MakeResolver(), verbose: false);
                Assert.True(SigningHelper.HasAuthenticodeSignature(pre));

                var files = new[] { "a.dll", "b.dll", "c.dll", "d.dll" }
                    .Select(n => (CopyPe(dir, n), (FileSignMode?)null))
                    .Append((pre, (FileSignMode?)FileSignMode.IfUnsigned)).ToList();
                var eff  = SigningHelper.ApplyOverrides(provider, new Options())!;
                var plan = SigningHelper.PlanFiles(files, eff, verbose: false);
                SigningHelper.StageCopies(plan, Path.Combine(dir, "staged"));
                long preStagedLength = new FileInfo(plan.Map(pre)).Length;
                byte[] clrBytes = File.ReadAllBytes(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "clr.dll"));

                SigningHelper.SignFiles(plan, eff.Provider, MakeResolver(), verbose: false);

                foreach (var (src, _) in files.Take(4))
                {
                    Assert.Equal(clrBytes, File.ReadAllBytes(src));   // source untouched
                    using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(plan.Map(src)));
                    Assert.Equal(cert.Thumbprint, signer.Thumbprint);
                }
                Assert.Equal(preStagedLength, new FileInfo(plan.Map(pre)).Length);   // not re-signed
            }
            finally { TryDelete(dir); }
        }

        // -----------------------------------------------------------------------
        // build.ps1 signing pieces
        // -----------------------------------------------------------------------

        [Fact]
        public void Script_ThumbprintProvider_HasParamsFilesAndMsiStep()
        {
            var rows = new List<(string, string, string)> { (@"..\bin\it's.exe", @"signed\1\it's.exe", "ifUnsigned") };
            var files = new SigningConfig { Thumbprint = "ABC", TimestampUrl = "http://ts" };
            var parts = InstallerBuilder.ComposeSigningScript(rows, files, files);

            Assert.Contains("[switch]$SkipSigning", parts.Params);
            Assert.Contains("[string]$Thumbprint = 'ABC'", parts.Params);
            Assert.Contains("[string]$TimestampUrl = 'http://ts'", parts.Params);
            Assert.Contains("[string]$PackageThumbprint = ''", parts.Params);
            Assert.Contains("$signPackagedFiles = $true", parts.PreBuild);
            Assert.Contains(@"Source = '..\bin\it''s.exe'; Path = 'signed\1\it''s.exe'; Mode = 'ifUnsigned'", parts.PreBuild);
            Assert.Contains("Invoke-Sign @($msiPath)", parts.PostBuild);
        }

        [Fact]
        public void Script_PackageOwnKey_IsTheDefaultForPackageThumbprint()
        {
            var rows = new List<(string, string, string)> { ("a.dll", @"signed\1\a.dll", "always") };
            var files = new SigningConfig { Thumbprint = "AAA" };
            var package = new SigningConfig { Thumbprint = "BBB", TimestampUrl = "http://ts2" };
            var parts = InstallerBuilder.ComposeSigningScript(rows, files, package, packageInherits: false,
                                                              packageTimestampUrl: "http://ts2");

            Assert.Contains("[string]$Thumbprint = 'AAA'", parts.Params);
            Assert.Contains("[string]$PackageThumbprint = 'BBB'", parts.Params);
            Assert.Contains("[string]$PackageTimestampUrl = 'http://ts2'", parts.Params);
            Assert.Contains("if ($PackageThumbprint) { $pkgTp = $PackageThumbprint }", parts.PostBuild);
        }

        [Fact]
        public void Script_PackageOnly_SignsNoFiles()
        {
            var parts = InstallerBuilder.ComposeSigningScript(
                new List<(string, string, string)>(), new SigningConfig(), new SigningConfig { Thumbprint = "BBB" },
                packageInherits: false);
            Assert.Contains("$signPackagedFiles = $false", parts.PreBuild);
            Assert.DoesNotContain("$signFiles", parts.PreBuild);
            Assert.Contains("Invoke-Sign @($msiPath)", parts.PostBuild);
        }

        [Fact]
        public void Script_NothingToSign_IsEmpty()
        {
            var parts = InstallerBuilder.ComposeSigningScript(
                new List<(string, string, string)>(), new SigningConfig(), package: null);
            Assert.Equal(string.Empty, parts.Params);
            Assert.Equal(string.Empty, parts.PreBuild);
            Assert.Equal(string.Empty, parts.PostBuild);
        }

        [Fact]
        public void Script_FilesOnly_NotesThatTheMsiIsUnsigned()
        {
            var rows = new List<(string, string, string)> { ("a.dll", @"signed.dll", "always") };
            var parts = InstallerBuilder.ComposeSigningScript(rows, new SigningConfig { Thumbprint = "AAA" }, package: null);
            Assert.Contains("$signPackagedFiles = $true", parts.PreBuild);
            Assert.DoesNotContain("$msiPath", parts.PostBuild);
            Assert.Contains("SkipSigning", parts.PostBuild);
        }

        [Fact]
        public void Script_PfxProvider_CopiesButDoesNotSign()
        {
            var rows = new List<(string, string, string)> { ("a.dll", @"signed\1\a.dll", "always") };
            var pfx = new SigningConfig { Pfx = "x.pfx" };
            var parts = InstallerBuilder.ComposeSigningScript(rows, pfx, pfx);
            Assert.Equal(string.Empty, parts.Params);
            Assert.Contains("$signFiles", parts.PreBuild);
            Assert.DoesNotContain("Invoke-Sign", parts.PreBuild);
            Assert.Equal(string.Empty, parts.PostBuild);
        }

        [Fact]
        public void Script_PfxFiles_ThumbprintPackage_SignsOnlyTheMsi()
        {
            var rows = new List<(string, string, string)> { ("a.dll", @"signed\1\a.dll", "always") };
            var parts = InstallerBuilder.ComposeSigningScript(
                rows, new SigningConfig { Pfx = "x.pfx" }, new SigningConfig { Thumbprint = "BBB" }, packageInherits: false);
            Assert.Contains("$signPackagedFiles = $false", parts.PreBuild);
            Assert.Contains("Packaged files are not signed here", parts.PreBuild);
            Assert.Contains("Invoke-Sign @($msiPath)", parts.PostBuild);
        }

        [Fact]
        public void Script_WholeBuildPs1_ParsesInPowerShell()
        {
            string dir = NewDir("ps1");
            try
            {
                var rows = new List<(string, string, string)> { ("a.dll", @"signed\1\a.dll", "always") };
                var parts = InstallerBuilder.ComposeSigningScript(
                    rows, new SigningConfig(), new SigningConfig { Thumbprint = "BBB" }, packageInherits: false);
                InstallerBuilder.WriteBuildScript(dir, "Test", new List<string>(), "x64", null, parts);

                string script = Path.Combine(dir, "build.ps1");
                string check = "$e = $null; [void][System.Management.Automation.Language.Parser]::ParseFile('" +
                               script.Replace("'", "''") + "', [ref]$null, [ref]$e); if ($e.Count) { $e | % { $_.Message }; exit 1 }";
                var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe", "-NoProfile -Command \"" + check + "\"")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
                };
                using var p = System.Diagnostics.Process.Start(psi)!;
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                Assert.True(p.ExitCode == 0, "build.ps1 does not parse: " + output);
            }
            finally { TryDelete(dir); }
        }
    }
}
