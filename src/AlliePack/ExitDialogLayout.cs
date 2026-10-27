using System;
using WixToolset.Dtf.WindowsInstaller;

namespace AlliePack
{
    /// <summary>
    /// Moves WixUI's ExitDialog launch checkbox (OptionalCheckBox) into the button band at
    /// the bottom of the dialog, left of the Back button.
    /// </summary>
    /// <remarks>
    /// WixUI places the checkbox over the dialog bitmap. Windows Installer cannot draw a
    /// CheckBox with a transparent background (the Transparent attribute applies to Text
    /// controls only), so it shows as a gray rectangle on the bitmap. The button band is
    /// that same gray, so the checkbox blends in there and its text stays clickable.
    ///
    /// ExitDialog is not overridable in WixUI, so the row is edited in the built MSI rather
    /// than in the WXS. Run it before the MSI is signed.
    /// </remarks>
    internal static class ExitDialogLayout
    {
        // WixUI ExitDialog: the bitmap ends at Y=234, the buttons sit at Y=243 with height
        // 17, and Back starts at X=180.
        public const int X = 10, Y = 243, Width = 165, Height = 17;

        // About what fits on one line in Width at the WixUI default font, after the box
        // itself. Width is about 220 px at 96 dpi; text ran about 4 px per character on the
        // gmStudio Finish page (33 characters, 133 px), so about 48 fit. Kept a little lower.
        public const int MaxTextLength = 45;

        /// <summary>Returns true when the checkbox row was found and moved.</summary>
        public static bool MoveLaunchCheckBox(string msiPath)
        {
            using var db = new Database(msiPath, DatabaseOpenMode.Transact);
            const string where = "`Dialog_`='ExitDialog' AND `Control`='OptionalCheckBox'";
            if (db.CountRows("Control", where) == 0)
                return false;

            db.Execute($"UPDATE `Control` SET `X`={X}, `Y`={Y}, `Width`={Width}, `Height`={Height} WHERE {where}");
            db.Commit();
            return true;
        }
    }
}
