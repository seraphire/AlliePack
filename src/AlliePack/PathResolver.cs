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
        private readonly Dictionary<string, string> _paths;

        public string WorkingDirectory => _gitRoot ?? _yamlDir;

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
            _paths = merged;
        }

        /// <summary>
        /// Applies built-in tokens only ([YamlDir], [CurrentDir], [GitRoot]).
        /// Used when resolving paths: values so they can reference built-ins
        /// without creating circular dependencies.
        /// </summary>
        private string ApplyBuiltinTokens(string path)
        {
            path = path.Replace("[YamlDir]", _yamlDir)
                       .Replace("[CurrentDir]", Environment.CurrentDirectory);
            if (_gitRoot != null)
                path = path.Replace("[GitRoot]", _gitRoot);
            return path;
        }

        /// <summary>
        /// Applies all tokens: built-ins first, then user-defined paths: entries.
        /// paths: values are resolved through built-ins only (no chaining between
        /// paths: entries) to avoid circular references.
        /// </summary>
        private string ApplyTokens(string path)
        {
            path = ApplyBuiltinTokens(path);

            foreach (var p in _paths)
                path = path.Replace($"[{p.Key}]", ApplyBuiltinTokens(p.Value));

            return path;
        }

        public string Resolve(string path)
        {
            path = ApplyTokens(path);

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
