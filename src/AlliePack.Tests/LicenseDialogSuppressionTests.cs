using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Xunit;
using AlliePack;

namespace AlliePack.Tests
{
    /// <summary>
    /// Tests for the WXS dialog-flow overrides that suppress <c>LicenseAgreementDlg</c>
    /// when no license file is configured.  Each method is exercised against a minimal
    /// in-memory WXS document so no WiX toolchain is required.
    /// </summary>
    public class LicenseDialogSuppressionTests
    {
        private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";

        /// <summary>Minimal WXS document with a Package element and no UI element.</summary>
        private static XDocument MakeDoc(bool withUiElement = false)
        {
            var package = new XElement(Wix + "Package");
            if (withUiElement)
                package.Add(new XElement(Wix + "UI"));

            return new XDocument(new XElement(Wix + "Wix", package));
        }

        private static IEnumerable<XElement> PublishElements(XDocument doc)
            => doc.Descendants(Wix + "Publish");

        private static XElement? FindPublish(XDocument doc, string dialog, string control)
            => PublishElements(doc).FirstOrDefault(p =>
                p.Attribute("Dialog")?.Value == dialog &&
                p.Attribute("Control")?.Value == control);

        // -----------------------------------------------------------------------
        // SuppressLicenseDialog  (WixUI_FeatureTree: Welcome -> Customize)
        // -----------------------------------------------------------------------

        [Fact]
        public void FeatureTree_WelcomeDlg_Next_PointsTo_CustomizeDlg()
        {
            var doc = MakeDoc();
            InstallerBuilder.SuppressLicenseDialog(doc);

            var pub = FindPublish(doc, "WelcomeDlg", "Next");
            Assert.NotNull(pub);
            Assert.Equal("NewDialog",   pub!.Attribute("Event")?.Value);
            Assert.Equal("CustomizeDlg", pub.Attribute("Value")?.Value);
            Assert.Equal("2",           pub.Attribute("Order")?.Value);
        }

        [Fact]
        public void FeatureTree_CustomizeDlg_Back_PointsTo_WelcomeDlg()
        {
            var doc = MakeDoc();
            InstallerBuilder.SuppressLicenseDialog(doc);

            var pub = FindPublish(doc, "CustomizeDlg", "Back");
            Assert.NotNull(pub);
            Assert.Equal("NewDialog",  pub!.Attribute("Event")?.Value);
            Assert.Equal("WelcomeDlg", pub.Attribute("Value")?.Value);
            Assert.Equal("2",          pub.Attribute("Order")?.Value);
        }

        [Fact]
        public void FeatureTree_CreatesUiElement_WhenAbsent()
        {
            var doc = MakeDoc(withUiElement: false);
            InstallerBuilder.SuppressLicenseDialog(doc);

            var ui = doc.Descendants(Wix + "UI").FirstOrDefault();
            Assert.NotNull(ui);
        }

        [Fact]
        public void FeatureTree_ReusesExistingUiElement()
        {
            var doc = MakeDoc(withUiElement: true);
            InstallerBuilder.SuppressLicenseDialog(doc);

            Assert.Single(doc.Descendants(Wix + "UI"));
        }

        // -----------------------------------------------------------------------
        // SuppressLicenseDialogInstallDir  (WixUI_InstallDir / WixUI_Mondo:
        //                                   Welcome -> InstallDir, skipping license)
        // -----------------------------------------------------------------------

        [Fact]
        public void InstallDir_WelcomeDlg_Next_PointsTo_InstallDirDlg()
        {
            var doc = MakeDoc();
            InstallerBuilder.SuppressLicenseDialogInstallDir(doc);

            var pub = FindPublish(doc, "WelcomeDlg", "Next");
            Assert.NotNull(pub);
            Assert.Equal("NewDialog",    pub!.Attribute("Event")?.Value);
            Assert.Equal("InstallDirDlg", pub.Attribute("Value")?.Value);
            Assert.Equal("2",            pub.Attribute("Order")?.Value);
        }

        [Fact]
        public void InstallDir_InstallDirDlg_Back_PointsTo_WelcomeDlg()
        {
            var doc = MakeDoc();
            InstallerBuilder.SuppressLicenseDialogInstallDir(doc);

            var pub = FindPublish(doc, "InstallDirDlg", "Back");
            Assert.NotNull(pub);
            Assert.Equal("NewDialog",  pub!.Attribute("Event")?.Value);
            Assert.Equal("WelcomeDlg", pub.Attribute("Value")?.Value);
            Assert.Equal("2",          pub.Attribute("Order")?.Value);
        }

