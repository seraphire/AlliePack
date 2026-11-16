using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using WixSharp;
using Xunit;
using AlliePack;

namespace AlliePack.Tests
{
    /// <summary>
    /// A file listed in structure: under more than one feature is one component
    /// referenced by every such feature, so it installs when any of them is selected.
    /// Before this, DeduplicateFiles kept the first feature only and the others did
    /// not install the file.
    /// </summary>
    public class SharedFeatureFileTests : IDisposable
    {
        private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";

        private readonly string _dir;

        public SharedFeatureFileTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "alliepack-sharedfeat-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_dir, "files"));
            foreach (var name in new[] { "shared.txt", "a.txt", "b.txt" })
                System.IO.File.WriteAllText(Path.Combine(_dir, "files", name), name);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        private InstallerBuilder MakeBuilder(AlliePackConfig? config = null, Options? options = null)
        {
            config ??= new AlliePackConfig();
            var resolver = new PathResolver(
                Path.Combine(_dir, "allie-pack.yaml"), config.Aliases, config.Variables);
            return new InstallerBuilder(
                config, resolver, new SolutionResolver(resolver), options ?? new Options(), new List<string>());
        }

        private ResolvedFile Shared(Feature? feature, FileSignMode? sign = null) => new ResolvedFile
        {
            SourcePath = Path.Combine(_dir, "files", "shared.txt"),
            RelativeDestinationPath = "shared.txt",
            WixFeature = feature,
            SignMode = sign,
        };

        // -----------------------------------------------------------------------
        // DeduplicateFiles: feature merging
        // -----------------------------------------------------------------------

        [Fact]
        public void TwoFeatures_KeepsOneFileInBoth()
        {
            var a = new Feature("A");
            var b = new Feature("B");

            var result = MakeBuilder().DeduplicateFiles(new List<ResolvedFile> { Shared(a), Shared(b) });

            var file = Assert.Single(result);
            Assert.Same(a, file.WixFeature);
            Assert.Equal(new[] { b }, file.ExtraFeatures);
        }

        [Fact]
        public void SameFeatureTwice_IsNotRepeated()
        {
            var a = new Feature("A");

            var file = Assert.Single(MakeBuilder().DeduplicateFiles(new List<ResolvedFile> { Shared(a), Shared(a) }));

            Assert.Same(a, file.WixFeature);
            Assert.Empty(file.ExtraFeatures);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void BaseStructure_Wins_WhicheverComesFirst(bool baseFirst)
        {
            var a = new Feature("A");
            var b = new Feature("B");
            var files = baseFirst
                ? new List<ResolvedFile> { Shared(null), Shared(a), Shared(b) }
                : new List<ResolvedFile> { Shared(a), Shared(b), Shared(null) };

            var file = Assert.Single(MakeBuilder().DeduplicateFiles(files));

            Assert.Null(file.WixFeature);
            Assert.Empty(file.ExtraFeatures);
        }

        [Fact]
        public void SignModes_AreStillMerged()
        {
            var a = new Feature("A");
            var b = new Feature("B");

            var file = Assert.Single(MakeBuilder().DeduplicateFiles(new List<ResolvedFile>
            {
                Shared(a, FileSignMode.Always), Shared(b, FileSignMode.Never),
            }));

            Assert.Equal(FileSignMode.Always, file.SignMode);
            Assert.Equal(new FileSignMode?[] { FileSignMode.Never }, file.DuplicateSignModes);
        }

        // -----------------------------------------------------------------------
        // AssignFeatures
        // -----------------------------------------------------------------------

        [Fact]
        public void AssignFeatures_SingleFeature_UsesFeatureProperty()
        {
            var a = new Feature("A");
            var wixFile = new WixSharp.File("x.txt");

            InstallerBuilder.AssignFeatures(wixFile, Shared(a));

            Assert.Equal(new[] { a }, wixFile.ActualFeatures);
            Assert.True(wixFile.Features == null || wixFile.Features.Length == 0);
        }

        [Fact]
        public void AssignFeatures_SeveralFeatures_UsesFeaturesArray()
        {
            var a = new Feature("A");
            var b = new Feature("B");
            var rf = Shared(a);
            rf.ExtraFeatures.Add(b);
            var wixFile = new WixSharp.File("x.txt");

            InstallerBuilder.AssignFeatures(wixFile, rf);

            Assert.Equal(new[] { a, b }, wixFile.Features);
            Assert.Equal(new[] { a, b }, wixFile.ActualFeatures);
        }

        // -----------------------------------------------------------------------
        // Serialized WXS: dedup + AssignFeatures handed to WixSharp
        // -----------------------------------------------------------------------

        [Fact]
        public void Wxs_ReferencesSharedComponentFromBothFeatures()
        {
            var a = new Feature("Feature A");
            var b = new Feature("Feature B");
            var unique = MakeBuilder().DeduplicateFiles(new List<ResolvedFile> { Shared(a), Shared(b) });

            var files = unique.Select(rf =>
            {
                var wf = new WixSharp.File(rf.SourcePath);
                InstallerBuilder.AssignFeatures(wf, rf);
                return wf;
            }).ToArray();

            // A plain Project, not a ManagedProject: ManagedProject makes WixSharp package
            // its CA assembly, which fails when the test host shadow-copies WixSharp.dll.
            var project = new Project("SharedFeat", new Dir(@"%ProgramFiles%\SharedFeat", files))
            {
                GUID = new Guid("c3d4e5f6-a7b8-9012-cdef-123456789012"),
                OutDir = _dir,
                OutFileName = "SharedFeat",
                UI = WUI.WixUI_ProgressOnly,
            };
            Compiler.BuildWxs(project, Compiler.OutputType.MSI);
            var doc = XDocument.Load(Path.Combine(_dir, "SharedFeat.wxs"));

            var componentId = (string?)Assert.Single(doc.Descendants(Wix + "Component")).Attribute("Id");
            var referencing = doc.Descendants(Wix + "Feature")
                .Where(f => f.Elements(Wix + "ComponentRef").Any(r => (string?)r.Attribute("Id") == componentId))
                .Select(f => (string?)f.Attribute("Title"))
                .OrderBy(t => t)
                .ToList();
            Assert.Equal(new[] { "Feature A", "Feature B" }, referencing);
        }
    }
}
