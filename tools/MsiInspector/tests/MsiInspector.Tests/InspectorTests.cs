using System;
using System.IO;
using System.Linq;
using Xunit;

namespace MsiInspector.Tests
{
    public class InspectorTests
    {
        [Fact]
        public void OpenAndReadSummary_ReturnsTitleSetByBuilder()
        {
            var msiPath = TestMsiBuilder.CreateMinimal(
                fixtureName: "summary-only",
                directories: new[]
                {
                    new TestMsiBuilder.DirectoryEntry { Directory = "TARGETDIR", DefaultDir = "SourceDir" }
                },
                components: Array.Empty<TestMsiBuilder.ComponentEntry>(),
                files: Array.Empty<TestMsiBuilder.FileEntry>());

            using var inspector = new Inspector(msiPath);

            Assert.Equal("MsiInspector Test Fixture", inspector.Summary.Title);
            Assert.Equal("MsiInspector.Tests", inspector.Summary.Author);
        }

        [Fact]
        public void Files_ReturnsAllRowsFromFileTable()
        {
            var msiPath = TestMsiBuilder.CreateMinimal(
                fixtureName: "files-and-components",
                directories: new[]
                {
                    new TestMsiBuilder.DirectoryEntry { Directory = "TARGETDIR", DefaultDir = "SourceDir" },
                    new TestMsiBuilder.DirectoryEntry { Directory = "INSTALLFOLDER", ParentDirectory = "TARGETDIR", DefaultDir = "MyApp" }
                },
                components: new[]
                {
                    new TestMsiBuilder.ComponentEntry { Component = "MainExe", Directory = "INSTALLFOLDER", KeyPath = "fileMain" }
                },
                files: new[]
                {
                    new TestMsiBuilder.FileEntry { File = "fileMain", Component = "MainExe", FileName = "App.exe", FileSize = 4096, Sequence = 1 },
                    new TestMsiBuilder.FileEntry { File = "fileLib",  Component = "MainExe", FileName = "App.dll", FileSize = 2048, Sequence = 2 }
                });

            using var inspector = new Inspector(msiPath);

            Assert.Equal(2, inspector.Files.Count);
            Assert.Contains(inspector.Files, f => f.FileKey == "fileMain" && f.FileName == "App.exe");
            Assert.Contains(inspector.Files, f => f.FileKey == "fileLib"  && f.FileName == "App.dll");
        }

        [Fact]
        public void GetFile_ByKey_ReturnsRow()
        {
            var msiPath = TestMsiBuilder.CreateMinimal(
                fixtureName: "get-file",
                directories: new[]
                {
                    new TestMsiBuilder.DirectoryEntry { Directory = "TARGETDIR", DefaultDir = "SourceDir" },
                    new TestMsiBuilder.DirectoryEntry { Directory = "INSTALLFOLDER", ParentDirectory = "TARGETDIR", DefaultDir = "MyApp" }
                },
                components: new[]
                {
                    new TestMsiBuilder.ComponentEntry { Component = "C1", Directory = "INSTALLFOLDER", KeyPath = "f1" }
                },
                files: new[]
                {
                    new TestMsiBuilder.FileEntry { File = "f1", Component = "C1", FileName = "X.dll", FileSize = 100, Sequence = 1 }
                });

            using var inspector = new Inspector(msiPath);
            var found = inspector.GetFile("f1");
            Assert.NotNull(found);
            Assert.Equal("X.dll", found!.FileName);

            Assert.Null(inspector.GetFile("does-not-exist"));
        }

        [Fact]
        public void ResolveDirectoryFullPath_WalksParentChain()
        {
            var msiPath = TestMsiBuilder.CreateMinimal(
                fixtureName: "resolve-path",
                directories: new[]
                {
                    new TestMsiBuilder.DirectoryEntry { Directory = "TARGETDIR", DefaultDir = "SourceDir" },
                    new TestMsiBuilder.DirectoryEntry { Directory = "ProgramFiles64Folder", ParentDirectory = "TARGETDIR", DefaultDir = "ProgramFiles64" },
                    new TestMsiBuilder.DirectoryEntry { Directory = "ManufacturerFolder", ParentDirectory = "ProgramFiles64Folder", DefaultDir = "GreatMigrations" },
                    new TestMsiBuilder.DirectoryEntry { Directory = "INSTALLFOLDER", ParentDirectory = "ManufacturerFolder", DefaultDir = "MyApp" }
                },
                components: Array.Empty<TestMsiBuilder.ComponentEntry>(),
                files: Array.Empty<TestMsiBuilder.FileEntry>());

            using var inspector = new Inspector(msiPath);
            var path = inspector.ResolveDirectoryFullPath("INSTALLFOLDER");
            Assert.Equal("SourceDir\\ProgramFiles64\\GreatMigrations\\MyApp", path);
        }

