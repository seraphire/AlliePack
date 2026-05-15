using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using AlliePack;

namespace AlliePack.Tests
{
    // -----------------------------------------------------------------------
    // TokenSubstitutor
    // -----------------------------------------------------------------------

    public class TokenSubstitutorTests
    {
        private static TokenSubstitutor Make(
            string yamlDir = @"C:\fake\yaml",
            string? gitRoot = null,
            Dictionary<string, string>? tokens = null)
            => new TokenSubstitutor(yamlDir, gitRoot, tokens ?? new Dictionary<string, string>());

        [Fact] public void NoTokens_InputIsUnchanged()
            => Assert.Equal("hello world", Make().Substitute("hello world"));

        [Fact] public void YamlDir_IsReplaced()
        {
            var sub = Make(yamlDir: @"C:\projects\myapp");
            Assert.Equal(@"C:\projects\myapp\output", sub.Substitute(@"[YamlDir]\output"));
        }

        [Fact] public void CurrentDir_IsReplaced()
            => Assert.Equal(Environment.CurrentDirectory, Make().Substitute("[CurrentDir]"));

        [Fact] public void GitRoot_WhenPresent_IsReplaced()
        {
            var sub = Make(gitRoot: @"C:\repo");
            Assert.Equal(@"C:\repo\src", sub.Substitute(@"[GitRoot]\src"));
        }

        [Fact] public void GitRoot_WhenAbsent_TokenIsLeftUnchanged()
            => Assert.Equal(@"[GitRoot]\src", Make(gitRoot: null).Substitute(@"[GitRoot]\src"));

        [Fact] public void NamedToken_IsReplaced()
        {
            var sub = Make(tokens: new Dictionary<string, string> { ["BuildDir"] = @"C:\build" });
            Assert.Equal(@"C:\build\output", sub.Substitute(@"[BuildDir]\output"));
        }

        [Fact] public void NamedTokenValueReferencingBuiltin_IsChained()
        {
            var sub = Make(
                yamlDir: @"C:\projects",
                tokens: new Dictionary<string, string> { ["Out"] = @"[YamlDir]\bin" });
            Assert.Equal(@"C:\projects\bin\release", sub.Substitute(@"[Out]\release"));
        }

        [Fact] public void UnknownToken_IsLeftUnchanged()
            => Assert.Equal("[Unknown]", Make().Substitute("[Unknown]"));

        [Fact] public void EmptyString_ReturnsEmpty()
            => Assert.Equal(string.Empty, Make().Substitute(string.Empty));
    }

    // -----------------------------------------------------------------------
    // PathResolver.Resolve()
    // -----------------------------------------------------------------------

    public class PathResolverResolveTests
    {
        private static PathResolver Make(
            string? yamlDir = null,
            Dictionary<string, string>? aliases = null,
            Dictionary<string, string>? variables = null,
            Dictionary<string, string>? defines = null)
        {
            string yamlPath = Path.Combine(yamlDir ?? Path.GetTempPath(), "test.yaml");
            return new PathResolver(
                yamlPath,
                aliases ?? new Dictionary<string, string>(),
                variables ?? new Dictionary<string, string>(),
                defines);
        }

        [Fact] public void RelativePath_BecomesAbsoluteRelativeToYamlDir()
        {
            string tempDir = Path.GetTempPath().TrimEnd('\\', '/');
            var resolver = Make(yamlDir: tempDir);
            string result = resolver.Resolve(@"subdir\file.txt");
            Assert.Equal(Path.Combine(tempDir, "subdir", "file.txt"), result);
        }

        [Fact] public void AbsolutePath_IsNormalized()
        {
            var resolver = Make();
            string result = resolver.Resolve(@"C:\foo\bar\..\baz");
            Assert.Equal(@"C:\foo\baz", result);
        }

        [Fact] public void Alias_IsExpanded()
        {
            var resolver = Make(aliases: new Dictionary<string, string> { ["src"] = @"C:\repo\source" });
            string result = resolver.Resolve(@"src:MyApp\bin");
            Assert.Equal(@"C:\repo\source\MyApp\bin", result);
        }

        [Fact] public void NamedToken_IsSubstituted()
        {
            var resolver = Make(variables: new Dictionary<string, string> { ["Bin"] = @"C:\build\bin" });
            string result = resolver.Resolve(@"[Bin]\MyApp.exe");
            Assert.Equal(@"C:\build\bin\MyApp.exe", result);
        }

        [Fact] public void DefineOverridesPath()
        {
            var resolver = Make(
                variables: new Dictionary<string, string> { ["Bin"] = @"C:\default\bin" },
                defines: new Dictionary<string, string> { ["Bin"] = @"C:\override\bin" });
            Assert.Equal(@"C:\override\bin\app.exe", resolver.Resolve(@"[Bin]\app.exe"));
        }

        [Fact] public void GlobPattern_IsNotNormalized()
        {
            var resolver = Make();
            string result = resolver.Resolve(@"C:\build\**\*.dll");
            Assert.Contains("**", result);
            Assert.Contains("*.dll", result);
        }
    }

    // -----------------------------------------------------------------------
    // PathResolver.ResolveGlob / ResolveGlobWithPaths
    // -----------------------------------------------------------------------

    public class PathResolverGlobTests : IDisposable
    {
        private readonly string _tempDir;

