using System;
using System.IO;
using Xunit;

namespace AlliePack.Tests
{
    /// <summary>
    /// A <see cref="FactAttribute"/> that skips itself when <c>wix.exe</c> is not on PATH,
    /// for tests that compile a real MSI.  There is no CI for this repository, so the
    /// toolchain prerequisite falls on whoever runs the suite locally; skipping keeps a
    /// checkout without WiX green instead of failing for an unrelated reason.
    /// <para>Install it with: <c>dotnet tool install --global wix --version 5.*</c></para>
    /// </summary>
    public sealed class WixRequiredFactAttribute : FactAttribute
    {
        public WixRequiredFactAttribute()
        {
            if (WixTool.Path == null)
                Skip = "wix.exe not found on PATH; skipping MSI-backed integration test.";
        }
    }

    /// <summary>Locates <c>wix.exe</c> once per test run.</summary>
    internal static class WixTool
    {
        private static readonly Lazy<string?> _path = new Lazy<string?>(Find);

        public static string? Path => _path.Value;

        private static string? Find()
        {
            string? pathVar = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathVar)) return null;

            foreach (string dir in pathVar!.Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                string candidate;
                try { candidate = System.IO.Path.Combine(dir.Trim(), "wix.exe"); }
                catch (ArgumentException) { continue; }   // malformed PATH entry
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }
    }
}
