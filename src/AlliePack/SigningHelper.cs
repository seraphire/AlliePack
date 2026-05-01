using Microsoft.Extensions.FileSystemGlobbing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace AlliePack
{
    internal static class SigningHelper
    {
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

        internal static void SignFiles(
            IReadOnlyList<ResolvedFile> files,
            SigningConfig signing,
            PathResolver resolver,
            bool verbose)
        {
            var cfg      = signing.Files!;
            bool modeAll = cfg.Mode.Equals("all", StringComparison.OrdinalIgnoreCase);
            bool isCommand = !string.IsNullOrEmpty(signing.Command);

            string? cmdTemplate = isCommand
                ? resolver.Tokens.Substitute(signing.Command!)
                : null;

            TempFile? azureTemp = (!isCommand && signing.Azure != null)
                ? WriteAzureMetadata(signing.Azure, resolver)
                : null;
            string? tool = isCommand ? null : FindSignTool(signing.SignToolPath, resolver);

            int countSigned = 0, countAlreadySigned = 0, countUnsignable = 0, countFailed = 0;

            try
            {
                foreach (var file in files)
                {
                    string path = file.SourcePath;
                    string name = Path.GetFileName(path);

                    // --- exclude filter ---
                    if (cfg.Exclude.Any() && MatchesFilename(cfg.Exclude, name))
                    {
                        if (verbose) Console.WriteLine($"  [excluded] {name}");
                        continue;
                    }

                    // --- include filter (if specified) ---
                    if (cfg.Include != null && !MatchesFilename(cfg.Include, name))
                    {
                        if (verbose) Console.WriteLine($"  [skip    ] {name} -- not in include list");
                        continue;
                    }

                    // --- SIP check: ask Windows whether this file type is signable ---
                    if (!IsSipSignable(path))
                    {
                        if (verbose) Console.WriteLine($"  [skip    ] {name} -- not signable (no SIP handler)");
                        countUnsignable++;
                        continue;
                    }

                    // --- already-signed check (mode: unsigned) ---
                    if (!modeAll && HasAuthenticodeSignature(path))
                    {
                        if (verbose) Console.WriteLine($"  [skip    ] {name} -- already signed");
                        countAlreadySigned++;
                        continue;
                    }

                    // --- sign ---
                    bool ok;
                    string errorText;
                    if (isCommand)
                    {
                        string cmd = cmdTemplate!.Replace("{file}", path);
                        ok = RunCustomCommand(cmd, out errorText);
                    }
                    else
                    {
                        string args = BuildArgs(signing, path, resolver, azureTemp?.Path);
                        ok = RunSignTool(tool!, args, out errorText);
                    }

                    if (ok)
                    {
                        Console.WriteLine($"  [signed  ] {name}");
                        countSigned++;
                    }
                    else
                    {
                        Console.WriteLine($"  [failed  ] {name} -- {errorText.Trim()}");
                        countFailed++;
                    }
                }
            }
            finally
            {
                azureTemp?.Dispose();
            }

            // Summary line always shown
            var parts = new List<string>();
            parts.Add($"{countSigned} signed");
            if (countAlreadySigned > 0) parts.Add($"{countAlreadySigned} already signed");
            if (countUnsignable    > 0) parts.Add($"{countUnsignable} not signable");
            if (countFailed        > 0) parts.Add($"{countFailed} FAILED");
            Console.WriteLine($"  files: {string.Join(", ", parts)}");

            if (countFailed > 0)
                throw new InvalidOperationException(
                    $"Code signing failed for {countFailed} file(s). See output above.");
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
