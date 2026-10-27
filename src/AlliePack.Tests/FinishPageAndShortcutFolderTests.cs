using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using MsiInspector;
using WixSharp;
using Xunit;
using AlliePack;

namespace AlliePack.Tests
{
    /// <summary>
    /// Shortcut folder mapping (all-users spellings must not become literal folder names)
    /// and the move of the Finish-page launch checkbox into the button band.
    /// </summary>
    public class FinishPageAndShortcutFolderTests : IDisposable
    {
        static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";

        private readonly string _dir;

        public FinishPageAndShortcutFolderTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "alliepack-finish-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        // -----------------------------------------------------------------------
        // Shortcut folders
        // -----------------------------------------------------------------------

        [Theory]
        [InlineData("[CommonProgramMenuFolder]\\GreatMigrations", "%ProgramMenu%\\GreatMigrations")]
        [InlineData("[ProgramMenuFolder]\\Vendor",                "%ProgramMenu%\\Vendor")]
        [InlineData("[CommonDesktopFolder]",                      "%Desktop%")]
        [InlineData("[DesktopFolder]",                            "%Desktop%")]
        [InlineData("[CommonStartupFolder]",                      "%Startup%")]
        [InlineData("[StartupFolder]",                            "%Startup%")]
        [InlineData("startmenu",                                  "%ProgramMenu%")]
        [InlineData("desktop",                                    "%Desktop%")]
        [InlineData("startup",                                    "%Startup%")]
        public void ResolveFolder_MapsToWixSharpKeyword_ForEitherScope(string folder, string expected)
        {
            Assert.Equal(expected, InstallerBuilder.ResolveFolder(folder, isMachine: true));
            Assert.Equal(expected, InstallerBuilder.ResolveFolder(folder, isMachine: false));
        }

        // -----------------------------------------------------------------------
        // Finish-page checkbox
        // -----------------------------------------------------------------------

        /// <summary>
        /// Builds an MSI whose ExitDialog has WixUI's checkbox at its stock position. The
        /// dialog is authored here so the compile does not need WixToolset.UI.wixext.
        /// </summary>
        private string BuildMsi(bool withCheckBox)
        {
            string exe = Path.Combine(_dir, "App.exe");
            System.IO.File.WriteAllText(exe, "exe");

            var project = new Project("FinishApp",
                new Dir("%ProgramFiles%", new Dir("FinishApp", new WixSharp.File(exe))))
            {
                GUID = new Guid("8c1d5e0a-6b2f-4d3e-9f40-2a7b1c9e5d33"),
                OutDir = _dir,
                OutFileName = "FinishApp",
                UI = WUI.WixUI_ProgressOnly,
            };

            project.WixSourceGenerated += doc =>
            {
                XNamespace ui = "http://wixtoolset.org/schemas/v4/wxs/ui";
                doc.Descendants(ui + "WixUI").Remove();
                doc.Descendants().Where(e => e.Name.LocalName == "UIRef").Remove();

                var dialog = new XElement(Wix + "Dialog",
                    new XAttribute("Id", "ExitDialog"),
                    new XAttribute("Width", 370), new XAttribute("Height", 270),
                    new XElement(Wix + "Control",
                        new XAttribute("Id", "Finish"), new XAttribute("Type", "PushButton"),
                        new XAttribute("X", 236), new XAttribute("Y", 243),
                        new XAttribute("Width", 56), new XAttribute("Height", 17),
                        new XAttribute("Default", "yes"), new XAttribute("Cancel", "yes"),
                        new XAttribute("Text", "Finish"),
                        new XElement(Wix + "Publish",
                            new XAttribute("Event", "EndDialog"), new XAttribute("Value", "Return"))));
                if (withCheckBox)
                    dialog.Add(new XElement(Wix + "Control",
                        new XAttribute("Id", "OptionalCheckBox"), new XAttribute("Type", "CheckBox"),
                        new XAttribute("X", 135), new XAttribute("Y", 190),
                        new XAttribute("Width", 220), new XAttribute("Height", 40),
                        new XAttribute("Property", "WIXUI_EXITDIALOGOPTIONALCHECKBOX"),
                        new XAttribute("CheckBoxValue", "1"),
                        new XAttribute("Text", "Launch")));

                doc.Descendants(Wix + "Package").Single().Add(new XElement(Wix + "UI", dialog));
            };

            Compiler.BuildWxs(project, Compiler.OutputType.MSI);

            string wxs = Path.Combine(_dir, "FinishApp.wxs");
            string msi = Path.Combine(_dir, "FinishApp.msi");

            // BuildWxs writes Source paths relative to the current directory, so wix must
            // run from there too.
            var psi = new ProcessStartInfo(WixTool.Path!)
            {
                Arguments = "build \"" + wxs + "\" -arch x86 -sw1044 -sw5437 -o \"" + msi + "\"",
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using (var p = Process.Start(psi)!)
            {
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.True(p.ExitCode == 0,
                    "wix build failed (" + p.ExitCode + "):" + Environment.NewLine + stdout + Environment.NewLine + stderr);
            }

            return msi;
        }

        [WixRequiredFact]
        public void MoveLaunchCheckBox_PutsCheckBoxInButtonBand()
        {
            string msi = BuildMsi(withCheckBox: true);

            Assert.True(ExitDialogLayout.MoveLaunchCheckBox(msi));

            using var inspector = new Inspector(msi);
            var row = inspector.Query(
                "SELECT `X`, `Y`, `Width`, `Height` FROM `Control` " +
                "WHERE `Dialog_`='ExitDialog' AND `Control`='OptionalCheckBox'").Single();
            Assert.Equal(
                (ExitDialogLayout.X, ExitDialogLayout.Y, ExitDialogLayout.Width, ExitDialogLayout.Height),
                (Convert.ToInt32(row["X"]), Convert.ToInt32(row["Y"]),
                 Convert.ToInt32(row["Width"]), Convert.ToInt32(row["Height"])));
        }

        [WixRequiredFact]
        public void MoveLaunchCheckBox_ReturnsFalse_WhenThereIsNoCheckBox()
        {
            string msi = BuildMsi(withCheckBox: false);
            Assert.False(ExitDialogLayout.MoveLaunchCheckBox(msi));
        }
    }
}
