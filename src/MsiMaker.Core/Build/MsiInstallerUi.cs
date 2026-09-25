using MsiMaker.Core.Msi;

namespace MsiMaker.Core.Build;

/// <summary>
/// Authors a minimal setup wizard into the database: Welcome, a destination
/// folder page with a working Browse dialog, a ready-to-install page, progress,
/// and a completion screen (plus cancel and failure screens). It lets a person
/// running the MSI by hand see what is happening and choose where it installs.
///
/// The dialog set is modelled on WiX's WixUI_InstallDir, harvested from a real
/// WiX build, but trimmed to what a first install needs and reauthored with only
/// built-in ControlEvents - SetTargetPath, DirectoryListUp/New, SpawnDialog,
/// Reset, EndDialog. WiX's own dialogs lean on a native custom-action DLL for
/// path validation; dropping it keeps this pure managed, at the cost of the
/// "is this path writable" pre-check, which the install itself still enforces.
///
/// Bitmaps and the elevation-shield glyph are left out, so the wizard is plainer
/// than a WiX one but needs no embedded images.
/// </summary>
internal static class MsiInstallerUi
{
    // Column definitions, exactly as harvested from a WiX-built MSI's _Columns.
    private static readonly MsiColumn[] DialogCols =
    [
        new("Dialog", 0x2D48), new("HCentering", 0x0502), new("VCentering", 0x0502),
        new("Width", 0x0502), new("Height", 0x0502), new("Attributes", 0x1104),
        new("Title", 0x1F80), new("Control_First", 0x0D32),
        new("Control_Default", 0x1D32), new("Control_Cancel", 0x1D32),
    ];
    private static readonly MsiColumn[] ControlCols =
    [
        new("Dialog_", 0x2D48), new("Control", 0x2D32), new("Type", 0x0D14),
        new("X", 0x0502), new("Y", 0x0502), new("Width", 0x0502), new("Height", 0x0502),
        new("Attributes", 0x1104), new("Property", 0x1D48), new("Text", 0x1F00),
        new("Control_Next", 0x1D32), new("Help", 0x1F32),
    ];
    private static readonly MsiColumn[] ControlEventCols =
    [
        new("Dialog_", 0x2D48), new("Control_", 0x2D32), new("Event", 0x2D32),
        new("Argument", 0x2DFF), new("Condition", 0x3DFF), new("Ordering", 0x1502),
    ];
    private static readonly MsiColumn[] EventMappingCols =
    [
        new("Dialog_", 0x2D48), new("Control_", 0x2D32), new("Event", 0x2D32), new("Attribute", 0x0D32),
    ];
    private static readonly MsiColumn[] TextStyleCols =
    [
        new("TextStyle", 0x2D48), new("FaceName", 0x0D20), new("Size", 0x0502),
        new("Color", 0x1104), new("StyleBits", 0x1502),
    ];

    private const string InstallDir = "INSTALLFOLDER";

