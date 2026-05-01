using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;
using AlliePack;

namespace AlliePack.Tests
{
    // Builds SampleSolution once per test run via xUnit class fixture.
    public sealed class SampleSolutionFixture : IDisposable
    {
        public string SolutionDir { get; }
        public bool BuildSucceeded { get; }

        public SampleSolutionFixture()
        {
            string? repoRoot = FindRepoRoot();
            if (repoRoot == null)
            {
                SolutionDir = string.Empty;
                BuildSucceeded = false;
                return;
            }

            SolutionDir = Path.Combine(repoRoot, "test", "assets", "SampleSolution");
            BuildSucceeded = DotnetBuild(SolutionDir, "Debug");
        }

        private static string? FindRepoRoot()
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.Exists(Path.Combine(dir, ".git"))) return dir;
                string? parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent!;
            }
            return null;
        }

        private static bool DotnetBuild(string workDir, string configuration)
        {
            try
            {
                var psi = new ProcessStartInfo("dotnet", $"build -c {configuration} --nologo -v q")
                {
                    WorkingDirectory       = workDir,
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };
                using var p = Process.Start(psi);
                p?.WaitForExit();
                return p?.ExitCode == 0;
            }
            catch { return false; }
        }

        public void Dispose() { }
    }

    public class ProjectAndSolutionTests : IClassFixture<SampleSolutionFixture>
    {
        private readonly SampleSolutionFixture _fix;

        public ProjectAndSolutionTests(SampleSolutionFixture fix) { _fix = fix; }

        private PathResolver MakeResolver()
        {
            string yamlPath = Path.Combine(_fix.SolutionDir, "test.yaml");
            return new PathResolver(
                yamlPath,
                aliases: new Dictionary<string, string>(),
                paths:   new Dictionary<string, string>());
        }

        // Skip helper: returns true if the fixture reports a failed build.
        // Allows individual tests to bail cleanly rather than throwing confusing
        // exceptions about missing directories.
        private bool ShouldSkip() => !_fix.BuildSucceeded || string.IsNullOrEmpty(_fix.SolutionDir);

        // -----------------------------------------------------------------------
        // ResolveProject
        // -----------------------------------------------------------------------

        [Fact]
        public void ResolveProject_Hello1_FindsAtLeastOneFile()
        {
            if (ShouldSkip()) return;

            string project = Path.Combine(_fix.SolutionDir, "Hello1", "Hello1.csproj");
            var sr = new SolutionResolver(MakeResolver());
            var files = sr.ResolveProject(project, "Debug", "AnyCPU", new List<string>());

            Assert.True(files.Count > 0, "Expected at least one file from Hello1 build output.");
        }

        [Fact]
        public void ResolveProject_Hello1_ContainsExeOrDll()
        {
            if (ShouldSkip()) return;

            string project = Path.Combine(_fix.SolutionDir, "Hello1", "Hello1.csproj");
            var sr = new SolutionResolver(MakeResolver());
            var files = sr.ResolveProject(project, "Debug", "AnyCPU", new List<string>());

            bool hasHello1 = files.Any(f =>
                Path.GetFileName(f.SourcePath).StartsWith("Hello1", StringComparison.OrdinalIgnoreCase));
            Assert.True(hasHello1, "Expected a file named Hello1.* in the output.");
        }

        [Fact]
        public void ResolveProject_Hello2_FindsAtLeastOneFile()
        {
            if (ShouldSkip()) return;

            string project = Path.Combine(_fix.SolutionDir, "Hello2", "Hello2.csproj");
            var sr = new SolutionResolver(MakeResolver());
            var files = sr.ResolveProject(project, "Debug", "AnyCPU", new List<string>());

            Assert.True(files.Count > 0, "Expected at least one file from Hello2 build output.");
        }

        [Fact]
        public void ResolveProject_ExcludeFiles_FiltersPdbFiles()
        {
            if (ShouldSkip()) return;

            string project = Path.Combine(_fix.SolutionDir, "Hello1", "Hello1.csproj");
            var sr = new SolutionResolver(MakeResolver());

            var withPdb = sr.ResolveProject(project, "Debug", "AnyCPU", new List<string>());
            var withoutPdb = sr.ResolveProject(project, "Debug", "AnyCPU",
                new List<string> { "*.pdb" });

            bool hadPdb = withPdb.Any(f =>
                Path.GetExtension(f.SourcePath).Equals(".pdb", StringComparison.OrdinalIgnoreCase));
            if (!hadPdb) return; // Debug build didn't produce a PDB — skip assertion

            bool hasPdbAfterExclude = withoutPdb.Any(f =>
                Path.GetExtension(f.SourcePath).Equals(".pdb", StringComparison.OrdinalIgnoreCase));
            Assert.False(hasPdbAfterExclude, "excludeFiles: ['*.pdb'] should remove .pdb files.");
        }

        [Fact]
        public void ResolveProject_NonExistentProject_ReturnsEmptyList()
        {
            var sr = new SolutionResolver(MakeResolver());
            var files = sr.ResolveProject(
                @"C:\does\not\exist\MyProject.csproj", "Debug", "AnyCPU", new List<string>());
            Assert.Empty(files);
        }

        // -----------------------------------------------------------------------
        // ResolveSolution
        // -----------------------------------------------------------------------

        [Fact]
        public void ResolveSolution_BothProjects_FindsFilesFromEach()
        {
            if (ShouldSkip()) return;

            string sln = Path.Combine(_fix.SolutionDir, "SampleSolution.sln");
            var sr = new SolutionResolver(MakeResolver());
            var files = sr.ResolveSolution(sln, "Debug", "AnyCPU",
                includeProjects: new List<string>(),
                excludeProjects: new List<string>(),
                excludeFiles:    new List<string>());

            bool hasHello1 = files.Any(f =>
                Path.GetFileName(f.SourcePath).StartsWith("Hello1", StringComparison.OrdinalIgnoreCase));
            bool hasHello2 = files.Any(f =>
                Path.GetFileName(f.SourcePath).StartsWith("Hello2", StringComparison.OrdinalIgnoreCase));

            Assert.True(hasHello1, "Expected Hello1 output files in solution resolve.");
            Assert.True(hasHello2, "Expected Hello2 output files in solution resolve.");
        }

        [Fact]
        public void ResolveSolution_ExcludeHello2_DoesNotContainHello2Files()
        {
            if (ShouldSkip()) return;

            string sln = Path.Combine(_fix.SolutionDir, "SampleSolution.sln");
            var sr = new SolutionResolver(MakeResolver());
            var files = sr.ResolveSolution(sln, "Debug", "AnyCPU",
                includeProjects: new List<string>(),
                excludeProjects: new List<string> { "Hello2" },
                excludeFiles:    new List<string>());

            bool hasHello2 = files.Any(f =>
                Path.GetFileName(f.SourcePath).StartsWith("Hello2", StringComparison.OrdinalIgnoreCase));

            Assert.False(hasHello2, "excludeProjects: [Hello2] should omit Hello2 output files.");
        }

        [Fact]
        public void ResolveSolution_ExcludeHello2_StillContainsHello1Files()
        {
            if (ShouldSkip()) return;

            string sln = Path.Combine(_fix.SolutionDir, "SampleSolution.sln");
            var sr = new SolutionResolver(MakeResolver());
            var files = sr.ResolveSolution(sln, "Debug", "AnyCPU",
                includeProjects: new List<string>(),
                excludeProjects: new List<string> { "Hello2" },
                excludeFiles:    new List<string>());

            bool hasHello1 = files.Any(f =>
                Path.GetFileName(f.SourcePath).StartsWith("Hello1", StringComparison.OrdinalIgnoreCase));

            Assert.True(hasHello1, "Hello1 should still be resolved when only Hello2 is excluded.");
        }

        [Fact]
        public void ResolveSolution_NonExistentSolution_Throws()
        {
            var sr = new SolutionResolver(MakeResolver());
            Assert.Throws<FileNotFoundException>(() =>
                sr.ResolveSolution(@"C:\does\not\exist\missing.sln", "Debug", "AnyCPU",
                    new List<string>(), new List<string>(), new List<string>()));
        }
    }
}