        [Fact]
        public void InstallDir_CreatesUiElement_WhenAbsent()
        {
            var doc = MakeDoc(withUiElement: false);
            InstallerBuilder.SuppressLicenseDialogInstallDir(doc);

            var ui = doc.Descendants(Wix + "UI").FirstOrDefault();
            Assert.NotNull(ui);
        }

        [Fact]
        public void InstallDir_ReusesExistingUiElement()
        {
            var doc = MakeDoc(withUiElement: true);
            InstallerBuilder.SuppressLicenseDialogInstallDir(doc);

            Assert.Single(doc.Descendants(Wix + "UI"));
        }

        // -----------------------------------------------------------------------
        // Distinguish the two methods — verify they target different dialogs
        // -----------------------------------------------------------------------

        // -----------------------------------------------------------------------
        // Regression: InstallDirDlg requires WIXUI_INSTALLDIR or MSI raises error
        // 2819 ("Control [3] on dialog [2] needs a property linked to it") the
        // moment the dialog renders in full-UI mode.  WixSharp emits this property
        // for its native WixUI_InstallDir set but NOT for WixUI_Mondo, into which
        // this reroute splices InstallDirDlg.
        // -----------------------------------------------------------------------

        [Fact]
        public void InstallDir_Suppression_Emits_WIXUI_INSTALLDIR_Property()
        {
            var doc = MakeDoc();
            InstallerBuilder.SuppressLicenseDialogInstallDir(doc);

            var prop = doc.Descendants(Wix + "Property")
                          .FirstOrDefault(p => p.Attribute("Id")?.Value == "WIXUI_INSTALLDIR");
            Assert.NotNull(prop);
            Assert.Equal("INSTALLDIR", prop!.Attribute("Value")?.Value);
        }

        [Fact]
        public void InstallDir_DoesNotTarget_CustomizeDlg()
        {
            var doc = MakeDoc();
            InstallerBuilder.SuppressLicenseDialogInstallDir(doc);

            Assert.Null(FindPublish(doc, "CustomizeDlg", "Back"));
            Assert.DoesNotContain(PublishElements(doc),
                p => p.Attribute("Value")?.Value == "CustomizeDlg");
        }

        [Fact]
        public void FeatureTree_DoesNotTarget_InstallDirDlg()
        {
            var doc = MakeDoc();
            InstallerBuilder.SuppressLicenseDialog(doc);

            Assert.Null(FindPublish(doc, "InstallDirDlg", "Back"));
            Assert.DoesNotContain(PublishElements(doc),
                p => p.Attribute("Value")?.Value == "InstallDirDlg");
        }

        [Fact]
        public void FeatureTree_DoesNotEmit_WIXUI_INSTALLDIR_Property()
        {
            // The CustomizeDlg flow has no InstallDirDlg, so it must not gain the property.
            var doc = MakeDoc();
            InstallerBuilder.SuppressLicenseDialog(doc);

            Assert.DoesNotContain(doc.Descendants(Wix + "Property"),
                p => p.Attribute("Id")?.Value == "WIXUI_INSTALLDIR");
        }

        // -----------------------------------------------------------------------
        // EnsureInstallDirProperty  (defines WIXUI_INSTALLDIR for InstallDirDlg)
        // -----------------------------------------------------------------------

        private static IEnumerable<XElement> InstallDirProps(XDocument doc)
            => doc.Descendants(Wix + "Property")
                  .Where(p => p.Attribute("Id")?.Value == "WIXUI_INSTALLDIR");

        [Fact]
        public void EnsureInstallDirProperty_AddsProperty_WhenAbsent()
        {
            var doc = MakeDoc();
            InstallerBuilder.EnsureInstallDirProperty(doc);

            var prop = Assert.Single(InstallDirProps(doc));
            Assert.Equal("INSTALLDIR", prop.Attribute("Value")?.Value);
        }

        [Fact]
        public void EnsureInstallDirProperty_AddsProperty_UnderPackage()
        {
            var doc = MakeDoc();
            InstallerBuilder.EnsureInstallDirProperty(doc);

            var prop = InstallDirProps(doc).Single();
            Assert.Equal("Package", prop.Parent?.Name.LocalName);
        }

        [Fact]
        public void EnsureInstallDirProperty_IsIdempotent_WhenAlreadyPresent()
        {
            var doc = MakeDoc();
            InstallerBuilder.EnsureInstallDirProperty(doc);
            InstallerBuilder.EnsureInstallDirProperty(doc);

            Assert.Single(InstallDirProps(doc));
        }

