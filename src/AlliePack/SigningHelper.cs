using Microsoft.Extensions.FileSystemGlobbing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AlliePack
{
    // How one packaged file is signed.  Ordered by strength: when one source file is
    // reached from entries with different settings, the stronger setting wins.
    internal enum FileSignMode { Never = 0, IfUnsigned = 1, Always = 2 }

    /// <summary>The signing: block combined with the command line.</summary>
    internal sealed class EffectiveSigning
    {
        // The key for packaged files (thumbprint, pfx, azure, command, timestamp, signtool path).
        internal SigningConfig Provider { get; set; } = new SigningConfig();
        // The key for the MSI: signing.package's own provider, or a copy of Provider.
        internal SigningConfig Package { get; set; } = new SigningConfig();
        // True when signing.package names no provider of its own, so the MSI uses Provider's key.
        internal bool PackageInherits { get; set; } = true;
        // signing.package.timestampUrl as written, or null; build.ps1 falls back to -TimestampUrl.
        internal string? PackageTimestampUrl { get; set; }
        internal bool SignPackage { get; set; }
        // The mode for files whose entries say no sign: (signing.files.mode or --sign-files).
        internal FileSignMode DefaultFileMode { get; set; }
        // True when --sign-files set the mode; it then overrides every entry's sign:.
        internal bool FileModeFromCommandLine { get; set; }
        internal bool InPlace { get; set; }
        internal List<string>? Include { get; set; }
        internal List<string> Exclude { get; set; } = new List<string>();

        internal FileSignMode ModeFor(FileSignMode? entryMode) =>
            FileModeFromCommandLine ? DefaultFileMode : entryMode ?? DefaultFileMode;
    }

    /// <summary>One packaged file to sign.</summary>
    internal sealed class SignCandidate
    {
        internal string Source { get; set; } = string.Empty;   // the file as found
        internal string Path   { get; set; } = string.Empty;   // the file signed and packaged
        internal FileSignMode Mode { get; set; }
    }

    internal sealed class FileSigningPlan
    {
        private readonly List<SignCandidate> _files = new List<SignCandidate>();
        private readonly Dictionary<string, SignCandidate> _bySource =
            new Dictionary<string, SignCandidate>(StringComparer.OrdinalIgnoreCase);

        internal IReadOnlyList<SignCandidate> Files => _files;
        internal int Unsignable { get; set; }
        internal int Filtered   { get; set; }

        internal void Add(SignCandidate c)
        {
            _files.Add(c);
            _bySource[c.Source] = c;
        }

        // Source path -> packaged path.  Sources not in the plan map to themselves.
        internal string Map(string source) =>
            _bySource.TryGetValue(System.IO.Path.GetFullPath(source), out var c) ? c.Path : source;
    }

    internal static class SigningHelper
    {
        internal const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";

        // CreateProcess caps the command line at 32767 chars; keep each signtool
        // batch well under it.
        internal const int MaxSignToolArgsLength = 30000;

        // -----------------------------------------------------------------------
        // Scope and command-line overrides
        // -----------------------------------------------------------------------

        // "unsigned" and "all" are the older files.mode words; still accepted.
        internal static FileSignMode ParseFileMode(string value, string source)
        {
            switch (value.Trim().ToLowerInvariant())
            {
                case "never":
                case "none":       return FileSignMode.Never;
                case "ifunsigned":
                case "unsigned":   return FileSignMode.IfUnsigned;
                case "always":
                case "all":        return FileSignMode.Always;
                default:
                    throw new InvalidOperationException(
                        $"{source}: '{value}' is not a sign: value. Use always, ifUnsigned or never.");
            }
        }

        internal static bool ParseYesNo(string value, string source)
        {
            switch (value.Trim().ToLowerInvariant())
            {
                case "yes": case "true":  return true;
                case "no":  case "false": return false;
                default:
                    throw new InvalidOperationException($"{source}: '{value}' is not yes or no.");
            }
        }

        internal static string Name(FileSignMode m) =>
            m == FileSignMode.IfUnsigned ? "ifUnsigned" : m.ToString().ToLowerInvariant();

        /// <summary>
        /// Combines the config's <c>signing:</c> block with --sign-files, --sign-package,
        /// --sign-cert, --sign-in-place and --no-sign.  Returns null when nothing can be
        /// signed (--no-sign, or --sign-files never with the MSI unsigned).
        ///
        /// Files: --sign-files, else signing.files.mode, else ifUnsigned.  With no
        /// signing: block the default is never (only entries marked sign: are signed),
        /// or ifUnsigned when --sign-cert is given.
        /// MSI: --sign-package, else signing.package.sign, else true.  With no block it
        /// is signed only when --sign-cert is given or --sign-files signs files.
        ///
        /// The providers are copies; the config is not changed.  Does not look in the
        /// certificate store (see <see cref="Prepare(EffectiveSigning, bool, bool)"/>).
        /// </summary>
        internal static EffectiveSigning? ApplyOverrides(SigningConfig? config, Options options)
        {
            FileSignMode? cliMode = string.IsNullOrWhiteSpace(options.SignFiles)
                ? (FileSignMode?)null
                : ParseFileMode(options.SignFiles!, "--sign-files");
            bool? cliPackage = string.IsNullOrWhiteSpace(options.SignPackage)
                ? (bool?)null
                : ParseYesNo(options.SignPackage!, "--sign-package");
            bool signCert = !string.IsNullOrWhiteSpace(options.SignCert);

            if (options.NoSign)
            {
                if ((cliMode != null && cliMode != FileSignMode.Never) || cliPackage == true)
                    throw new InvalidOperationException(
                        "--no-sign conflicts with --sign-files or --sign-package. Pass one or the other.");
                return null;
            }

            var files = new SigningConfig();
            if (config != null) CopyProvider(config, files);
            var pkgConfig = config?.Package;
            bool pkgOwn = pkgConfig != null && HasProvider(pkgConfig);

            if (signCert)
            {
                // Thumbprints copied from certmgr carry spaces and an invisible U+200E mark.
                string cleaned = new string(options.SignCert!.Where(c => !char.IsWhiteSpace(c) && c != '\u200E')
                                                       .ToArray()).ToUpperInvariant();
                if (cleaned.Length != 40 || !cleaned.All(Uri.IsHexDigit))
                    throw new InvalidOperationException(
                        $"--sign-cert: '{options.SignCert}' is not a SHA1 thumbprint (40 hex digits).");
                files.Thumbprint  = cleaned;
                files.Pfx         = null;
                files.PfxPassword = null;
                files.Azure       = null;
                files.Command     = null;
                pkgOwn = false;   // --sign-cert replaces both keys
            }

            // The MSI's key is the package's own, or the files' key.  Its timestampUrl and
            // signToolPath are the package's when set, whichever key it uses, else the block's.
            var package = new SigningConfig();
            CopyProvider(pkgOwn ? pkgConfig! : files, package);
            package.TimestampUrl = pkgConfig?.TimestampUrl ?? files.TimestampUrl;
            package.SignToolPath = pkgConfig?.SignToolPath ?? files.SignToolPath;

            FileSignMode mode =
                cliMode
                ?? (config == null ? (signCert ? FileSignMode.IfUnsigned : FileSignMode.Never)
                    : config.Files?.Mode != null ? ParseFileMode(config.Files.Mode, "signing.files.mode")
                    : FileSignMode.IfUnsigned);

            bool signPackage =
                cliPackage
                ?? (config == null ? signCert || (cliMode != null && cliMode != FileSignMode.Never)
                    : pkgConfig?.Sign ?? true);

            if (cliMode == FileSignMode.Never && !signPackage)
                return null;

            return new EffectiveSigning
            {
                Provider                = files,
                Package                 = package,
                PackageInherits         = !pkgOwn,
                PackageTimestampUrl     = pkgConfig?.TimestampUrl,
                SignPackage             = signPackage,
                DefaultFileMode         = mode,
                FileModeFromCommandLine = cliMode != null,
                InPlace                 = (config?.InPlace ?? false) || options.SignInPlace,
                Include                 = config?.Files?.Include,
                Exclude                 = config?.Files?.Exclude ?? new List<string>(),
            };
        }

        private static void CopyProvider(SigningProviderConfig from, SigningProviderConfig to)
        {
            to.Thumbprint   = from.Thumbprint;
            to.Pfx          = from.Pfx;
            to.PfxPassword  = from.PfxPassword;
            to.Azure        = from.Azure;
            to.Command      = from.Command;
            to.TimestampUrl = from.TimestampUrl;
            to.SignToolPath = from.SignToolPath;
        }

        internal static bool HasProvider(SigningProviderConfig s) =>
            !string.IsNullOrEmpty(s.Thumbprint) || !string.IsNullOrEmpty(s.Pfx) ||
            s.Azure != null || !string.IsNullOrEmpty(s.Command);

        // -----------------------------------------------------------------------
        // Certificate selection
        // -----------------------------------------------------------------------

        /// <summary>
        /// Run once before signing: prepares the files' key when files will be signed or the
        /// MSI uses the same key, and the package's own key when it has one.  When the MSI
        /// uses the files' key, the automatic choice is made once and copied to it.
        /// </summary>
        internal static void Prepare(EffectiveSigning s, bool signFiles, bool verbose)
        {
            if (signFiles || (s.SignPackage && s.PackageInherits))
                Prepare(s.Provider, verbose);
            if (!s.SignPackage) return;
            if (s.PackageInherits)
            {
                if (!HasProvider(s.Package)) s.Package.Thumbprint = s.Provider.Thumbprint;
            }
            else
            {
                Prepare(s.Package, verbose);
            }
        }

        /// <summary>
        /// With no provider configured, picks the only usable
        /// code-signing certificate in CurrentUser\My and fails, listing them, when there
        /// is none or more than one.  Warns when the certificate's key is on a hardware token.
        /// </summary>
        internal static void Prepare(SigningConfig s, bool verbose)
        {
            if (!HasProvider(s))
            {
                using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadOnly);
                var certs = store.Certificates.Cast<X509Certificate2>().ToList();
                var pick  = SelectCertificate(certs, DateTime.Now, out string error);
                if (pick == null)
                    throw new InvalidOperationException(error);

                s.Thumbprint = pick.Thumbprint;
                Console.WriteLine($"  certificate: {pick.Subject} ({pick.Thumbprint}), " +
                                  $"the only code-signing certificate in CurrentUser\\My");
            }

            if (!string.IsNullOrEmpty(s.Thumbprint))
                WarnIfHardwareToken(s.Thumbprint!);
        }

        // For --report: which certificate Prepare would pick, without failing.
        internal static string DescribeAutoCertificate()
        {
            try
            {
                using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadOnly);
                var pick = SelectCertificate(store.Certificates.Cast<X509Certificate2>(), DateTime.Now, out string error);
                return pick != null
                    ? $"auto ({pick.Subject}, {pick.Thumbprint})"
                    : "auto -- WILL FAIL: " + error.Replace("\r", "").Replace("\n", "\n              ");
            }
            catch (Exception ex)
            {
                return $"auto -- could not read CurrentUser\\My: {ex.Message}";
            }
        }

        internal static bool IsUsableCodeSigningCert(X509Certificate2 cert, DateTime now)
        {
            if (!cert.HasPrivateKey) return false;
            if (now < cert.NotBefore || now > cert.NotAfter) return false;
            return cert.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                       .Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == CodeSigningOid));
        }

        /// <summary>
        /// Returns the one usable code-signing certificate, or null with a message that
        /// says why (none, or several, listed).
        /// </summary>
        internal static X509Certificate2? SelectCertificate(
            IEnumerable<X509Certificate2> certs, DateTime now, out string error)
        {
            var usable = certs.Where(c => IsUsableCodeSigningCert(c, now)).ToList();
            if (usable.Count == 1)
            {
                error = string.Empty;
                return usable[0];
            }

            if (usable.Count == 0)
            {
                error = "signing: no certificate was named, and CurrentUser\\My has no code-signing " +
                        "certificate with a private key that is currently valid. Set signing.thumbprint, " +
                        "pfx, azure or command, or pass --sign-cert. For a hardware token, plug it in " +
                        "first so its driver publishes the certificate to the store.";
                return null;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"signing: no certificate was named, and CurrentUser\\My has {usable.Count} " +
                          "code-signing certificates. Pick one with signing.thumbprint or --sign-cert:");
            foreach (var c in usable.OrderBy(c => c.Subject, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"  {c.Thumbprint}  {c.Subject}  (expires {c.NotAfter:yyyy-MM-dd})");
            error = sb.ToString().TrimEnd();
            return null;
        }

        // -----------------------------------------------------------------------
        // Hardware token detection.  Reads the key provider name from the
        // certificate's CERT_KEY_PROV_INFO property, which does not open the key,
        // so it cannot trigger a token password prompt.
        // -----------------------------------------------------------------------

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern bool CertGetCertificateContextProperty(
            IntPtr pCertContext, uint dwPropId, IntPtr pvData, ref uint pcbData);

        private const uint CERT_KEY_PROV_INFO_PROP_ID = 2;

        internal static string? GetKeyProviderName(X509Certificate2 cert)
        {
            uint size = 0;
            if (!CertGetCertificateContextProperty(cert.Handle, CERT_KEY_PROV_INFO_PROP_ID, IntPtr.Zero, ref size) || size == 0)
                return null;

            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (!CertGetCertificateContextProperty(cert.Handle, CERT_KEY_PROV_INFO_PROP_ID, buf, ref size))
                    return null;
                // CRYPT_KEY_PROV_INFO: pwszContainerName, then pwszProvName.
                IntPtr provName = Marshal.ReadIntPtr(buf, IntPtr.Size);
                return provName == IntPtr.Zero ? null : Marshal.PtrToStringUni(provName);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        internal static bool IsHardwareTokenProvider(string? provider)
        {
            if (string.IsNullOrEmpty(provider)) return false;
            string[] markers = { "eToken", "SafeNet", "Smart Card", "SmartCard" };
            return markers.Any(m => provider!.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static void WarnIfHardwareToken(string thumbprint)
        {
            try
            {
                using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadOnly);
                var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
                if (found.Count == 0) return;

                string? provider = GetKeyProviderName(found[0]);
                if (!IsHardwareTokenProvider(provider)) return;

                Console.WriteLine($"  note: this certificate's key is on a hardware token ({provider}).");
                Console.WriteLine("        The token driver will ask for the token password, possibly more than once.");
                Console.WriteLine("        AlliePack signs one file first and stops after the first failed signtool run.");
                Console.WriteLine("        With SafeNet Authentication Client, single logon (Client Settings > Advanced,");
                Console.WriteLine("        then log off and on) avoids repeated prompts.");
            }
            catch
            {
                // Advisory only; signing proceeds either way.
            }
        }

        // -----------------------------------------------------------------------
        // Windows SIP (Subject Interface Package) query.
        // This is the same lookup signtool performs internally to determine
        // whether it can sign a given file.  Returns false for text files,
        // images, and any format with no registered handler.
        // -----------------------------------------------------------------------

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptSIPRetrieveSubjectGuid(
            string fileName,
            IntPtr hFileIn,
            out Guid pgSubject);

        internal static bool IsSipSignable(string path)
        {
            try { return CryptSIPRetrieveSubjectGuid(path, IntPtr.Zero, out _); }
            catch { return false; }
        }

        // -----------------------------------------------------------------------
        // Check whether a file already carries an Authenticode signature.
        // Used by mode: unsigned to skip already-signed files.
        // Only detects embedded signatures; catalog-signed files return false,
        // which is intentional -- they will pass through to be signed.
        // -----------------------------------------------------------------------

        internal static bool HasAuthenticodeSignature(string path)
        {
            try { X509Certificate.CreateFromSignedFile(path); return true; }
            catch { return false; }
        }

        // -----------------------------------------------------------------------
        // Filename glob matching -- patterns match against the filename only,
        // not the full path.  Uses the same Matcher as the rest of AlliePack.
        // -----------------------------------------------------------------------

        internal static bool MatchesFilename(IEnumerable<string> patterns, string fileName)
        {
            var matcher = new Matcher();
            foreach (var p in patterns)
                matcher.AddInclude(p);
            return matcher.Match(fileName).HasMatches;
        }

        // -----------------------------------------------------------------------
        // Sign individual files before packaging
        // -----------------------------------------------------------------------

        /// <summary>
        /// Decides which packaged files are signed and how.  Each source is considered
        /// once; when entries disagree the stronger mode wins, with a warning (an error
        /// under --strict).  Then the
        /// signing.files include/exclude filters and the SIP check apply.  Whether a file
        /// already has a signature is checked later, when it is signed.
        /// </summary>
        internal static FileSigningPlan PlanFiles(
            IEnumerable<(string Path, FileSignMode? Entry)> files,
            EffectiveSigning s,
            bool verbose,
            bool strict = false)
        {
            var order = new List<string>();
            var modes = new Dictionary<string, List<FileSignMode>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, entry) in files)
            {
                string full = Path.GetFullPath(path);
                if (!modes.TryGetValue(full, out var list))
                {
                    modes[full] = list = new List<FileSignMode>();
                    order.Add(full);
                }
                list.Add(s.ModeFor(entry));
            }

            var plan = new FileSigningPlan();
            foreach (string full in order)
            {
                var seen = modes[full].Distinct().OrderBy(m => m).ToList();
                var mode = seen.Last();
                string name = Path.GetFileName(full);

                if (seen.Count > 1)
                {
                    string msg = $"{full} is reached from entries with different sign: values " +
                                 $"({string.Join(", ", seen.Select(Name))})";
                    if (strict)
                        throw new InvalidOperationException(
                            msg + ", and --strict makes that an error. Give the entries the same sign:, " +
                            "or set it on a common parent.");
                    Console.WriteLine($"Warning: {msg}; using {Name(mode)}.");
                }

                if (mode == FileSignMode.Never)
                    continue;

                if (s.Exclude.Any() && MatchesFilename(s.Exclude, name))
                {
                    if (verbose) Console.WriteLine($"  [excluded] {name}");
                    plan.Filtered++;
                    continue;
                }

                if (s.Include != null && !MatchesFilename(s.Include, name))
                {
                    if (verbose) Console.WriteLine($"  [skip    ] {name} -- not in include list");
                    plan.Filtered++;
                    continue;
                }

                // Ask Windows whether this file type is signable.
                if (!IsSipSignable(full))
                {
                    if (verbose) Console.WriteLine($"  [skip    ] {name} -- not signable (no SIP handler)");
                    plan.Unsignable++;
                    continue;
                }

                plan.Add(new SignCandidate { Source = full, Path = full, Mode = mode });
            }
            return plan;
        }

        /// <summary>
        /// Copies each planned file to <paramref name="stagingDir"/>\&lt;n&gt;\&lt;name&gt;
        /// and points the plan at the copy.  The folder is emptied first.  The file name
        /// is kept because WiX installs a file under its source name.
        /// </summary>
        internal static void StageCopies(FileSigningPlan plan, string stagingDir)
        {
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);

            int n = 0;
            foreach (var c in plan.Files)
            {
                string dir = Path.Combine(stagingDir, (++n).ToString());
                Directory.CreateDirectory(dir);
                string target = Path.Combine(dir, Path.GetFileName(c.Source));
                File.Copy(c.Source, target, overwrite: true);
                File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
                c.Path = target;
            }
        }

        /// <summary>
        /// Signs the planned files (each at its <c>Path</c>).  Files whose mode is
        /// ifUnsigned and that already carry a signature are skipped.
        /// </summary>
        internal static void SignFiles(
            FileSigningPlan plan,
            SigningConfig signing,
            PathResolver resolver,
            bool verbose)
        {
            bool isCommand = !string.IsNullOrEmpty(signing.Command);
            int countAlreadySigned = 0, countUnsignable = plan.Unsignable;

            // --- pass 1: skip what is already signed ---
            var toSign = new List<string>();
            foreach (var c in plan.Files)
            {
                if (c.Mode == FileSignMode.IfUnsigned && HasAuthenticodeSignature(c.Path))
                {
                    if (verbose) Console.WriteLine($"  [skip    ] {Path.GetFileName(c.Path)} -- already signed");
                    countAlreadySigned++;
                    continue;
                }
                toSign.Add(c.Path);
            }

            // --- pass 2: sign.  Stops at the first failure: on a hardware token every
            // failed attempt can use up one of the token's password retries. ---
            int countSigned = 0;
            string? failure = null;

            if (toSign.Count > 0)
            {
                if (isCommand)
                {
                    // {file} names one file, so the command runs once per file.
                    string cmdTemplate = resolver.Tokens.Substitute(signing.Command!);
                    foreach (string path in toSign)
                    {
                        if (!RunCustomCommand(cmdTemplate.Replace("{file}", path), out string errorText))
                        {
                            Console.WriteLine($"  [failed  ] {Path.GetFileName(path)} -- {errorText.Trim()}");
                            failure = path;
                            break;
                        }
                        Console.WriteLine($"  [signed  ] {Path.GetFileName(path)}");
                        countSigned++;
                    }
                }
                else
                {
                    TempFile? azureTemp = signing.Azure != null ? WriteAzureMetadata(signing.Azure, resolver) : null;
                    try
                    {
                        string tool = FindSignTool(signing.SignToolPath, resolver);
                        int baseLength = BuildArgs(signing, new string[0], resolver, azureTemp?.Path).Length;

                        foreach (var batch in PlanBatches(toSign, MaxSignToolArgsLength - baseLength))
                        {
                            string args = BuildArgs(signing, batch, resolver, azureTemp?.Path);
                            if (!RunSignTool(tool, args, out string errorText))
                            {
                                string label = batch.Count == 1
                                    ? Path.GetFileName(batch[0])
                                    : $"{batch.Count} files from {Path.GetFileName(batch[0])}";
                                Console.WriteLine($"  [failed  ] {label} -- {errorText.Trim()}");
                                failure = batch[0];
                                break;
                            }
                            foreach (string path in batch)
                                Console.WriteLine($"  [signed  ] {Path.GetFileName(path)}");
                            countSigned += batch.Count;
                        }
                    }
                    finally
                    {
                        azureTemp?.Dispose();
                    }
                }
            }

            // Summary line always shown
            var parts = new List<string>();
            parts.Add($"{countSigned} signed");
            if (countAlreadySigned > 0) parts.Add($"{countAlreadySigned} already signed");
            if (countUnsignable    > 0) parts.Add($"{countUnsignable} not signable");
            if (failure != null)        parts.Add($"FAILED, up to {toSign.Count - countSigned} not signed");
            Console.WriteLine($"  files: {string.Join(", ", parts)}");

            if (failure != null)
                throw new InvalidOperationException(
                    "Code signing failed. AlliePack stopped at the first failure, so a wrong " +
                    "token password is not retried. See output above.");
        }

        /// <summary>
        /// Splits the files into signtool runs: the first file on its own, so a bad
        /// password or certificate fails on one file, then the rest in batches whose
        /// quoted paths fit in <paramref name="budget"/> characters.
        /// </summary>
        internal static List<List<string>> PlanBatches(IReadOnlyList<string> paths, int budget)
        {
            var batches = new List<List<string>>();
            if (paths.Count == 0) return batches;

            batches.Add(new List<string> { paths[0] });

            var current = new List<string>();
            int length = 0;
            for (int i = 1; i < paths.Count; i++)
            {
                int cost = paths[i].Length + 3; // space and two quotes
                if (current.Count > 0 && length + cost > budget)
                {
                    batches.Add(current);
                    current = new List<string>();
                    length = 0;
                }
                current.Add(paths[i]);
                length += cost;
            }
            if (current.Count > 0) batches.Add(current);
            return batches;
        }

        // -----------------------------------------------------------------------
        // Sign the final MSI output
        // -----------------------------------------------------------------------

        internal static void Sign(string msiPath, SigningConfig signing, PathResolver resolver, bool verbose)
        {
            if (!string.IsNullOrEmpty(signing.Command))
            {
                string cmd = resolver.Tokens.Substitute(signing.Command!).Replace("{file}", msiPath);
                if (verbose) Console.WriteLine($"  signing: {cmd}");
                if (!RunCustomCommand(cmd, out string errorText))
                {
                    Console.WriteLine(errorText.TrimEnd());
                    throw new InvalidOperationException(
                        "Code signing failed. See output above.");
                }
                Console.WriteLine($"  signed : {msiPath}");
                return;
            }

            TempFile? azureTemp = signing.Azure != null
                ? WriteAzureMetadata(signing.Azure, resolver)
                : null;
            try
            {
                string tool = FindSignTool(signing.SignToolPath, resolver);
                string args = BuildArgs(signing, msiPath, resolver, azureTemp?.Path);

                if (verbose) Console.WriteLine($"  signing: {tool} sign ...");

                if (!RunSignTool(tool, args, out string errorText))
                {
                    Console.WriteLine(errorText.TrimEnd());
                    throw new InvalidOperationException(
                        "Code signing failed (signtool returned non-zero). See output above.");
                }

                Console.WriteLine($"  signed : {msiPath}");
            }
            finally
            {
                azureTemp?.Dispose();
            }
        }

        // -----------------------------------------------------------------------
        // Shared helpers
        // -----------------------------------------------------------------------

        internal static string FindSignTool(string? configPath, PathResolver resolver)
        {
            if (!string.IsNullOrEmpty(configPath))
            {
                string resolved = resolver.Resolve(configPath!);
                if (File.Exists(resolved)) return resolved;
                throw new InvalidOperationException(
                    $"signtool not found at configured path: {resolved}");
            }

            // PATH search
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                                   .Split(new char[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(dir.Trim(), "signtool.exe");
                if (File.Exists(candidate)) return candidate;
            }

            // Windows SDK fallback -- newest version first
            string[] sdkRoots = {
                @"C:\Program Files (x86)\Windows Kits\10\bin",
                @"C:\Program Files\Windows Kits\10\bin",
            };
            string[] archs = { "x64", "x86" };

            foreach (string root in sdkRoots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (string ver in Directory.GetDirectories(root)
                                                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    foreach (string arch in archs)
                    {
                        string candidate = Path.Combine(ver, arch, "signtool.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }

            throw new InvalidOperationException(
                "signtool.exe not found. Add it to PATH, install the Windows SDK, " +
                "or set signing.signToolPath in the config.");
        }

        internal static string FindDlib(string? configPath, PathResolver resolver)
        {
            if (!string.IsNullOrEmpty(configPath))
            {
                string resolved = resolver.Resolve(configPath!);
                if (File.Exists(resolved)) return resolved;
                throw new InvalidOperationException(
                    $"Azure.CodeSigning.Dlib.dll not found at configured path: {resolved}");
            }

            throw new InvalidOperationException(
                "signing.azure.dlibPath is required. " +
                "Install the Azure Artifact Signing Client Tools " +
                "(winget install -e --id Microsoft.Azure.ArtifactSigningClientTools) " +
                "and set signing.azure.dlibPath to the full path of Azure.CodeSigning.Dlib.dll.");
        }

        internal static string BuildArgs(
            SigningConfig signing,
            string filePath,
            PathResolver resolver,
            string? azureMetadataPath = null)
            => BuildArgs(signing, new[] { filePath }, resolver, azureMetadataPath);

        // signtool signs every file named on one command line with the same options.
        internal static string BuildArgs(
            SigningConfig signing,
            IReadOnlyList<string> filePaths,
            PathResolver resolver,
            string? azureMetadataPath = null)
        {
            bool hasThumbprint = !string.IsNullOrEmpty(signing.Thumbprint);
            bool hasPfx        = !string.IsNullOrEmpty(signing.Pfx);
            bool hasAzure      = signing.Azure != null;

            int count = (hasThumbprint ? 1 : 0) + (hasPfx ? 1 : 0) + (hasAzure ? 1 : 0);
            if (count == 0)
                throw new InvalidOperationException(
                    "signing: requires 'thumbprint', 'pfx', 'azure', or 'command' to be specified.");
            if (count > 1)
                throw new InvalidOperationException(
                    "signing: specify exactly one of 'thumbprint', 'pfx', 'azure', or 'command'.");

            var sb = new StringBuilder("sign");

            if (hasThumbprint)
            {
                sb.Append($" /sha1 {signing.Thumbprint}");
            }
            else if (hasPfx)
            {
                string pfxPath = resolver.Resolve(signing.Pfx!);
                sb.Append($" /f \"{pfxPath}\"");
                if (!string.IsNullOrEmpty(signing.PfxPassword))
                {
                    string password = resolver.Tokens.Substitute(signing.PfxPassword!);
                    sb.Append($" /p \"{password}\"");
                }
            }
            else // azure
            {
                if (azureMetadataPath == null)
                    throw new InvalidOperationException(
                        "Internal: azureMetadataPath must be provided for azure signing.");
                string dlibPath = FindDlib(signing.Azure!.DlibPath, resolver);
                sb.Append($" /dlib \"{dlibPath}\"");
                sb.Append($" /dmdf \"{azureMetadataPath}\"");
            }

            sb.Append(" /fd sha256");

            if (!string.IsNullOrEmpty(signing.TimestampUrl))
            {
                sb.Append($" /tr {signing.TimestampUrl}");
                sb.Append(" /td sha256");
            }

            foreach (string filePath in filePaths)
                sb.Append($" \"{filePath}\"");
            return sb.ToString();
        }

        // -----------------------------------------------------------------------
        // Azure Trusted Signing -- metadata.json generation
        // -----------------------------------------------------------------------

        private sealed class TempFile : IDisposable
        {
            internal string Path { get; }
            internal TempFile(string path) { Path = path; }
            public void Dispose() { try { File.Delete(Path); } catch { } }
        }

        private static TempFile WriteAzureMetadata(AzureSigningConfig azure, PathResolver resolver)
        {
            string json = BuildAzureMetadataJson(azure, resolver);
            string path = System.IO.Path.GetTempFileName();
            File.WriteAllText(path, json);
            return new TempFile(path);
        }

        private static string BuildAzureMetadataJson(AzureSigningConfig azure, PathResolver resolver)
        {
            var props = new List<string>
            {
                $"  \"Endpoint\": \"{JsonEscape(resolver.Tokens.Substitute(azure.Endpoint))}\"",
                $"  \"CodeSigningAccountName\": \"{JsonEscape(resolver.Tokens.Substitute(azure.Account))}\"",
                $"  \"CertificateProfileName\": \"{JsonEscape(resolver.Tokens.Substitute(azure.CertificateProfile))}\"",
            };
            if (!string.IsNullOrEmpty(azure.CorrelationId))
                props.Add($"  \"CorrelationId\": \"{JsonEscape(resolver.Tokens.Substitute(azure.CorrelationId!))}\"");

            return "{\n" + string.Join(",\n", props) + "\n}";
        }

        private static string JsonEscape(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

        // -----------------------------------------------------------------------
        // Process runners
        // -----------------------------------------------------------------------

        private static bool RunSignTool(string tool, string args, out string errorText)
        {
            var psi = new ProcessStartInfo(tool, args)
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start signtool.exe");

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            errorText = proc.ExitCode != 0
                ? (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)
                : string.Empty;

            return proc.ExitCode == 0;
        }

        // Runs an arbitrary shell command via cmd.exe.
        // Used by signing.command: to support external signing tools.
        private static bool RunCustomCommand(string command, out string errorText)
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c {command}")
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start signing command.");

            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            errorText = proc.ExitCode != 0
                ? (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)
                : string.Empty;

            return proc.ExitCode == 0;
        }
    }
}