        public PathResolverGlobTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "AlliePack_Glob_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }

        private PathResolver MakeResolver() => new PathResolver(
            Path.Combine(_tempDir, "test.yaml"),
            aliases: new Dictionary<string, string>(),
            variables: new Dictionary<string, string>());

        private void Touch(string relativePath)
        {
            string full = Path.Combine(_tempDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            System.IO.File.WriteAllText(full, string.Empty);
        }

        [Fact] public void SingleStar_MatchesFilesInDir()
        {
            Touch(@"bin\foo.dll");
            Touch(@"bin\bar.dll");
            Touch(@"bin\readme.txt");

            var results = MakeResolver().ResolveGlob(Path.Combine(_tempDir, @"bin\*.dll"));

            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.EndsWith(".dll", r));
        }

        [Fact] public void DoubleStar_MatchesNestedFiles()
        {
            Touch(@"bin\Release\net48\app.dll");
            Touch(@"bin\Debug\net48\app.dll");
            Touch(@"bin\Release\net48\app.exe");

            var results = MakeResolver().ResolveGlob(Path.Combine(_tempDir, @"bin\**\*.dll"));

            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.EndsWith(".dll", r));
        }

        [Fact] public void NestedDoubleStars_MatchCorrectSubtree()
        {
            Touch(@"bin\Release\net48\MyApp.dll");
            Touch(@"bin\Debug\net48\MyApp.dll");
            Touch(@"bin\Release\net6.0\MyApp.dll");

            var results = MakeResolver().ResolveGlob(Path.Combine(_tempDir, @"bin\Release\**\*.dll"));

            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.Contains("Release", r));
        }

        [Fact] public void NonExistentDir_ReturnsEmpty()
        {
            var results = MakeResolver().ResolveGlob(Path.Combine(_tempDir, @"nonexistent\*.dll"));
            Assert.Empty(results);
        }

        [Fact] public void ResolveGlobWithPaths_RelativePaths_DoNotContainBaseDir()
        {
            Touch(@"bin\app.dll");
            Touch(@"bin\helper.dll");

            var results = MakeResolver().ResolveGlobWithPaths(Path.Combine(_tempDir, @"bin\*.dll"));

            Assert.Equal(2, results.Count);
            Assert.All(results, r => Assert.DoesNotContain(_tempDir, r.RelativePath));
        }

        [Fact] public void ResolveGlobWithPaths_AbsolutePaths_AreCorrect()
        {
            Touch(@"bin\app.dll");

            var results = MakeResolver().ResolveGlobWithPaths(Path.Combine(_tempDir, @"bin\*.dll"));

            Assert.Single(results);
            Assert.True(System.IO.File.Exists(results[0].AbsolutePath));
        }
    }

    // -----------------------------------------------------------------------
    // ConditionalString
    // -----------------------------------------------------------------------

    public class ConditionalStringTests
    {
        private static ConditionalString Scalar(string value) => new ConditionalString(value);

        private static ConditionalString Map(params (string key, string value)[] entries)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in entries) dict[k] = v;
            return new ConditionalString(dict);
        }

        [Fact] public void Scalar_ReturnsValue_WhenFlagsEmpty()
            => Assert.Equal("perMachine", Scalar("perMachine").Resolve(new List<string>()));

        [Fact] public void Scalar_ReturnsValue_WhenFlagsSet()
            => Assert.Equal("perMachine", Scalar("perMachine").Resolve(new List<string> { "Enterprise" }));

        [Fact] public void Map_MatchingFlag_ReturnsCorrectValue()
        {
            var cs = Map(("Enterprise", "perMachine"), ("Personal", "perUser"), ("_else", "perUser"));
            Assert.Equal("perMachine", cs.Resolve(new List<string> { "Enterprise" }));
            Assert.Equal("perUser",    cs.Resolve(new List<string> { "Personal" }));
        }

        [Fact] public void Map_NoMatchingFlag_FallsBackToElse()
        {
            var cs = Map(("Enterprise", "perMachine"), ("_else", "perUser"));
            Assert.Equal("perUser", cs.Resolve(new List<string> { "Unknown" }));
        }

        [Fact] public void Map_EmptyFlags_FallsBackToElse()
        {
            var cs = Map(("Enterprise", "perMachine"), ("_else", "perUser"));
            Assert.Equal("perUser", cs.Resolve(new List<string>()));
        }

        [Fact] public void Map_NoMatchAndNoElse_ReturnsEmpty()
        {
            var cs = Map(("Enterprise", "perMachine"));
            Assert.Equal(string.Empty, cs.Resolve(new List<string> { "Unknown" }));
        }

        [Fact] public void Map_FlagMatchIsCaseInsensitive()
        {
            var cs = Map(("Enterprise", "perMachine"), ("_else", "perUser"));
            Assert.Equal("perMachine", cs.Resolve(new List<string> { "ENTERPRISE" }));
            Assert.Equal("perMachine", cs.Resolve(new List<string> { "enterprise" }));
        }

        [Fact] public void Map_FirstMatchingFlagWins()
        {
            var cs = Map(("A", "from-A"), ("B", "from-B"));
            Assert.Equal("from-A", cs.Resolve(new List<string> { "A", "B" }));
            Assert.Equal("from-B", cs.Resolve(new List<string> { "B", "A" }));
        }
    }
}