    /// <summary>
    /// Emits the wizard. <paramref name="offerFolderChoice"/> false drops the
    /// destination-folder page (Welcome goes straight to the ready page) for a
    /// package where the install location is not the user's to choose.
    /// </summary>
    public static void Emit(MsiDatabase db, bool offerFolderChoice = true)
    {
        var dialog = db.Table("Dialog", DialogCols);
        var control = db.Table("Control", ControlCols);
        var evt = db.Table("ControlEvent", ControlEventCols);
        var map = db.Table("EventMapping", EventMappingCols);
        var style = db.Table("TextStyle", TextStyleCols);

        // Fonts the control text refers to as {\WixUI_Font_*}.
        style.Add("WixUI_Font_Normal", "Tahoma", 8, null, null);
        style.Add("WixUI_Font_Bigger", "Tahoma", 12, null, null);
        style.Add("WixUI_Font_Title", "Tahoma", 9, null, 1);

        // WIXUI_INSTALLDIR holds the *name* of the directory property to edit, so
        // the PathEdit and Browse controls edit INSTALLFOLDER's path and
        // SetTargetPath [WIXUI_INSTALLDIR] applies it. DefaultUIFont sets the base
        // font for controls that name none.
        db.SetProperty("WIXUI_INSTALLDIR", InstallDir);
        db.SetProperty("DefaultUIFont", "WixUI_Font_Normal");

        // Local helpers keep the row lists readable.
        void Dlg(string id, int w, int h, int attr, string first, string def, string cancel) =>
            dialog.Add(id, 50, 50, w, h, attr, "[ProductName] Setup", first, def, cancel);
        void Text(string dlg, string id, int x, int y, int w, int h, int attr, string text) =>
            control.Add(dlg, id, "Text", x, y, w, h, attr, null, text, null, null);
        void Line(string dlg, string id, int x, int y, int w) =>
            control.Add(dlg, id, "Line", x, y, w, 0, 1, null, null, null, null);
        void Button(string dlg, string id, int x, int y, int w, int h, string text, string? next) =>
            control.Add(dlg, id, "PushButton", x, y, w, h, 3, null, text, next, null);
        void Ev(string dlg, string ctrl, string ev, string arg, string cond, int order) =>
            evt.Add(dlg, ctrl, ev, arg, cond, order);

        string afterWelcome = offerFolderChoice ? "InstallDirDlg" : "VerifyReadyDlg";

        // ---------------------------------------------------------- WelcomeDlg
        Dlg("WelcomeDlg", 370, 270, 7, "Next", "Next", "Cancel");
        Text("WelcomeDlg", "Title", 15, 20, 340, 60, 196611,
            "{\\WixUI_Font_Bigger}Welcome to the [ProductName] Setup Wizard");
        Text("WelcomeDlg", "Description", 15, 90, 340, 60, 196611,
            "The Setup Wizard will install [ProductName] on your computer. "
            + "Click Next to continue or Cancel to exit.");
        Line("WelcomeDlg", "BottomLine", 0, 234, 370);
        Button("WelcomeDlg", "Next", 236, 243, 56, 17, "&Next", "Cancel");
        Button("WelcomeDlg", "Cancel", 304, 243, 56, 17, "Cancel", "Next");
        Ev("WelcomeDlg", "Next", "NewDialog", afterWelcome, "1", 1);
        Ev("WelcomeDlg", "Cancel", "SpawnDialog", "CancelDlg", "1", 1);

        if (offerFolderChoice)
        {
            // ------------------------------------------------------ InstallDirDlg
            Dlg("InstallDirDlg", 370, 270, 7, "Folder", "Next", "Cancel");
            Text("InstallDirDlg", "Title", 15, 6, 340, 15, 196611,
                "{\\WixUI_Font_Title}Destination Folder");
            Text("InstallDirDlg", "Description", 25, 23, 320, 15, 196611,
                "Click Next to install to the default folder or click Change to choose another.");
            Text("InstallDirDlg", "FolderLabel", 20, 50, 320, 15, 3, "Install [ProductName] to:");
            control.Add("InstallDirDlg", "Folder", "PathEdit", 20, 66, 330, 18, 11,
                "WIXUI_INSTALLDIR", null, "ChangeFolder", null);
            Button("InstallDirDlg", "ChangeFolder", 20, 90, 60, 17, "&Change...", "Back");
            Line("InstallDirDlg", "BottomLine", 0, 234, 370);
            Button("InstallDirDlg", "Back", 180, 243, 56, 17, "&Back", "Next");
            Button("InstallDirDlg", "Next", 236, 243, 56, 17, "&Next", "Cancel");
            Button("InstallDirDlg", "Cancel", 304, 243, 56, 17, "Cancel", "Folder");
            Ev("InstallDirDlg", "ChangeFolder", "[_BrowseProperty]", "[WIXUI_INSTALLDIR]", "1", 1);
            Ev("InstallDirDlg", "ChangeFolder", "SpawnDialog", "BrowseDlg", "1", 2);
            Ev("InstallDirDlg", "Back", "NewDialog", "WelcomeDlg", "1", 1);
            Ev("InstallDirDlg", "Next", "SetTargetPath", "[WIXUI_INSTALLDIR]", "1", 1);
            Ev("InstallDirDlg", "Next", "NewDialog", "VerifyReadyDlg", "1", 2);
            Ev("InstallDirDlg", "Cancel", "SpawnDialog", "CancelDlg", "1", 1);

            // ------------------------------------------------------------ BrowseDlg
            Dlg("BrowseDlg", 370, 270, 7, "DirectoryCombo", "OK", "Cancel");
            Text("BrowseDlg", "Title", 15, 6, 340, 15, 196611,
                "{\\WixUI_Font_Title}Change destination folder");
            Text("BrowseDlg", "ComboLabel", 25, 58, 44, 10, 3, "&Look in:");
            control.Add("BrowseDlg", "DirectoryCombo", "DirectoryCombo", 70, 55, 220, 80, 393227,
                "_BrowseProperty", null, "Up", null);
            Button("BrowseDlg", "Up", 298, 55, 30, 17, "&Up", "NewFolder");
            Button("BrowseDlg", "NewFolder", 331, 55, 33, 17, "&New", "DirectoryList");
            control.Add("BrowseDlg", "DirectoryList", "DirectoryList", 25, 83, 340, 98, 15,
                "_BrowseProperty", null, "PathLabel", null);
            Text("BrowseDlg", "PathLabel", 25, 190, 320, 10, 3, "&Folder name:");
            control.Add("BrowseDlg", "PathEdit", "PathEdit", 25, 202, 340, 18, 11,
                "_BrowseProperty", null, "OK", null);
            Line("BrowseDlg", "BottomLine", 0, 234, 370);
            Button("BrowseDlg", "OK", 240, 243, 56, 17, "OK", "Cancel");
            Button("BrowseDlg", "Cancel", 304, 243, 56, 17, "Cancel", "DirectoryCombo");
            Ev("BrowseDlg", "Up", "DirectoryListUp", "0", "1", 1);
            Ev("BrowseDlg", "NewFolder", "DirectoryListNew", "0", "1", 1);
            Ev("BrowseDlg", "OK", "SetTargetPath", "[_BrowseProperty]", "1", 1);
            Ev("BrowseDlg", "OK", "EndDialog", "Return", "1", 2);
            Ev("BrowseDlg", "Cancel", "Reset", "0", "1", 1);
            Ev("BrowseDlg", "Cancel", "EndDialog", "Return", "1", 2);
            map.Add("BrowseDlg", "DirectoryCombo", "IgnoreChange", "IgnoreChange");
        }

        // ------------------------------------------------------- VerifyReadyDlg
        Dlg("VerifyReadyDlg", 370, 270, 7, "Install", "Install", "Cancel");
        Text("VerifyReadyDlg", "Title", 15, 15, 340, 15, 196610,
            "{\\WixUI_Font_Title}Ready to install [ProductName]");
        Text("VerifyReadyDlg", "Description", 25, 70, 320, 60, 2,
            "Click Install to begin the installation. Click Back to review or change your "
            + "settings. Click Cancel to exit.");
        Line("VerifyReadyDlg", "BottomLine", 0, 234, 370);
        Button("VerifyReadyDlg", "Back", 180, 243, 56, 17, "&Back", "Install");
        Button("VerifyReadyDlg", "Install", 236, 243, 56, 17, "&Install", "Cancel");
        Button("VerifyReadyDlg", "Cancel", 304, 243, 56, 17, "Cancel", "Back");
        Ev("VerifyReadyDlg", "Back", "NewDialog", afterWelcome, "1", 1);
        Ev("VerifyReadyDlg", "Install", "EndDialog", "Return", "1", 1);
        Ev("VerifyReadyDlg", "Cancel", "SpawnDialog", "CancelDlg", "1", 1);

        // ----------------------------------------------------------- ProgressDlg
        Dlg("ProgressDlg", 370, 270, 5, "Cancel", "Cancel", "Cancel");
        Text("ProgressDlg", "TitleInstalling", 20, 15, 330, 15, 196610,
            "{\\WixUI_Font_Title}Installing [ProductName]");
        Text("ProgressDlg", "TextInstalling", 20, 65, 330, 20, 3,
            "Please wait while the Setup Wizard installs [ProductName].");
        Text("ProgressDlg", "StatusLabel", 20, 100, 50, 10, 3, "Status:");
        Text("ProgressDlg", "ActionText", 75, 100, 285, 10, 3, "");
        control.Add("ProgressDlg", "ProgressBar", "ProgressBar", 20, 115, 330, 10, 65537,
            null, "Progress done", null, null);
        Line("ProgressDlg", "BottomLine", 0, 234, 370);
        Button("ProgressDlg", "Cancel", 304, 243, 56, 17, "Cancel", "Cancel");
        Ev("ProgressDlg", "Cancel", "SpawnDialog", "CancelDlg", "1", 1);
        map.Add("ProgressDlg", "ActionText", "ActionText", "Text");
        map.Add("ProgressDlg", "ProgressBar", "SetProgress", "Progress");

        // ------------------------------------------------------------ ExitDialog
        Dlg("ExitDialog", 370, 270, 7, "Finish", "Finish", "Finish");
        Text("ExitDialog", "Title", 15, 20, 340, 60, 196611,
            "{\\WixUI_Font_Bigger}Completed the [ProductName] Setup Wizard");
        Text("ExitDialog", "Description", 15, 90, 340, 40, 196611,
            "Click the Finish button to exit the Setup Wizard.");
        Line("ExitDialog", "BottomLine", 0, 234, 370);
        Button("ExitDialog", "Finish", 236, 243, 56, 17, "&Finish", "Finish");
        Ev("ExitDialog", "Finish", "EndDialog", "Return", "1", 999);

        // ------------------------------------------------------------ FatalError
        Dlg("FatalError", 370, 270, 7, "Finish", "Finish", "Finish");
        Text("FatalError", "Title", 15, 20, 340, 60, 196611,
            "{\\WixUI_Font_Bigger}[ProductName] Setup Wizard ended prematurely");
        Text("FatalError", "Description", 15, 90, 340, 60, 196611,
            "[ProductName] was not installed. The Setup Wizard ended before it could complete. "
            + "Click Finish to exit.");
        Line("FatalError", "BottomLine", 0, 234, 370);
        Button("FatalError", "Finish", 236, 243, 56, 17, "&Finish", "Finish");
        Ev("FatalError", "Finish", "EndDialog", "Exit", "1", 999);

        // -------------------------------------------------------------- UserExit
        Dlg("UserExit", 370, 270, 7, "Finish", "Finish", "Finish");
        Text("UserExit", "Title", 15, 20, 340, 60, 196611,
            "{\\WixUI_Font_Bigger}[ProductName] Setup Wizard was interrupted");
        Text("UserExit", "Description", 15, 90, 340, 60, 196611,
            "[ProductName] was not installed. To install it later, run Setup again. "
            + "Click Finish to exit.");
        Line("UserExit", "BottomLine", 0, 234, 370);
        Button("UserExit", "Finish", 236, 243, 56, 17, "&Finish", "Finish");
        Ev("UserExit", "Finish", "EndDialog", "Exit", "1", 999);

        // ------------------------------------------------------------- CancelDlg
        Dlg("CancelDlg", 260, 85, 7, "No", "No", "No");
        Text("CancelDlg", "Text", 48, 15, 194, 30, 3,
            "Are you sure you want to cancel [ProductName] installation?");
        Button("CancelDlg", "Yes", 72, 57, 56, 17, "&Yes", "No");
        Button("CancelDlg", "No", 132, 57, 56, 17, "&No", "Yes");
        Ev("CancelDlg", "Yes", "EndDialog", "Exit", "1", 1);
        Ev("CancelDlg", "No", "EndDialog", "Return", "1", 1);

        // Sequenced dialogs. The three termination dialogs use the special
        // negative sequence numbers the installer looks for at end of the
        // execute action: -1 success, -2 user-exit, -3 fatal.
        var ui = db.Std("InstallUISequence");
        ui.Add("WelcomeDlg", "NOT Installed", 1297);
        ui.Add("ProgressDlg", null, 1299);
        ui.Add("ExitDialog", null, -1);
        ui.Add("UserExit", null, -2);
        ui.Add("FatalError", null, -3);
    }
}
