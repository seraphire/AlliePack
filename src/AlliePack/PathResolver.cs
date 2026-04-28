using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AlliePack
{
    public class PathResolver
    {
        private readonly string _yamlDir;
        private readonly string? _gitRoot;
        private readonly Dictionary<string, string> _aliases;

        public string WorkingDirectory => _gitRoot ?? _yamlDir;

        /// <summary>
        /// Plain token substitution without path normalization.
        /// Use this for non-path string fields (e.g. MSBuild configuration,
        /// platform) where Path.GetFullPath would produce wrong results.
        /// </summary>
        public TokenSubstitutor Tokens { get; }

        /// <param name="defines">
        /// Tokens from --define KEY=VALUE.  These are merged over the paths: block
        /// so command-line overrides take priority.  Values are used as-is (no
        /// built-in token expansion) so Windows backslash paths are safe.
        /// </param>
        public PathResolver(string yamlFilePath, Dictionary<string, string> aliases,
                            Dictionary<string, string> paths,
                            Dictionary<string, string>? defines = null)
        {
            _yamlDir = Path.GetDirectoryName(Path.GetFullPath(yamlFilePath)) ?? Environment.CurrentDirectory;
            _gitRoot = FindGitRoot(_yamlDir);
            _aliases = aliases;

            // Merge paths: entries then overlay --define entries (defines win on conflict).
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths)   merged[p.Key] = p.Value;
            if (defines != null)
                foreach (var d in defines) merged[d.Key] = d.Value;

            Tokens = new TokenSubstitutor(_yamlDir, _gitRoot, merged);
        }

        public string Resolve(string path)
        {
            path = Tokens.Substitute(path);

            // Replace aliases, then apply tokens again so alias values like
            // "[GitRoot]/src/..." resolve correctly.
            foreach (var alias in _aliases)
            {
                if (path.StartsWith(alias.Key + ":"))
                {
                    string aliasValue = ApplyTokens(alias.Value);
                    path = Path.Combine(aliasValue, path.Substring(alias.Key.Length + 1));
                    break; // Only one alias per path
                }
            }

            // Normalize the path, resolving any .. or . segments
            if (!path.Contains("*") && !path.Contains("?"))
            {
                if (!Path.IsPathRooted(path))
                    path = Path.Combine(_yamlDir, path);
                path = Path.GetFullPath(path);
            }
            else if (!Path.IsPathRooted(path))
            {
                path = Path.Combine(_yamlDir, path);
            }
            else
            {
                // Rooted glob — normalize any .. segments in the base portion
                int starIdx = path.IndexOf('*');
                string basePart = path.Substring(0, starIdx);
                string globPart = path.Substring(starIdx);
                if (basePart.Contains(".."))
                    path = Path.GetFullPath(basePart).TrimEnd('\\', '/') + "\\" + globPart.TrimStart('\\', '/');
            }

            return path;
        }

        private string? FindGitRoot(string startDir)
        {
            var dir = new DirectoryInfo(startDir);
            while (dir != null)
            {
                if (dir.GetDirectories(".git").Any() || dir.GetFiles(".git").Any())
                {
                    return dir.FullName;
                }
                dir = dir.Parent;
            }
            return null;
        }

        public List<string> ResolveGlob(string pattern)
        {
            return ResolveGlobWithPaths(pattern).Select(t => t.AbsolutePath).ToList();
        }

        public List<(string AbsolutePath, string RelativePath)> ResolveGlobWithPaths(string pattern)
        {
            string resolvedPattern = Resolve(pattern);

            if (resolvedPattern.Contains("**"))
            {
                int idx = resolvedPattern.IndexOf("**", StringComparison.Ordinal);
                string baseDir = resolvedPattern.Substring(0, idx).TrimEnd('\\', '/');
                string filePattern = resolvedPattern.Substring(idx + 2).TrimStart('\\', '/');
                if (string.IsNullOrEmpty(filePattern)) filePattern = "*";

                if (!Directory.Exists(baseDir)) return new List<(string, string)>();

                return Directory.GetFiles(baseDir, filePattern, SearchOption.AllDirectories)
                    .Select(f => (f, f.Substring(baseDir.Length).TrimStart('\\', '/')))
                    .ToList();
            }

            string? dir = Path.GetDirectoryName(resolvedPattern);
            string? filePat = Path.GetFileName(resolvedPattern);

            if (dir == null || !Directory.Exists(dir)) return new List<(string, string)>();

            return Directory.GetFiles(dir, filePat)
                .Select(f => (f, Path.GetFileName(f)))
                .ToList();
        }
    }
}
