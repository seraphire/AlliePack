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
        // SuppressLicenseDialogMondo  (WixUI_Mondo: Welcome -> InstallDir -> Customize)
        // -----------------------------------------------------------------------

        [Fact]
        public void Mondo_WelcomeDlg_Next_PointsTo_InstallDirDlg()
        {
            var doc = MakeDoc();
            InstallerBuilder.SuppressLicenseDialogMondo(doc);

            var pub = FindPublish(doc, "WelcomeDlg", "Next");
            Assert.NotNull(pub);
            Assert.Equal("NewDialog",    pub!.Attribute("Event")?.Value);
            Assert.Equal("InstallDirDlg", pub.Attribute("Value")?.Value);
            Assert.Equal("2",            pub.Attribute("Order")?.Value);
        }

        [Fact]
        public void Mondo_InstallDirDlg_Back_PointsTo_WelcomeDlg()
        {
            var doc = MakeDoc();
            InstallerBuilder.SuppressLicenseDialogMondo(doc);

            var pub = FindPublish(doc, "InstallDirDlg", "Back");
            Assert.NotNull(pub);
            Assert.Equal("NewDialog",  pub!.Attribute("Event")?.Value);
            Assert.Equal("WelcomeDlg", pub.Attribute("Value")?.Value);
            Assert.Equal("2",          pub.Attribute("Order")?.Value);
        }

        [Fact]
        public void Mondo_CreatesUiElement_WhenAbsent()
        {
            var doc = MakeDoc(withUiElement: false);
            InstallerBuilder.SuppressLicenseDialogMondo(doc);

            var ui = doc.Descendants(Wix + "UI").FirstOrDefault();
            Assert.NotNull(ui);
        }

        [Fact]
        public void Mondo_ReusesExistingUiElement()
        {
            var doc = MakeDoc(withUiElement: true);
            InstallerBuilder.SuppressLicenseDialogMondo(doc);

            Assert.Single(doc.Descendants(Wix + "UI"));
        }

        // -----------------------------------------------------------------------
        // Distinguish the two methods — verify they target different dialogs
        // -----------------------------------------------------------------------

        [Fact]
        public void Mondo_DoesNotTarget_CustomizeDlg()
        {
            var doc = MakeDoc();
            InstallerBuilder.SuppressLicenseDialogMondo(doc);

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
    }
}