        [Fact]
        public void CountDirectoryEntries_DetectsSingleAndDuplicate()
        {
            // The user's stated test case: "do we only have directory a/b/c listed once?"
            var msiPath = TestMsiBuilder.CreateMinimal(
                fixtureName: "count-dirs",
                directories: new[]
                {
                    new TestMsiBuilder.DirectoryEntry { Directory = "TARGETDIR", DefaultDir = "SourceDir" },
                    new TestMsiBuilder.DirectoryEntry { Directory = "INSTALLFOLDER", ParentDirectory = "TARGETDIR", DefaultDir = "MyApp" }
                },
                components: Array.Empty<TestMsiBuilder.ComponentEntry>(),
                files: Array.Empty<TestMsiBuilder.FileEntry>());

            using var inspector = new Inspector(msiPath);
            Assert.Equal(1, inspector.CountDirectoryEntries("INSTALLFOLDER"));
            Assert.Equal(0, inspector.CountDirectoryEntries("DoesNotExist"));
        }

        [Fact]
        public void GetFilesIn_ReturnsFilesForGivenDirectoryViaComponent()
        {
            var msiPath = TestMsiBuilder.CreateMinimal(
                fixtureName: "files-in-dir",
                directories: new[]
                {
                    new TestMsiBuilder.DirectoryEntry { Directory = "TARGETDIR",     DefaultDir = "SourceDir" },
                    new TestMsiBuilder.DirectoryEntry { Directory = "BinFolder",     ParentDirectory = "TARGETDIR", DefaultDir = "bin" },
                    new TestMsiBuilder.DirectoryEntry { Directory = "AssetsFolder",  ParentDirectory = "TARGETDIR", DefaultDir = "assets" }
                },
                components: new[]
                {
                    new TestMsiBuilder.ComponentEntry { Component = "Exes",   Directory = "BinFolder",    KeyPath = "appExe" },
                    new TestMsiBuilder.ComponentEntry { Component = "Assets", Directory = "AssetsFolder", KeyPath = "logo" }
                },
                files: new[]
                {
                    new TestMsiBuilder.FileEntry { File = "appExe", Component = "Exes",   FileName = "App.exe", FileSize = 100, Sequence = 1 },
                    new TestMsiBuilder.FileEntry { File = "appDll", Component = "Exes",   FileName = "App.dll", FileSize = 200, Sequence = 2 },
                    new TestMsiBuilder.FileEntry { File = "logo",   Component = "Assets", FileName = "logo.png",FileSize = 300, Sequence = 3 }
                });

            using var inspector = new Inspector(msiPath);

            var binFiles = inspector.GetFilesIn("BinFolder").ToList();
            Assert.Equal(2, binFiles.Count);
            Assert.Contains(binFiles, f => f.FileKey == "appExe");
            Assert.Contains(binFiles, f => f.FileKey == "appDll");

            var assetFiles = inspector.GetFilesIn("AssetsFolder").ToList();
            Assert.Single(assetFiles);
            Assert.Equal("logo", assetFiles[0].FileKey);
        }

        [Fact]
        public void IdtDumper_WritesTableFiles()
        {
            var msiPath = TestMsiBuilder.CreateMinimal(
                fixtureName: "idt-dump",
                directories: new[]
                {
                    new TestMsiBuilder.DirectoryEntry { Directory = "TARGETDIR", DefaultDir = "SourceDir" }
                },
                components: new[]
                {
                    new TestMsiBuilder.ComponentEntry { Component = "C1", Directory = "TARGETDIR", KeyPath = "f1" }
                },
                files: new[]
                {
                    new TestMsiBuilder.FileEntry { File = "f1", Component = "C1", FileName = "x.txt", FileSize = 1, Sequence = 1 }
                });

            var outDir = Path.Combine(Path.GetDirectoryName(msiPath)!, "dump");
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);

            IdtDumper.Dump(msiPath, outDir);

            Assert.True(File.Exists(Path.Combine(outDir, "Directory.idt")), "Directory.idt should exist");
            Assert.True(File.Exists(Path.Combine(outDir, "Component.idt")), "Component.idt should exist");
            Assert.True(File.Exists(Path.Combine(outDir, "File.idt")),      "File.idt should exist");
            Assert.True(File.Exists(Path.Combine(outDir, "_SummaryInformation.txt")), "_SummaryInformation.txt should exist");

            var fileIdt = File.ReadAllText(Path.Combine(outDir, "File.idt"));
            Assert.Contains("File\t", fileIdt);   // header line starts with column name
            Assert.Contains("x.txt", fileIdt);    // row data present
        }
    }
}
