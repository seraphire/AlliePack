using System;
using System.Collections.Generic;

namespace AlliePack
{
    /// <summary>
    /// Replaces [KEY] tokens in arbitrary string values using the merged
    /// paths: / --define dictionary and the built-in tokens ([YamlDir],
    /// [GitRoot], [CurrentDir]).
    ///
    /// Unlike PathResolver, this class performs no path normalization, so it
    /// is safe to use on plain string fields such as MSBuild configuration
    /// ("Release", "Debug") and platform ("x86", "AnyCPU") where
    /// Path.GetFullPath would produce wrong results.
    /// </summary>
    public class TokenSubstitutor
    {
        private readonly string _yamlDir;
        private readonly string? _gitRoot;
        private readonly Dictionary<string, string> _tokens;

        public TokenSubstitutor(string yamlDir, string? gitRoot,
                                Dictionary<string, string> tokens)
        {
            _yamlDir  = yamlDir;
            _gitRoot  = gitRoot;
            _tokens   = tokens;
        }

        /// <summary>
        /// Applies built-in tokens only: [YamlDir], [CurrentDir], [GitRoot].
        /// Used when expanding user-defined token values so they can reference
        /// built-ins without circular dependencies between named tokens.
        /// </summary>
        private string ApplyBuiltins(string value)
        {
            value = value
                .Replace("[YamlDir]",    _yamlDir)
                .Replace("[CurrentDir]", Environment.CurrentDirectory);
            if (_gitRoot != null)
                value = value.Replace("[GitRoot]", _gitRoot);
            return value;
        }

        /// <summary>
        /// Substitutes all known tokens in <paramref name="value"/> and
        /// returns the result.  No path normalization is applied.
        /// </summary>
        public string Substitute(string value)
        {
            value = ApplyBuiltins(value);

            foreach (var token in _tokens)
                value = value.Replace($"[{token.Key}]", ApplyBuiltins(token.Value));

            return value;
        }
    }
}
