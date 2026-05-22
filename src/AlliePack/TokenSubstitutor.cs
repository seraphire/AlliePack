using System;
using System.Collections.Generic;

namespace AlliePack
{
    /// <summary>
    /// Replaces compile-time tokens in arbitrary string values using the merged
    /// variables: / --define dictionary and the built-in tokens.
    ///
    /// Primary syntax: $(NAME) -- resolves on the build machine before the Package
    /// is produced.  This is distinct from WiX install-time tokens ([INSTALLDIR],
    /// [ProgramFilesFolder], etc.) which use [NAME] syntax and are resolved by
    /// Windows Installer at install time.  See ADR-0003.
    ///
    /// Legacy syntax: [NAME] for built-ins and user-defined tokens is still accepted
    /// for backward compatibility but emits a deprecation warning (once per token
    /// per build run).  [NAME] tokens that are NOT in the substitution dictionary
    /// (e.g. WiX install-time tokens) are never warned about and pass through
    /// unchanged.
    ///
    /// Unlike PathResolver, this class performs no path normalization, so it is safe
    /// to use on plain string fields such as MSBuild configuration ("Release",
    /// "Debug") and platform ("x86", "AnyCPU") where Path.GetFullPath would produce
    /// wrong results.
    /// </summary>
    public class TokenSubstitutor
    {
        private readonly string _yamlDir;
        private readonly string? _gitRoot;
        private readonly Dictionary<string, string> _tokens;

        // Tracks deprecated [NAME] tokens we have already warned about so we only
        // emit one warning per token per build run, not one per field.
        private readonly HashSet<string> _warnedDeprecated =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public TokenSubstitutor(string yamlDir, string? gitRoot,
                                Dictionary<string, string> tokens)
        {
            _yamlDir = yamlDir;
            _gitRoot = gitRoot;
            _tokens  = tokens;
        }

        // -----------------------------------------------------------------------
        // Built-in tokens
        // -----------------------------------------------------------------------

        /// <summary>
        /// Applies built-in tokens only: $(YamlDir), $(CurrentDir), $(GitRoot).
        /// Used when expanding user-defined token values so they can reference
        /// built-ins without circular dependencies between named tokens.
        /// </summary>
        private string ApplyBuiltins(string value)
        {
            // Primary $(NAME) syntax
            value = value
                .Replace("$(YamlDir)",    _yamlDir)
                .Replace("$(CurrentDir)", Environment.CurrentDirectory);
            if (_gitRoot != null)
                value = value.Replace("$(GitRoot)", _gitRoot);

            // Legacy [NAME] syntax -- warn once, then substitute
            value = WarnAndReplace(value, "[YamlDir]",    "$(YamlDir)",    _yamlDir);
            value = WarnAndReplace(value, "[CurrentDir]", "$(CurrentDir)", Environment.CurrentDirectory);
            if (_gitRoot != null)
                value = WarnAndReplace(value, "[GitRoot]", "$(GitRoot)", _gitRoot);

            return value;
        }

        // -----------------------------------------------------------------------
        // Public API
        // -----------------------------------------------------------------------

        /// <summary>
        /// Substitutes all known compile-time tokens in <paramref name="value"/>
        /// and returns the result.  No path normalization is applied.
        /// </summary>
        public string Substitute(string value)
        {
            value = ApplyBuiltins(value);

            foreach (var token in _tokens)
            {
                string resolved = ApplyBuiltins(token.Value);

                // Primary $(NAME) syntax
                value = value.Replace($"$({token.Key})", resolved);

                // Legacy [NAME] syntax -- warn once, then substitute
                value = WarnAndReplace(value, $"[{token.Key}]", $"$({token.Key})", resolved);
            }

            return value;
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private string WarnAndReplace(string value, string oldToken, string newToken,
                                      string replacement)
        {
            if (!value.Contains(oldToken))
                return value;

            if (_warnedDeprecated.Add(oldToken))
                Console.WriteLine(
                    $"Warning: '{oldToken}' uses deprecated [NAME] compile-time token syntax. " +
                    $"Replace with '{newToken}'. See ADR-0003.");

            return value.Replace(oldToken, replacement);
        }
    }
}