        [Fact]
        public void EnsureInstallDirProperty_DoesNotOverwrite_ExistingValue()
        {
            // Simulates WixSharp having already emitted the property (native
            // WixUI_InstallDir set) -- the existing definition must be left intact.
            var doc = MakeDoc();
            var package = doc.Descendants(Wix + "Package").First();
            package.Add(new XElement(Wix + "Property",
                new XAttribute("Id",    "WIXUI_INSTALLDIR"),
                new XAttribute("Value", "CUSTOMDIR")));

            InstallerBuilder.EnsureInstallDirProperty(doc);

            var prop = Assert.Single(InstallDirProps(doc));
            Assert.Equal("CUSTOMDIR", prop.Attribute("Value")?.Value);
        }

        [Fact]
        public void EnsureInstallDirProperty_NoPackage_IsNoOp()
        {
            var doc = new XDocument(new XElement(Wix + "Wix"));
            InstallerBuilder.EnsureInstallDirProperty(doc);

            Assert.Empty(InstallDirProps(doc));
        }

        // -----------------------------------------------------------------------
        // MakeFeaturesConfigurable  (ConfigurableDirectory lights up the Browse
        // button on WixUI_FeatureTree's CustomizeDlg so the user can retarget the
        // install location -- otherwise the button is greyed out).
        // -----------------------------------------------------------------------

        private static XDocument MakeFeatureDoc(params (string id, string? display)[] features)
        {
            var package = new XElement(Wix + "Package");
            foreach (var (id, display) in features)
            {
                var f = new XElement(Wix + "Feature", new XAttribute("Id", id));
                if (display != null) f.SetAttributeValue("Display", display);
                package.Add(f);
            }
            return new XDocument(new XElement(Wix + "Wix", package));
        }

        private static IEnumerable<XElement> FeatureElements(XDocument doc)
            => doc.Descendants(Wix + "Feature");

        private static XElement FeatureById(XDocument doc, string id)
            => FeatureElements(doc).First(f => f.Attribute("Id")?.Value == id);

        private static string? ConfigDir(XElement f)
            => f.Attribute("ConfigurableDirectory")?.Value;

        [Fact]
        public void MakeFeaturesConfigurable_AddsINSTALLDIR_ToVisibleFeature()
        {
            var doc = MakeFeatureDoc(("Batch20Import", "collapse"));
            InstallerBuilder.MakeFeaturesConfigurable(doc);

            Assert.Equal("INSTALLDIR", ConfigDir(FeatureById(doc, "Batch20Import")));
        }

        [Fact]
        public void MakeFeaturesConfigurable_AppliesTo_AllVisibleFeatures()
        {
            var doc = MakeFeatureDoc(("A", "collapse"), ("B", "expand"), ("C", null));
            InstallerBuilder.MakeFeaturesConfigurable(doc);

            Assert.All(FeatureElements(doc), f => Assert.Equal("INSTALLDIR", ConfigDir(f)));
        }

        [Fact]
        public void MakeFeaturesConfigurable_SkipsCompleteRootFeature()
        {
            // WixSharp's hidden root feature must not gain a Browse button -- it never
            // appears in the tree, so the button would be unreachable.
            var doc = MakeFeatureDoc(("Complete", "hidden"), ("Batch20Import", "collapse"));
            InstallerBuilder.MakeFeaturesConfigurable(doc);

            Assert.Null(ConfigDir(FeatureById(doc, "Complete")));
            Assert.Equal("INSTALLDIR", ConfigDir(FeatureById(doc, "Batch20Import")));
        }

        [Fact]
        public void MakeFeaturesConfigurable_SkipsHiddenFeature()
        {
            var doc = MakeFeatureDoc(("SecretThing", "hidden"));
            InstallerBuilder.MakeFeaturesConfigurable(doc);

            Assert.Null(ConfigDir(FeatureById(doc, "SecretThing")));
        }

        [Fact]
        public void MakeFeaturesConfigurable_DoesNotOverwrite_ExistingConfigurableDirectory()
        {
            var doc = MakeFeatureDoc(("A", "collapse"));
            FeatureById(doc, "A").SetAttributeValue("ConfigurableDirectory", "CUSTOMDIR");

            InstallerBuilder.MakeFeaturesConfigurable(doc);

            Assert.Equal("CUSTOMDIR", ConfigDir(FeatureById(doc, "A")));
        }

        [Fact]
        public void MakeFeaturesConfigurable_IsIdempotent()
        {
            var doc = MakeFeatureDoc(("A", "collapse"));
            InstallerBuilder.MakeFeaturesConfigurable(doc);
            InstallerBuilder.MakeFeaturesConfigurable(doc);

            Assert.Equal("INSTALLDIR", ConfigDir(FeatureById(doc, "A")));
        }
    }
}
