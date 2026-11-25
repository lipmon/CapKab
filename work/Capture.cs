using System;
using System.Drawing;
using System.Globalization;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program {
    internal const string AppName = "CapKab";
    internal const string ProductKey = @"Software\CapKab";
    internal const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string StartupValue = "CapKab";
    internal const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CapKab";
    [STAThread] static void Main(string[] args) {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { SetProcessDPIAware(); }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length > 0 && args[0] == "--uninstall") { Remove(); return; }
        if (args.Length > 0 && args[0] == "--finish-uninstall") { FinishUninstall(args); return; }
        if (args.Length > 0 && args[0] == "--self-test") {
            string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            try {
                using (ScreenShot shot = ScreenShot.Take(SystemInformation.VirtualScreen))
                using (Bitmap crop = shot.Crop(new Rectangle(5, 9, 20, 20))) {
                    if (crop.GetPixel(0, 0) != shot.Image.GetPixel(5, 9) || crop.GetPixel(19, 19) != shot.Image.GetPixel(24, 28))
                        throw new Exception("Crop verification failed.");
                    string first = CaptureApp.SavePng(crop, folder);
                    string second = CaptureApp.SavePng(crop, folder);
                    using (Bitmap saved = new Bitmap(first))
                        if (saved.Width != 20 || saved.Height != 20 || first == second) throw new Exception("PNG verification failed.");
                }
                if (!Shutter.Load()) throw new Exception("Shutter sound verification failed.");
                Console.WriteLine("Screen capture, crop, PNG and shutter passed.");
            } catch (Exception ex) { Console.WriteLine(ex); Environment.ExitCode = 1; }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            return;
        }
        bool created;
        using (var mutex = new Mutex(true, @"Local\CapKab.1", out created)) {
            if (!created) { PostMessage(new IntPtr(65535), OpenSettingsMessage, IntPtr.Zero, IntPtr.Zero); return; }
            using (var app = new CaptureApp()) {
                if (args.Length == 0 || args[0] != "--background") app.ShowSettings();
                else app.RestWhenIdle();
                Application.Run(app);
            }
        }
    }
    static void Remove() {
        string temporary = null;
        bool started = false;
        try {
            string installed = InstalledExecutable();
            ValidateUninstallTarget(Application.ExecutablePath, installed);
            if (MessageBox.Show("Uninstall CapKab?", AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            using (var mutex = AcquireUninstallMutex(@"Local\CapKab.1")) {
                try {
                    temporary = Path.Combine(Path.GetTempPath(), "CapKab-Uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
                    using (var source = File.OpenRead(installed))
                    using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write)) source.CopyTo(destination);
                    using (var parent = System.Diagnostics.Process.GetCurrentProcess()) {
                        string arguments = "--finish-uninstall " + parent.Id.ToString(CultureInfo.InvariantCulture) + " " +
                            parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture);
                        using (var worker = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(temporary, arguments) {
                            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath()
                        })) {
                            if (worker == null) throw new IOException("Could not start the uninstall worker.");
                            started = true;
                        }
                    }
                } finally { mutex.ReleaseMutex(); }
            }
        } catch (Exception ex) { MessageBox.Show(ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally {
            if (!started && temporary != null) {
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
    static void FinishUninstall(string[] args) {
        try {
            int parentId;
            long parentStarted;
            if (args.Length != 3 || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out parentId) || parentId <= 0 ||
                !long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out parentStarted) || parentStarted <= 0)
                throw new InvalidOperationException("The uninstall request is invalid. Run uninstall from Windows Settings.");
            ValidateUninstallWorker(Application.ExecutablePath, Path.GetTempPath());
            WaitForUninstallParent(parentId, parentStarted, 10000);
            using (var mutex = AcquireUninstallMutex(@"Local\CapKab.1")) {
                try {
                    // Open the required registry parents before removing the executable, so access failures leave it intact.
                    using (var software = Registry.CurrentUser.OpenSubKey(@"Software", true))
                    using (var uninstall = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall", true))
                    using (var startup = Registry.CurrentUser.OpenSubKey(StartupKey, true)) {
                        string installed = InstalledExecutable();
                        File.Delete(Path.Combine(Path.GetDirectoryName(installed), "LICENSE.txt"));
                        DeleteUninstallExecutable(installed, installed, 3000);
                        File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"));
                        if (startup != null) startup.DeleteValue(StartupValue, false);
                        if (software != null) software.DeleteSubKeyTree(AppName, false);
                        RemoveEmptyInstallDirectory(Path.GetDirectoryName(installed));
                        if (uninstall != null) uninstall.DeleteSubKeyTree(AppName, false);
                    }
                } finally { mutex.ReleaseMutex(); }
            }
            // This running copy stays in TEMP for normal Windows temporary-file cleanup.
            MessageBox.Show("CapKab was uninstalled. Your saved screenshots were kept.", AppName);
        } catch (Exception ex) {
            MessageBox.Show("CapKab could not be fully uninstalled.\n\n" + ex.Message +
                "\n\nIf necessary, run CapKab Setup again and retry uninstall.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    static string InstalledExecutable() {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName, AppName + ".exe");
    }
    internal static void ValidateUninstallTarget(string actual, string expected) {
        if (!string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This copy is not installed in the CapKab installation folder. Use Windows Settings to uninstall the installed copy.");
    }
    internal static void ValidateUninstallWorker(string executable, string tempFolder) {
        string path = Path.GetFullPath(executable);
        string fileName = Path.GetFileNameWithoutExtension(path);
        const string prefix = "CapKab-Uninstall-";
        Guid id;
        if (!string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(tempFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) ||
            !fileName.StartsWith(prefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(fileName.Substring(prefix.Length), "N", out id))
            throw new InvalidOperationException("The uninstall worker location is invalid.");
    }
    internal static Mutex AcquireUninstallMutex(string name) {
        var mutex = new Mutex(false, name);
        try {
            bool acquired;
            try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Exit CapKab from the system tray, then run uninstall again.");
            return mutex;
        } catch { mutex.Dispose(); throw; }
    }
    internal static void WaitForUninstallParent(int processId, long startTicks, int timeout) {
        System.Diagnostics.Process parent;
        try { parent = System.Diagnostics.Process.GetProcessById(processId); }
        catch (ArgumentException) { return; }
        using (parent) {
            // A different start time means the original parent already exited and Windows reused its PID.
            try {
                if (parent.HasExited || parent.StartTime.ToUniversalTime().Ticks != startTicks) return;
                if (!parent.WaitForExit(timeout)) throw new IOException("The original uninstall process has not exited. Please try again.");
            } catch (InvalidOperationException) { if (!parent.HasExited) throw; }
        }
    }
    internal static void DeleteUninstallExecutable(string installed, string expected, int retryMilliseconds) {
        ValidateUninstallTarget(installed, expected);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (true) {
            try { File.Delete(installed); return; }
            catch (IOException) { if (timer.ElapsedMilliseconds >= retryMilliseconds) throw; }
            catch (UnauthorizedAccessException) { if (timer.ElapsedMilliseconds >= retryMilliseconds) throw; }
            Thread.Sleep(100);
        }
    }
    internal static void RemoveEmptyInstallDirectory(string directory) {
        if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0) {
            try { Directory.Delete(directory); }
            catch (IOException) {
                // Preserve any file another process created between the empty check and removal.
                if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length != 0) return;
                throw;
            }
        }
    }
    internal static readonly uint OpenSettingsMessage = RegisterWindowMessage("CapKab.ShowSettings.1");
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
}

struct Shortcut {
    internal Keys Key;
    internal uint Modifiers;
    internal Shortcut(Keys key, uint modifiers) { Key = key; Modifiers = modifiers; }
    public override string ToString() {
        string keyName = Key >= Keys.D0 && Key <= Keys.D9 ? (Key - Keys.D0).ToString(CultureInfo.InvariantCulture) : Key.ToString();
        return ((Modifiers & 2) != 0 ? "Ctrl + " : "") + ((Modifiers & 1) != 0 ? "Alt + " : "") +
            ((Modifiers & 4) != 0 ? "Shift + " : "") + keyName;
    }
    internal int Packed { get { return ((int)Modifiers << 16) | (int)Key; } }
    internal static Shortcut Unpack(int value) { return new Shortcut((Keys)(value & 0xffff), (uint)(value >> 16)); }
}


enum CaptureMode { Rectangle, Fullscreen, Scrolling, FixedSize, Freehand }

sealed class CaptureApp : ApplicationContext {
    static readonly string[] Names = { "Rectangle", "Fullscreen", "Scrolling window", "Fixed size", "Freehand" };
    static readonly Shortcut[] DefaultShortcuts = { new Shortcut(Keys.D4, 1), new Shortcut(Keys.F10, 3),
        new Shortcut(Keys.F11, 3), new Shortcut(Keys.F8, 3), new Shortcut(Keys.F7, 3) };
    static readonly string[] KeysForMode = { "Region", "Screen", "Scroll", "Fixed", "Freehand" };
    static readonly Color Ink = Color.FromArgb(33, 33, 29);
    static readonly Color Accent = Color.FromArgb(255, 221, 99);
    readonly NotifyIcon tray;
    readonly SettingsWindow window;
    readonly Icon icon;
    readonly Panel captureModes, preferences, fileNames;
    readonly Button modesTab, preferencesTab, namesTab, folderLink, themeButton, languageButton;
    readonly TextBox prefixBox;
    readonly RadioButton[] namePatternButtons = new RadioButton[CaptureNames.Templates.Length];
    FileNamePattern draftPattern;
    readonly Label nameExample;
    readonly ToolTip tooltips = new ToolTip();
    readonly Dictionary<Control, string> localizedControls = new Dictionary<Control, string>();
    readonly Dictionary<ToolStripItem, string> localizedMenu = new Dictionary<ToolStripItem, string>();
    readonly TextBox folderBox;
    readonly TextBox[] shortcutFields = new TextBox[Names.Length];
    readonly Label[] hotkeyLabels = new Label[Names.Length];
    readonly CheckBox audioBox, autoBox, startupBox, hideBox;
    readonly NumericUpDown widthBox, heightBox;
    // One-shot: fires once after the last capture or save, then stays stopped. Nothing polls while idle.
    readonly System.Windows.Forms.Timer idleTimer = new System.Windows.Forms.Timer { Interval = 3000 };
    readonly Shortcut[] shortcuts = new Shortcut[Names.Length];
    readonly Shortcut[] drafts = new Shortcut[Names.Length];
    readonly int[] registered = new int[Names.Length];
    readonly ContextMenuStrip menu;
    string folder, filePrefix;
    FileNamePattern filePattern;
    Task saveQueue = Task.FromResult(0);
    int pendingSaves;
    bool exitRequested;
    bool sound, autosave, startWithWindows, hideOnClose, darkMode, thai, busy;
    int fixedWidth, fixedHeight, nextHotkeyId = 100;
    readonly float scale;   // display scaling of the settings window: 1 at 100%, 1.5 at 150%
    int shutterCount;       // captures that asked for the shutter sound
    bool shutterAccepted;   // whether Windows accepted the most recent one for playback

    internal CaptureApp() {
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrEmpty(pictures)) pictures = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        using (var key = OpenSettingsKey(Program.ProductKey)) {
            folder = (string)ReadSetting(key, "Folder", Path.Combine(pictures, "CapKab"));
            filePrefix = (string)ReadSetting(key, "FilePrefix", "CapKab");
            if (!CaptureNames.ValidPrefix(filePrefix)) filePrefix = "CapKab";
            int pattern = (int)ReadSetting(key, "FilePattern", 0);
            filePattern = Enum.IsDefined(typeof(FileNamePattern), pattern) ? (FileNamePattern)pattern : FileNamePattern.DateTime;
            sound = (int)ReadSetting(key, "Sound", 1) != 0;
            autosave = (int)ReadSetting(key, "AutoSave", 1) != 0;
            hideOnClose = (int)ReadSetting(key, "HideOnClose", 1) != 0;
            darkMode = (int)ReadSetting(key, "DarkMode", 0) != 0;
            thai = (string)ReadSetting(key, "Language", "en") == "th";
            fixedWidth = Math.Max(16, Math.Min(16000, (int)ReadSetting(key, "Width", 800)));
            fixedHeight = Math.Max(16, Math.Min(16000, (int)ReadSetting(key, "Height", 600)));
            for (int i = 0; i < shortcuts.Length; i++)
                shortcuts[i] = Shortcut.Unpack((int)ReadSetting(key, KeysForMode[i], DefaultShortcuts[i].Packed));
            if (shortcuts[3].Key == Keys.F12) shortcuts[3] = new Shortcut(Keys.F8, 3);
        }
        using (var startup = OpenSettingsKey(Program.StartupKey))
            startWithWindows = ReadSetting(startup, Program.StartupValue, null) != null;
        icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        idleTimer.Tick += (s,e) => {
            idleTimer.Stop();
            if (!busy && pendingSaves == 0) ReleaseMemory();
        };
        // Text already follows the display scaling because fonts are sized in points. The layout below is written
        // in 96-DPI pixels and is scaled once, by hand, to match; Windows Forms does not do it for a window built in code.
        using (Graphics screen = Graphics.FromHwnd(IntPtr.Zero)) scale = screen.DpiX / 96f;
        window = new SettingsWindow(this) {
            Text = Program.AppName + " Settings", Icon = icon, StartPosition = FormStartPosition.CenterScreen,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = true,
            AutoScaleMode = AutoScaleMode.None, Font = new Font("Segoe UI", 9f),
            BackColor = Color.FromArgb(248, 248, 245), ForeColor = Ink
        };
        window.FormClosing += (s,e) => {
            if (e.CloseReason == CloseReason.WindowsShutDown) {
                // Let a screenshot that is still being written finish before Windows ends the session.
                try { saveQueue.Wait(2000); } catch (AggregateException) { }
                return;
            }
            e.Cancel = true;
            if (busy) return;
            if (e.CloseReason == CloseReason.UserClosing && hideOnClose) { ApplyLanguage(thai); window.Hide(); RestWhenIdle(); }
            // Any other close (the [X] with auto-hide off, or Task Manager) ends the app instead of leaving a tray icon without its window.
            else window.BeginInvoke(new Action(RequestExit));
        };
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        window.Controls.Add(shell);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1,
            Padding = new Padding(16, 12, 19, 0), Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var tabs = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false,
            Margin = Padding.Empty };
        modesTab = ButtonFor("Capture modes", () => SetPage(false), true);
        preferencesTab = ButtonFor("Preferences", () => { if (!preferences.Visible) OpenPreferences(); });
        namesTab = ButtonFor("File names", () => { if (!fileNames.Visible) OpenFileNames(); });
        tabs.Controls.Add(modesTab);
        tabs.Controls.Add(preferencesTab);
        tabs.Controls.Add(namesTab);
        header.Controls.Add(tabs, 0, 0);
        // The label names the language a click switches to: TH while the interface is English, EN while it is Thai.
        languageButton = HeaderButton(ToggleLanguage);
        languageButton.Text = "TH";
        header.Controls.Add(languageButton, 1, 0);
        themeButton = HeaderButton(ToggleTheme);
        themeButton.Paint += (sender, e) => DrawThemeIcon(e.Graphics);
        header.Controls.Add(themeButton, 2, 0);
        shell.Controls.Add(header, 0, 0);

        var body = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        shell.Controls.Add(body, 0, 1);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 2,
            Padding = new Padding(22, 0, 22, 0), Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var footerRule = new Panel { Dock = DockStyle.Top, Height = 1, Margin = Padding.Empty, Tag = "divider" };
        footer.Controls.Add(footerRule, 0, 0);
        footer.SetColumnSpan(footerRule, 2);
        var copyright = new Label { Text = "Freeware · 2026 " + ProductInfo.Owner, AutoSize = true,
            Anchor = AnchorStyles.Left, Margin = new Padding(0, 9, 10, 10), Tag = "muted" };
        footer.Controls.Add(copyright, 0, 1);
        var version = new Label { Text = "v" + ProductInfo.Version, AutoSize = true, Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 7, 0, 8), Padding = new Padding(7, 2, 7, 2), Tag = "version" };
        footer.Controls.Add(version, 1, 1);
        shell.Controls.Add(footer, 0, 2);
        captureModes = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 9, 16, 12) };
        preferences = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 9, 16, 12), Visible = false };
        fileNames = new Panel { Dock = DockStyle.Fill, Padding = new Padding(19, 12, 19, 12), Visible = false };
        body.Controls.Add(fileNames);
        body.Controls.Add(preferences);
        body.Controls.Add(captureModes);

        var modeList = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top, ColumnCount = 2, RowCount = Names.Length + 2 };
        modeList.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        modeList.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        captureModes.Controls.Add(modeList);
        modeList.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var captureHint = LabelFor("Click a mode to capture, or press its shortcut.");
        captureHint.Margin = new Padding(3, 1, 3, 8);
        captureHint.Tag = "muted";
        modeList.Controls.Add(captureHint, 0, 0);
        modeList.SetColumnSpan(captureHint, 2);
        menu = new ContextMenuStrip();
        for (int i = 0; i < Names.Length; i++) {
            modeList.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            CaptureMode mode = (CaptureMode)i;
            var tile = new ModeButton(mode, Names[i]);
            tile.Click += (s,e) => Capture(mode);
            modeList.Controls.Add(tile, 0, i + 1);
            hotkeyLabels[i] = new Label { AutoSize = true, Anchor = AnchorStyles.Left,
                Margin = new Padding(12, 0, 6, 0), Tag = "muted" };
            modeList.Controls.Add(hotkeyLabels[i], 1, i + 1);
        }
        modeList.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        folderLink = ButtonFor("", OpenFolder);
        folderLink.AutoSize = false;
        folderLink.Padding = Padding.Empty;
        folderLink.MinimumSize = Size.Empty;
        folderLink.Size = new Size(42, 34);
        folderLink.Paint += (sender, e) => DrawFolderIcon(e.Graphics, folderLink);
        folderLink.Anchor = AnchorStyles.Left;
        folderLink.Margin = new Padding(3, 8, 3, 13);
        modeList.Controls.Add(folderLink, 0, Names.Length + 1);

        var form = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 3 };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        for (int i = 0; i < 3; i++) form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        preferences.Controls.Add(form);

        var destination = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, RowCount = 1,
            Margin = new Padding(0, 0, 0, 12) };
        destination.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        destination.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        destination.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        folderBox = new TextBox { Dock = DockStyle.Fill };
        destination.Controls.Add(LabelFor("Save folder"), 0, 0);
        destination.Controls.Add(folderBox, 1, 0);
        destination.Controls.Add(ButtonFor("Browse…", Browse), 2, 0);
        form.Controls.Add(destination, 0, 0);
        form.SetColumnSpan(destination, 2);

        var general = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 6,
            Margin = new Padding(0, 0, 13, 0) };
        general.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        general.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 6; i++) general.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        form.Controls.Add(general, 0, 1);
        var generalTitle = LabelFor("General");
        generalTitle.Margin = new Padding(3, 0, 3, 6);
        general.Controls.Add(generalTitle, 0, 0);
        general.SetColumnSpan(generalTitle, 2);

        autoBox = ToggleFor("Auto-save PNG after capture");
        audioBox = ToggleFor("Play a quiet shutter sound");
        startupBox = ToggleFor("Run when Windows starts");
        hideBox = ToggleFor("Auto-hide when clicking [X]");
        var toggles = new[] { autoBox, audioBox, startupBox, hideBox };
        for (int i = 0; i < toggles.Length; i++) {
            general.Controls.Add(toggles[i], 0, i + 1);
            general.SetColumnSpan(toggles[i], 2);
        }
        widthBox = DimensionBox();
        heightBox = DimensionBox();
        var dimensions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        widthBox.Width = heightBox.Width = 58;
        dimensions.Controls.Add(widthBox);
        dimensions.Controls.Add(new Label { Text = "×", AutoSize = true, Margin = new Padding(1, 5, 1, 0) });
        dimensions.Controls.Add(heightBox);
        dimensions.Controls.Add(new Label { Text = "px", AutoSize = true, Margin = new Padding(4, 5, 0, 0) });
        general.Controls.Add(LabelFor("Fixed size"), 0, 5);
        general.Controls.Add(dimensions, 1, 5);

        var hotkeys = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2,
            RowCount = 7, Margin = new Padding(13, 0, 0, 0) };
        hotkeys.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        hotkeys.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 7; i++) hotkeys.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        form.Controls.Add(hotkeys, 1, 1);
        var hotkeyTitle = LabelFor("Keyboard shortcuts");
        hotkeyTitle.Margin = new Padding(3, 0, 3, 6);
        hotkeys.Controls.Add(hotkeyTitle, 0, 0);
        hotkeys.SetColumnSpan(hotkeyTitle, 2);
        for (int i = 0; i < Names.Length; i++) {
            shortcutFields[i] = HotkeyBox(i);
            hotkeys.Controls.Add(LabelFor(Names[i]), 0, i + 1);
            hotkeys.Controls.Add(shortcutFields[i], 1, i + 1);
        }
        var info = LabelFor("Click a field and press the new shortcut.");
        info.Tag = "muted";
        info.Margin = new Padding(3, 5, 3, 0);
        hotkeys.Controls.Add(info, 0, 6);
        hotkeys.SetColumnSpan(info, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0, 10, 0, 0) };
        actions.Controls.Add(ButtonFor("Save", Apply, true));
        form.Controls.Add(actions, 0, 2);
        form.SetColumnSpan(actions, 2);

        var namesForm = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 7, Margin = Padding.Empty };
        namesForm.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        namesForm.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < namesForm.RowCount; i++) namesForm.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        fileNames.Controls.Add(namesForm);
        prefixBox = new TextBox { MaxLength = CaptureNames.MaxPrefixLength, Dock = DockStyle.Fill, Margin = new Padding(3, 6, 3, 6) };
        var formats = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = new Padding(0, 4, 0, 4) };
        formats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        formats.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        for (int i = 0; i < 3; i++) formats.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        for (int i = 0; i < namePatternButtons.Length; i++) {
            FileNamePattern pattern = (FileNamePattern)i;
            var choice = new RadioButton { Text = CaptureNames.Templates[i], AutoSize = true,
                Anchor = AnchorStyles.Left, Margin = new Padding(3, 4, 9, 4) };
            choice.CheckedChanged += (sender,e) => {
                if (!choice.Checked) return;
                draftPattern = pattern;
                UpdateNameExample();
            };
            namePatternButtons[i] = choice;
            formats.Controls.Add(choice, i / 3, i % 3);
        }
        nameExample = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 3, 8), Tag = "muted" };
        nameExample.MaximumSize = new Size(480, 0);
        prefixBox.TextChanged += (sender, e) => UpdateNameExample();
        var namingHint = LabelFor("Name your captures. Choose from 6 formats.");
        namingHint.Margin = new Padding(3, 0, 3, 12);
        namingHint.Tag = "muted";
        namesForm.Controls.Add(namingHint, 0, 0);
        namesForm.SetColumnSpan(namingHint, 2);
        namesForm.Controls.Add(LabelFor("Name prefix"), 0, 1);
        namesForm.Controls.Add(prefixBox, 1, 1);
        namesForm.Controls.Add(formats, 0, 2);
        namesForm.SetColumnSpan(formats, 2);
        var formatLegend = LabelFor("$Y year · $M month · $D day · $H hour · $N minute · $S second · $MS millisecond");
        var symbolsLegend = LabelFor("%COMPUTERNAME% computer · %USERNAME% user");
        foreach (Label legend in new[] { formatLegend, symbolsLegend }) {
            legend.Margin = new Padding(3, 2, 3, 3);
            legend.MaximumSize = new Size(650, 0);
            legend.Tag = "muted";
        }
        namesForm.Controls.Add(formatLegend, 0, 3);
        namesForm.SetColumnSpan(formatLegend, 2);
        namesForm.Controls.Add(symbolsLegend, 0, 4);
        namesForm.SetColumnSpan(symbolsLegend, 2);
        namesForm.Controls.Add(LabelFor("Example"), 0, 5);
        namesForm.Controls.Add(nameExample, 1, 5);
        var nameActions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false, Margin = new Padding(0, 8, 0, 0) };
        nameActions.Controls.Add(ButtonFor("Save", ApplyFileNames, true));
        namesForm.Controls.Add(nameActions, 0, 6);
        namesForm.SetColumnSpan(nameActions, 2);

        menu.Items.Add("Open save folder", null, (s,e) => OpenFolder());
        menu.Items.Insert(0, new ToolStripMenuItem("Settings", null, (s,e) => ShowSettings()));
        menu.Items.Add("Exit", null, (s,e) => { if (!busy) RequestExit(); });
        tray = new NotifyIcon { Text = Program.AppName, Icon = icon, Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (s,e) => ShowSettings();
        RememberControlText(window);
        foreach (ToolStripItem item in menu.Items) if (item.Text != null) localizedMenu.Add(item, item.Text);
        if (scale != 1f) window.Scale(new SizeF(scale, scale));
        SetPage(false);
        // Without this Windows fades the window out when it hides, and the fading window lands in the screenshot.
        int noTransitions = 1;
        DwmSetWindowAttribute(window.Handle, 3, ref noTransitions, sizeof(int));
        for (int i = 0; i < shortcuts.Length; i++) registered[i] = TryRegister(shortcuts[i]);
        UpdateLabels();
    }

    sealed class SettingsWindow : Form {
        readonly CaptureApp app;
        internal SettingsWindow(CaptureApp app) { this.app = app; }
        protected override void WndProc(ref Message message) {
            if (message.Msg == 0x0312) {
                int index = Array.IndexOf(app.registered, message.WParam.ToInt32());
                if (index >= 0 && app.registered[index] != 0) app.Capture((CaptureMode)index);
            }
            if ((uint)message.Msg == Program.OpenSettingsMessage && Program.OpenSettingsMessage != 0) app.ShowSettings();
            base.WndProc(ref message);
        }
    }

    static RegistryKey OpenSettingsKey(string path) {
        try { return Registry.CurrentUser.OpenSubKey(path); }
        catch (System.Security.SecurityException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
    static object ReadSetting(RegistryKey key, string name, object fallback) {
        if (key == null) return fallback;
        object value = key.GetValue(name, fallback);
        return fallback == null || (value != null && value.GetType() == fallback.GetType()) ? value : fallback;
    }
    static Label LabelFor(string title) {
        return new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 0, 12, 0) };
    }
    static Button ButtonFor(string title, Action action, bool accent = false) {
        // Shrinking back to fit keeps a button in proportion after the window is scaled for the display;
        // the minimum is the size these buttons always had at 100%.
        var button = new Button { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.Flat,
            MinimumSize = new Size(75, 28), Tag = accent ? "accent" : null, BackColor = accent ? Accent : Color.White, ForeColor = Ink, Padding = new Padding(8, 0, 8, 0) };
        button.FlatAppearance.BorderColor = accent ? Accent : Color.Gainsboro;
        button.Click += (s,e) => action();
        return button;
    }
    static Button HeaderButton(Action click) {
        var button = ButtonFor("", click);
        button.AutoSize = false;
        button.Padding = Padding.Empty;
        button.MinimumSize = Size.Empty;
        button.Size = new Size(32, 32);
        button.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        return button;
    }

    static CheckBox ToggleFor(string text) {
        return new CheckBox { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 4, 3, 4) };
    }
    static void DrawFolderIcon(Graphics graphics, Button button) {
        float scale = graphics.DpiX / 96f;
        float x = button.ClientSize.Width / 2f - 9 * scale;
        float y = button.ClientSize.Height / 2f - 7 * scale;
        GraphicsState state = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TranslateTransform(x, y);
        graphics.ScaleTransform(scale, scale);
        using (var pen = new Pen(button.ForeColor, 1.5f) { LineJoin = LineJoin.Round }) {
            graphics.DrawPolygon(pen, new[] { new Point(0, 14), new Point(0, 1), new Point(6, 1),
                new Point(8, 4), new Point(17, 4), new Point(17, 6), new Point(19, 6),
                new Point(16, 14) });
            graphics.DrawLine(pen, 3, 6, 17, 6);
            graphics.DrawLine(pen, 3, 6, 0, 14);
        }
        graphics.Restore(state);
    }
    static NumericUpDown DimensionBox() {
        return new NumericUpDown { Minimum = 16, Maximum = 16000, Width = 72, TextAlign = HorizontalAlignment.Right };
    }
    TextBox HotkeyBox(int index) {
        var field = new TextBox { ReadOnly = true, Dock = DockStyle.Fill, BackColor = Color.White };
        field.KeyDown += (s,e) => {
            bool isKey = (e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z) || (e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9) ||
                (e.KeyCode >= Keys.F1 && e.KeyCode <= Keys.F11) || (e.KeyCode >= Keys.F13 && e.KeyCode <= Keys.F24);
            if (!isKey || !(e.Control || e.Alt || e.Shift)) return;
            e.SuppressKeyPress = true;
            drafts[index] = new Shortcut(e.KeyCode, (uint)((e.Control ? 2 : 0) | (e.Alt ? 1 : 0) | (e.Shift ? 4 : 0)));
            field.Text = drafts[index].ToString();
        };
        return field;
    }
    internal void ShowSettings() {
        if (busy) return;
        if (!window.Visible) { SetPage(false); window.Show(); }
        if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
        window.Activate();
    }
    void SetPage(bool edit) { ShowPage(edit ? preferences : captureModes); }

    void ShowPage(Panel page) {
        window.SuspendLayout();
        Size desired = page == preferences ? new Size(750, 475) :
            page == fileNames ? new Size(780, 515) : new Size(750, 457);
        Rectangle workArea = Screen.FromControl(window).WorkingArea;
        window.ClientSize = new Size(Math.Min(workArea.Width - 32, (int)Math.Round(desired.Width * scale)),
            Math.Min(workArea.Height - 60, (int)Math.Round(desired.Height * scale)));
        captureModes.Visible = page == captureModes;
        preferences.Visible = page == preferences;
        fileNames.Visible = page == fileNames;
        modesTab.Tag = page == captureModes ? "accent" : null;
        preferencesTab.Tag = page == preferences ? "accent" : null;
        namesTab.Tag = page == fileNames ? "accent" : null;
        ApplyTheme(darkMode);
        ApplyLanguage(thai);
        window.ResumeLayout(true);
    }

    void OpenFileNames() {
        prefixBox.Text = filePrefix;
        draftPattern = filePattern;
        namePatternButtons[(int)filePattern].Checked = true;
        UpdateNameExample();
        ShowPage(fileNames);
        namesTab.Focus();
    }

    void UpdateNameExample() {
        string prefix = prefixBox.Text.Trim();
        if (!CaptureNames.ValidPrefix(prefix)) {
            nameExample.Text = T("Remove characters that Windows does not allow in file names.");
            return;
        }
        DateTime now = DateTime.Now;
        nameExample.Text = CaptureNames.Stem(prefix, draftPattern, now, 1, true) + ".png";
    }

    void ApplyFileNames() {
        try {
            string prefix = prefixBox.Text.Trim();
            if (!CaptureNames.ValidPrefix(prefix)) throw new Exception("Remove characters that Windows does not allow in file names.");
            var pattern = draftPattern;
            using (var registry = Registry.CurrentUser.CreateSubKey(Program.ProductKey)) {
                registry.SetValue("FilePrefix", prefix);
                registry.SetValue("FilePattern", (int)pattern);
            }
            filePrefix = prefix;
            filePattern = pattern;
            SetPage(false);
        } catch (Exception ex) { Error(ex.Message); }
    }

    string T(string text) { return UiText.Translate(text, thai); }

    void RememberControlText(Control control) {
        if (UiText.HasTranslation(control.Text)) localizedControls.Add(control, control.Text);
        foreach (Control child in control.Controls) RememberControlText(child);
    }

    void ApplyLanguage(bool useThai) {
        thai = useThai;
        window.SuspendLayout();
        foreach (var entry in localizedControls) entry.Key.Text = T(entry.Value);
        foreach (var entry in localizedMenu) entry.Key.Text = T(entry.Value);
        folderLink.AccessibleName = T("Open save folder");
        tooltips.SetToolTip(folderLink, folderLink.AccessibleName);
        UpdateThemeButton();
        languageButton.Text = thai ? "EN" : "TH";
        languageButton.AccessibleName = thai ? T("Switch to English") : "เปลี่ยนเป็นภาษาไทย / Switch to Thai";
        tooltips.SetToolTip(languageButton, languageButton.AccessibleName);
        UpdateNameExample();
        UpdateLabels();
        window.ResumeLayout(true);
    }

    void ToggleLanguage() {
        ApplyLanguage(!thai);
        try {
            using (var key = Registry.CurrentUser.CreateSubKey(Program.ProductKey))
                key.SetValue("Language", thai ? "th" : "en");
        } catch (Exception ex) { Error(ex.Message); }
    }

    void ToggleTheme() {
        darkMode = !darkMode;
        ApplyTheme(darkMode);
        UpdateThemeButton();
        try {
            using (var key = Registry.CurrentUser.CreateSubKey(Program.ProductKey))
                key.SetValue("DarkMode", darkMode ? 1 : 0);
        } catch (Exception ex) { Error(ex.Message); }
    }

    void UpdateThemeButton() {
        themeButton.AccessibleName = T(darkMode ? "Switch to light mode" : "Switch to dark mode");
        tooltips.SetToolTip(themeButton, themeButton.AccessibleName);
        themeButton.Invalidate();
    }

    void DrawThemeIcon(Graphics graphics) {
        float scale = Math.Min(themeButton.ClientSize.Width, themeButton.ClientSize.Height) / 32f;
        GraphicsState state = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TranslateTransform(themeButton.ClientSize.Width / 2f, themeButton.ClientSize.Height / 2f);
        graphics.ScaleTransform(scale, scale);
        using (var pen = new Pen(themeButton.ForeColor, 1.55f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
            if (darkMode) {
                graphics.DrawEllipse(pen, -4, -4, 8, 8);
                for (int i = 0; i < 8; i++) {
                    double angle = i * Math.PI / 4;
                    float x = (float)Math.Cos(angle), y = (float)Math.Sin(angle);
                    graphics.DrawLine(pen, x * 6.5f, y * 6.5f, x * 9, y * 9);
                }
            } else {
                using (var moon = new GraphicsPath()) {
                    moon.AddBezier(2, -8, -7, -9, -11, 1, -5, 7);
                    moon.AddBezier(-5, 7, 0, 11, 7, 8, 9, 3);
                    moon.AddBezier(9, 3, 2, 5, -3, -2, 2, -8);
                    moon.CloseFigure();
                    graphics.DrawPath(pen, moon);
                }
            }
        }
        graphics.Restore(state);
    }

    void ApplyTheme(bool dark) {
        Color surface = dark ? Color.FromArgb(29, 30, 33) : Color.FromArgb(248, 248, 245);
        Color tile = dark ? Color.FromArgb(43, 45, 49) : Color.White;
        Color text = dark ? Color.FromArgb(244, 242, 233) : Ink;
        Color muted = dark ? Color.FromArgb(179, 179, 174) : Color.DimGray;
        Color border = dark ? Color.FromArgb(83, 85, 91) : Color.FromArgb(226, 226, 219);
        ThemeControl(window, dark, surface, tile, text, muted, border);
        menu.BackColor = tile;
        menu.ForeColor = text;
        if (dark) menu.Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors());
        else menu.RenderMode = ToolStripRenderMode.System;
        foreach (ToolStripItem item in menu.Items) item.ForeColor = text;
        int useDark = dark ? 1 : 0;
        if (DwmSetWindowAttribute(window.Handle, 20, ref useDark, sizeof(int)) != 0)
            DwmSetWindowAttribute(window.Handle, 19, ref useDark, sizeof(int));
        window.Invalidate(true);
    }

    static void ThemeControl(Control control, bool dark, Color surface, Color tile, Color text, Color muted, Color border) {
        control.ForeColor = Equals(control.Tag, "muted") || Equals(control.Tag, "version") ? muted : text;
        var button = control as Button;
        if (button != null) {
            bool accent = Equals(button.Tag, "accent");
            button.BackColor = accent ? Accent : tile;
            button.ForeColor = accent ? Ink : text;
            button.FlatAppearance.BorderColor = accent ? Accent : border;
            button.FlatAppearance.MouseOverBackColor = accent || !dark ? Color.FromArgb(255, 244, 203) : Color.FromArgb(69, 61, 43);
            button.FlatAppearance.MouseDownBackColor = dark && !accent ? Color.FromArgb(82, 69, 41) : Accent;
        } else if (Equals(control.Tag, "divider")) {
            control.BackColor = border;
        } else if (Equals(control.Tag, "version")) {
            control.BackColor = dark ? Color.FromArgb(49, 51, 55) : Color.FromArgb(234, 234, 228);
        } else if (control is TextBoxBase || control is NumericUpDown || control is ComboBox) {
            control.BackColor = tile;
        } else {
            control.BackColor = surface;
        }
        if (!(control is NumericUpDown))
            foreach (Control child in control.Controls) ThemeControl(child, dark, surface, tile, text, muted, border);
    }

    sealed class DarkMenuColors : ProfessionalColorTable {
        static readonly Color Base = Color.FromArgb(43, 45, 49);
        static readonly Color Line = Color.FromArgb(83, 85, 91);
        public override Color ToolStripDropDownBackground { get { return Base; } }
        public override Color ImageMarginGradientBegin { get { return Base; } }
        public override Color ImageMarginGradientMiddle { get { return Base; } }
        public override Color ImageMarginGradientEnd { get { return Base; } }
        public override Color MenuBorder { get { return Line; } }
        public override Color MenuItemBorder { get { return Line; } }
        public override Color MenuItemSelected { get { return Color.FromArgb(69, 61, 43); } }
        public override Color SeparatorDark { get { return Line; } }
        public override Color SeparatorLight { get { return Line; } }
    }

    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    void OpenPreferences() {
        Array.Copy(shortcuts, drafts, shortcuts.Length);
        for (int i = 0; i < drafts.Length; i++) shortcutFields[i].Text = drafts[i].ToString();
        folderBox.Text = folder;
        autoBox.Checked = autosave;
        audioBox.Checked = sound;
        startupBox.Checked = startWithWindows;
        hideBox.Checked = hideOnClose;
        widthBox.Value = fixedWidth;
        heightBox.Value = fixedHeight;
        SetPage(true);
        folderBox.SelectionStart = 0;
        folderBox.SelectionLength = 0;
        preferencesTab.Focus();
    }
    void OpenFolder() {
        try { Directory.CreateDirectory(folder); System.Diagnostics.Process.Start(folder); }
        catch (Exception ex) { Error(ex.Message); }
    }

    void Browse() {
        using (var dialog = new FolderBrowserDialog { Description = T("Choose where to save screenshots"),
            SelectedPath = Directory.Exists(folderBox.Text) ? folderBox.Text : folder }) {
            if (dialog.ShowDialog(window) == DialogResult.OK) folderBox.Text = dialog.SelectedPath;
        }
    }
    void UpdateLabels() {
        for (int i = 0; i < shortcuts.Length; i++)
            hotkeyLabels[i].Text = registered[i] == 0 ? T("Key unavailable") : shortcuts[i].ToString().Replace(" + ", "+");
    }
    void Apply() {
        var candidate = new int[registered.Length];
        var fresh = new int[registered.Length];
        try {
            string desired = folderBox.Text.Trim();
            if (desired.Length == 0 || !Path.IsPathRooted(desired)) throw new Exception("Choose an absolute save folder.");
            desired = Path.GetFullPath(desired);
            var seen = new HashSet<int>();
            for (int i = 0; i < drafts.Length; i++)
                if (!seen.Add(drafts[i].Packed)) throw new Exception("Assign a different shortcut to each mode.");
            for (int i = 0; i < candidate.Length; i++) {
                for (int j = 0; j < shortcuts.Length; j++)
                    if (drafts[i].Packed == shortcuts[j].Packed) candidate[i] = registered[j];
                if (candidate[i] == 0) fresh[i] = candidate[i] = TryRegister(drafts[i]);
                if (candidate[i] == 0 && drafts[i].Packed != shortcuts[i].Packed)
                    throw new Exception(string.Format(T("Windows could not register {0}. Choose another shortcut."), drafts[i]));
            }
            Directory.CreateDirectory(desired);
            if (startupBox.Checked != startWithWindows) {
                using (var startup = Registry.CurrentUser.CreateSubKey(Program.StartupKey)) {
                    if (startupBox.Checked)
                        startup.SetValue(Program.StartupValue, "\"" + Application.ExecutablePath + "\" --background");
                    else startup.DeleteValue(Program.StartupValue, false);
                }
                startWithWindows = startupBox.Checked;
            }
            using (var registry = Registry.CurrentUser.CreateSubKey(Program.ProductKey)) {
                registry.SetValue("Folder", desired);
                for (int i = 0; i < drafts.Length; i++) registry.SetValue(KeysForMode[i], drafts[i].Packed);
                registry.SetValue("Sound", audioBox.Checked ? 1 : 0);
                registry.SetValue("AutoSave", autoBox.Checked ? 1 : 0);
                registry.SetValue("HideOnClose", hideBox.Checked ? 1 : 0);
                registry.SetValue("Width", (int)widthBox.Value);
                registry.SetValue("Height", (int)heightBox.Value);
            }
            foreach (int id in registered)
                if (id != 0 && Array.IndexOf(candidate, id) < 0) UnregisterHotKey(window.Handle, id);
            Array.Copy(candidate, registered, registered.Length);
            Array.Copy(drafts, shortcuts, drafts.Length);
            folder = desired;
            sound = audioBox.Checked;
            autosave = autoBox.Checked;
            hideOnClose = hideBox.Checked;
            fixedWidth = (int)widthBox.Value;
            fixedHeight = (int)heightBox.Value;
            UpdateLabels();
            SetPage(false);
        } catch (Exception ex) {
            foreach (int id in fresh) if (id != 0) UnregisterHotKey(window.Handle, id);
            Error(ex.Message);
        }
    }
    int TryRegister(Shortcut shortcut) {
        int id = nextHotkeyId++;
        if (nextHotkeyId >= 0xBFFF) nextHotkeyId = 100;
        return RegisterHotKey(window.Handle, id, shortcut.Modifiers | 0x4000, (uint)shortcut.Key) ? id : 0;
    }
    async void Capture(CaptureMode mode) {
        if (busy || exitRequested || Array.Exists(shortcutFields, box => box.Focused)) return;
        busy = true;
        // A minimized window is not on screen, so it stays as it is and keeps its taskbar button.
        bool wasVisible = window.Visible && window.WindowState != FormWindowState.Minimized;
        bool menuWasVisible = menu.Visible;
        Point position = Cursor.Position;
        ScreenShot image = null;
        idleTimer.Stop();
        try {
            if (pendingSaves >= 2) {
                try { await saveQueue; }
                catch { /* QueueSave reports the write error on the UI thread. */ }
            }
            if (exitRequested || window.IsDisposed) return;
            if (wasVisible) window.Hide();
            if (menuWasVisible) menu.Close();
            if (wasVisible || menuWasVisible) {
                // Two composed frames: one for Windows to take the window off the desktop, one to be sure it was presented.
                await Task.Yield();
                DwmFlush();
                DwmFlush();
            }
            Rectangle bounds = SystemInformation.VirtualScreen;
            image = await Grab(mode, bounds, position);
            if (image == null) return;
            // The shutter is the only feedback that a capture happened; it plays once the picture has been taken.
            if (sound) {
                shutterCount++;
                shutterAccepted = Shutter.Play();
            }
            if (autosave) {
                QueueSave(image);
                image = null;
            } else {
                string suggestedName = await Task.Run(() => CaptureNames.Suggest(folder, filePrefix, filePattern));
                using (var dialog = new SaveFileDialog { Title = T("Save screenshot"), Filter = T("PNG image") + " (*.png)|*.png",
                    DefaultExt = "png", AddExtension = true, InitialDirectory = Directory.Exists(folder) ? folder : "",
                    FileName = suggestedName }) {
                    if (dialog.ShowDialog() != DialogResult.OK) return;
                    string path = dialog.FileName;
                    await Task.Run(() => SaveSelectedPng(image.Image, path));
                }
            }
        } catch (Exception ex) { Error(ex.Message); }
        finally {
            if (image != null) image.Dispose();
            busy = false;
            if (wasVisible && !window.IsDisposed) { window.Show(); window.Activate(); }
            RestWhenIdle();
        }
    }

    // Starts the one-shot idle timer; a capture or another call restarts the wait.
    internal void RestWhenIdle() {
        idleTimer.Stop();
        if (!exitRequested && !window.IsDisposed) idleTimer.Start();
    }

    // A tray app spends nearly all its time waiting, so the memory a capture touched goes back to Windows.
    static void ReleaseMemory() {
        GC.Collect();
        SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
    }
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);

    async void QueueSave(ScreenShot image) {
        string destination = folder;
        string prefix = filePrefix;
        FileNamePattern pattern = filePattern;
        DateTime time = DateTime.Now;
        pendingSaves++;
        Task queued = saveQueue.ContinueWith(previous => {
            using (image) SavePng(image.Image, destination, prefix, pattern, time);
        }, TaskScheduler.Default);
        saveQueue = queued;
        try { await queued; }
        catch (Exception ex) { Error(ex.Message); }
        finally {
            pendingSaves--;
            if (pendingSaves == 0) {
                saveQueue = Task.FromResult(0);
                if (exitRequested) ExitThread();
                else RestWhenIdle();
            }
        }
    }

    void RequestExit() {
        if (pendingSaves == 0) ExitThread();
        else exitRequested = true;
    }

    async Task<ScreenShot> Grab(CaptureMode mode, Rectangle bounds, Point position) {
        if (mode == CaptureMode.Fullscreen) return ScreenShot.Take(bounds);
        if (mode == CaptureMode.FixedSize) {
            Rectangle monitor = Screen.FromPoint(position).Bounds;
            int w = Math.Min(fixedWidth, monitor.Width), h = Math.Min(fixedHeight, monitor.Height);
            if (w != fixedWidth || h != fixedHeight)
                throw new Exception("The fixed size is larger than the screen under the pointer.");
            return ScreenShot.Take(new Rectangle(Math.Max(monitor.Left, Math.Min(monitor.Right - w, position.X - w / 2)),
                Math.Max(monitor.Top, Math.Min(monitor.Bottom - h, position.Y - h / 2)), w, h));
        }
        Rectangle area;
        using (ScreenShot screenshot = ScreenShot.Take(bounds))
        using (var picker = new AreaPicker(bounds, screenshot, mode)) {
            if (picker.ShowDialog() != DialogResult.OK) return null;
            area = picker.Selection;
            if (mode == CaptureMode.Rectangle) return new ScreenShot(screenshot.Crop(area));
            if (mode == CaptureMode.Freehand) return new ScreenShot(picker.MakeFreehand());
        }
        area.Offset(bounds.Location);
        Bitmap stitched = await ScrollCapture.Grab(area);
        return stitched == null ? null : new ScreenShot(stitched);
    }
    internal static void SaveSelectedPng(Bitmap image, string path) {
        string staged = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), Guid.NewGuid().ToString("N") + ".tmp");
        try {
            using (var file = new FileStream(staged, FileMode.CreateNew, FileAccess.Write))
                image.Save(file, ImageFormat.Png);
            if (File.Exists(path)) File.Replace(staged, path, null);
            else File.Move(staged, path);
        } finally {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }
    internal static string SavePng(Bitmap shot, string destination, string prefix = "CapKab", FileNamePattern pattern = FileNamePattern.DateTime, DateTime? captureTime = null) {
        Directory.CreateDirectory(destination);
        DateTime now = captureTime ?? DateTime.Now;
        long sequence = CaptureNames.NextNumber(destination, prefix, pattern, now);
        string stem = CaptureNames.Stem(prefix, pattern, now, sequence);
        for (int attempt = 0; ; attempt++) {
            string candidate = attempt == 0 ? stem : CaptureNames.UsesNumber(pattern)
                ? CaptureNames.Stem(prefix, pattern, now, sequence + attempt)
                : stem + "_" + (attempt + 1).ToString(CultureInfo.InvariantCulture);
            string path = Path.Combine(destination, candidate + ".png");
            FileStream file;
            try { file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) { if (File.Exists(path)) continue; throw; }
            try { using (file) shot.Save(file, ImageFormat.Png); }
            catch { File.Delete(path); throw; }
            return path;
        }
    }

    void Error(string text) { MessageBox.Show(T(text), Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    protected override void ExitThreadCore() {
        idleTimer.Dispose();
        foreach (int id in registered) if (id != 0) UnregisterHotKey(window.Handle, id);
        tray.Visible = false; tray.Dispose(); menu.Dispose();
        window.Dispose(); tooltips.Dispose(); icon.Dispose();
        base.ExitThreadCore();
    }
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

    sealed class ModeButton : Button {
        readonly CaptureMode mode;
        internal ModeButton(CaptureMode mode, string title) {
            this.mode = mode;
            Text = title;
            Dock = DockStyle.Fill;
            TextAlign = ContentAlignment.MiddleLeft;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            MinimumSize = new Size(0, 43);
            Padding = new Padding(47, 5, 12, 5);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderColor = Color.FromArgb(226, 226, 219);
            FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 244, 203);
            FlatAppearance.MouseDownBackColor = Accent;
            BackColor = Color.White;
            ForeColor = Ink;
        }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            GraphicsState state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = Math.Min(g.DpiX / 96f, Height / 37f);
            g.TranslateTransform(10 * scale, (Height - 32 * scale) / 2);
            g.ScaleTransform(scale, scale);
            using (var pen = new Pen(ForeColor, 1.65f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var soft = new SolidBrush(BackColor.GetBrightness() < .5f ? Color.FromArgb(89, 73, 39) : Color.FromArgb(255, 242, 194))) {
                switch (mode) {
                    case CaptureMode.Rectangle:
                        g.FillRectangle(soft, 5, 8, 22, 16);
                        pen.DashStyle = DashStyle.Dash;
                        g.DrawRectangle(pen, 5, 8, 22, 16);
                        break;
                    case CaptureMode.Fullscreen:
                        g.FillRectangle(soft, 4, 7, 24, 18);
                        g.DrawRectangle(pen, 4, 7, 24, 18);
                        g.DrawLine(pen, 12, 28, 20, 28);
                        g.DrawLine(pen, 16, 25, 16, 28);
                        break;
                    case CaptureMode.Scrolling:
                        g.FillRectangle(soft, 5, 4, 21, 23);
                        g.DrawRectangle(pen, 5, 4, 21, 23);
                        g.DrawLine(pen, 8, 9, 14, 9);
                        g.DrawLine(pen, 8, 13, 14, 13);
                        g.DrawLine(pen, 8, 17, 14, 17);
                        g.DrawLine(pen, 21, 9, 21, 23);
                        g.DrawLines(pen, new[] { new Point(18, 20), new Point(21, 23), new Point(24, 20) });
                        break;
                    case CaptureMode.FixedSize:
                        g.FillRectangle(soft, 6, 8, 20, 16);
                        g.DrawRectangle(pen, 6, 8, 20, 16);
                        g.DrawLine(pen, 10, 16, 22, 16);
                        g.DrawLines(pen, new[] { new Point(13, 13), new Point(10, 16), new Point(13, 19) });
                        g.DrawLines(pen, new[] { new Point(19, 13), new Point(22, 16), new Point(19, 19) });
                        break;
                    case CaptureMode.Freehand:
                        Point[] outline = { new Point(6, 16), new Point(9, 7), new Point(19, 7), new Point(26, 14),
                            new Point(23, 19), new Point(20, 25), new Point(10, 25) };
                        g.FillClosedCurve(soft, outline);
                        pen.DashStyle = DashStyle.Dash;
                        g.DrawClosedCurve(pen, outline);
                        break;
                }
            }
            g.Restore(state);
        }
    }
}
// A screen grab held in a GDI bitmap. Windows copies the pixels in once, the area picker paints them back
// with BitBlt, and Image exposes the same memory to GDI+ for cropping and PNG encoding without another copy.
sealed class ScreenShot : IDisposable {
    internal readonly Bitmap Image;
    internal readonly Size Size;
    readonly int stride;
    IntPtr dc, dib, previous, bits;

    // Wraps a picture that already owns its pixels (a crop, a freehand cut-out or a stitched scroll).
    internal ScreenShot(Bitmap image) {
        Image = image;
        Size = image.Size;
    }

    // An empty 24-bit surface that both GDI and GDI+ can draw on.
    internal ScreenShot(int width, int height) {
        if (width <= 0 || height <= 0 || (long)width * 3 + 3 > int.MaxValue) throw new Exception("Windows could not capture the screen.");
        stride = (width * 3 + 3) & ~3;
        if ((long)stride * height > int.MaxValue) throw new Exception("Windows could not capture the screen.");
        Size = new Size(width, height);
        var info = new BitmapHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 24 };
        try {
            dc = CreateCompatibleDC(IntPtr.Zero);
            if (dc != IntPtr.Zero) dib = CreateDIBSection(dc, ref info, 0, out bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) throw new Exception("Windows could not capture the screen.");
            previous = SelectObject(dc, dib);
            Image = new Bitmap(width, height, stride, PixelFormat.Format24bppRgb, bits);
        } catch { Release(); throw; }
    }

    internal static ScreenShot Take(Rectangle bounds) {
        var shot = new ScreenShot(bounds.Width, bounds.Height);
        IntPtr screen = GetDC(IntPtr.Zero);
        try {
            if (screen == IntPtr.Zero || !BitBlt(shot.dc, 0, 0, bounds.Width, bounds.Height, screen, bounds.X, bounds.Y, 0x00CC0020))
                throw new Exception("Windows could not capture the screen.");
            GdiFlush();
            return shot;
        } catch { shot.Dispose(); throw; }
        finally { if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen); }
    }

    internal void Paint(IntPtr target, Rectangle area) {
        BitBlt(target, area.X, area.Y, area.Width, area.Height, dc, area.X, area.Y, 0x00CC0020);
    }

    internal Bitmap Crop(Rectangle area) {
        if (area.Width <= 0 || area.Height <= 0 || !new Rectangle(Point.Empty, Size).Contains(area))
            throw new ArgumentOutOfRangeException("area");
        if (bits == IntPtr.Zero) return Image.Clone(area, PixelFormat.Format24bppRgb);
        var result = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
        try {
            BitmapData data = result.LockBits(new Rectangle(Point.Empty, result.Size), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try {
                var rowBytes = new UIntPtr((uint)area.Width * 3);
                for (int y = 0; y < area.Height; y++)
                    CopyMemory(IntPtr.Add(data.Scan0, y * data.Stride), IntPtr.Add(bits, (area.Y + y) * stride + area.X * 3), rowBytes);
            } finally { result.UnlockBits(data); }
            return result;
        } catch { result.Dispose(); throw; }
    }

    void Release() {
        if (dc != IntPtr.Zero) {
            if (previous != IntPtr.Zero) SelectObject(dc, previous);
            DeleteDC(dc);
        }
        if (dib != IntPtr.Zero) DeleteObject(dib);
        dc = dib = previous = bits = IntPtr.Zero;
    }

    public void Dispose() {
        // The GDI+ wrapper goes first because it points into the bitmap that Release frees.
        if (Image != null) Image.Dispose();
        Release();
        GC.SuppressFinalize(this);
    }

    ~ScreenShot() { Release(); }

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapHeader {
        internal int Size, Width, Height;
        internal short Planes, BitCount;
        internal int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ColorsUsed, ColorsImportant;
    }
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int operation);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] static extern void CopyMemory(IntPtr target, IntPtr source, UIntPtr size);
}

// The shutter click that confirms a capture. The WAV sits in unmanaged memory because Windows keeps
// reading it after an asynchronous PlaySound call has returned.
static class Shutter {
    static IntPtr wave;

    internal static bool Load() {
        if (wave != IntPtr.Zero) return true;
        using (Stream source = typeof(Shutter).Assembly.GetManifestResourceStream("shutter.wav")) {
            if (source == null || source.Length < 44 || source.Length > 1 << 20) return false;
            var data = new byte[source.Length];
            for (int read = 0; read < data.Length; ) {
                int count = source.Read(data, read, data.Length - read);
                if (count <= 0) return false;
                read += count;
            }
            if (BitConverter.ToInt32(data, 0) != 0x46464952 || BitConverter.ToInt32(data, 8) != 0x45564157 ||
                BitConverter.ToInt32(data, 4) != data.Length - 8) return false;
            wave = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, wave, data.Length);
            return true;
        }
    }

    // Asynchronous, from memory, and silent rather than the Windows default beep if playback is unavailable.
    internal static bool Play() {
        return Load() && PlaySound(wave, IntPtr.Zero, 0x0001 | 0x0002 | 0x0004);
    }
    [DllImport("winmm.dll")] static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
}

sealed class AreaPicker : Form {
    readonly ScreenShot screen;
    readonly CaptureMode mode;
    readonly List<Point> trace = new List<Point>();
    Point start, current;
    bool dragging;
    readonly Pen darkGuide = new Pen(Color.Black, 1);
    readonly Pen lightGuide = new Pen(Color.White, 1) { DashPattern = new float[] { 3, 3 } };
    internal Rectangle Selection { get; private set; }

    internal AreaPicker(Rectangle bounds, ScreenShot screenshot, CaptureMode mode) {
        screen = screenshot;
        this.mode = mode;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Bounds = bounds;
        Cursor = Cursors.Cross;
        KeyPreview = true;
    }
    Rectangle Area {
        get { return Rectangle.FromLTRB(Math.Min(start.X,current.X), Math.Min(start.Y,current.Y),
            Math.Max(start.X,current.X), Math.Max(start.Y,current.Y)); }
    }
    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        // Appear and disappear in a single frame: no fade over the desktop, and no fading outline in a scrolling capture.
        int noTransitions = 1;
        DwmSetWindowAttribute(Handle, 3, ref noTransitions, sizeof(int));
    }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnPaint(PaintEventArgs e) {
        Rectangle dirty = Rectangle.Intersect(e.ClipRectangle, new Rectangle(Point.Empty, screen.Size));
        if (dirty.IsEmpty) return;
        IntPtr surface = e.Graphics.GetHdc();
        try { screen.Paint(surface, dirty); }
        finally { e.Graphics.ReleaseHdc(surface); }
        if (!dragging) return;
        if (mode == CaptureMode.Freehand) {
            for (int i = 1; i < trace.Count; i++) {
                Point first = trace[i - 1], second = trace[i];
                if (!SegmentBounds(first, second).IntersectsWith(dirty)) continue;
                e.Graphics.DrawLine(darkGuide, first, second);
                e.Graphics.DrawLine(lightGuide, first, second);
            }
        } else {
            Rectangle area = Area;
            if (area.Width <= 0 || area.Height <= 0) return;
            e.Graphics.DrawRectangle(darkGuide, area);
            e.Graphics.DrawRectangle(lightGuide, area);
        }
    }

    static Rectangle SegmentBounds(Point first, Point second) {
        return Rectangle.FromLTRB(Math.Min(first.X, second.X) - 1, Math.Min(first.Y, second.Y) - 1,
            Math.Max(first.X, second.X) + 2, Math.Max(first.Y, second.Y) + 2);
    }

    void InvalidateOutline(Rectangle area) {
        if (area.Width <= 0 || area.Height <= 0) return;
        Invalidate(new Rectangle(area.Left - 1, area.Top - 1, area.Width + 3, 3));
        Invalidate(new Rectangle(area.Left - 1, area.Bottom - 1, area.Width + 3, 3));
        Invalidate(new Rectangle(area.Left - 1, area.Top - 1, 3, area.Height + 3));
        Invalidate(new Rectangle(area.Right - 1, area.Top - 1, 3, area.Height + 3));
    }

    Point Clamp(Point p) {
        return new Point(Math.Max(0, Math.Min(ClientSize.Width, p.X)), Math.Max(0, Math.Min(ClientSize.Height, p.Y)));
    }
    protected override void OnMouseDown(MouseEventArgs e) {
        if (e.Button == MouseButtons.Right) { DialogResult = DialogResult.Cancel; return; }
        if (e.Button != MouseButtons.Left) return;
        start = current = Clamp(e.Location);
        trace.Clear();
        trace.Add(start);
        dragging = true;
        Capture = true;
    }
    protected override void OnMouseMove(MouseEventArgs e) {
        if (!dragging) return;
        Point next = Clamp(e.Location);
        if (next == current) return;
        Rectangle previousArea = Area;
        Point previous = current;
        current = next;
        if (mode == CaptureMode.Freehand) {
            trace.Add(current);
            Invalidate(SegmentBounds(previous, current));
        } else {
            InvalidateOutline(previousArea);
            InvalidateOutline(Area);
        }
    }
    protected override void OnMouseUp(MouseEventArgs e) {
        if (!dragging || e.Button != MouseButtons.Left) return;
        current = Clamp(e.Location);
        Selection = Area;
        if (mode == CaptureMode.Freehand) {
            if (current != trace[trace.Count - 1]) trace.Add(current);
            if (trace.Count >= 3) {
                using (var path = new GraphicsPath()) {
                    path.AddPolygon(trace.ToArray());
                    Selection = Rectangle.Intersect(Rectangle.Ceiling(path.GetBounds()), ClientRectangle);
                }
            } else Selection = Rectangle.Empty;
        }
        dragging = false;
        Capture = false;
        if (Selection.Width > 0 && Selection.Height > 0) DialogResult = DialogResult.OK;
        else Invalidate();
    }
    internal Bitmap MakeFreehand() {
        var result = new Bitmap(Selection.Width, Selection.Height, PixelFormat.Format32bppArgb);
        try {
            // The brush copies its picture, so it is built from the selected part rather than the whole desktop.
            Point[] outline = trace.ToArray();
            for (int i = 0; i < outline.Length; i++) outline[i].Offset(-Selection.X, -Selection.Y);
            using (Bitmap selected = screen.Crop(Selection))
            using (var g = Graphics.FromImage(result))
            using (var path = new GraphicsPath())
            using (var image = new TextureBrush(selected, WrapMode.Clamp)) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                path.AddPolygon(outline);
                g.FillPath(image, path);
            }
            return result;
        } catch { result.Dispose(); throw; }
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) {
        if (keyData == Keys.Escape) { DialogResult = DialogResult.Cancel; return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void Dispose(bool disposing) {
        if (disposing) {
            darkGuide.Dispose();
            lightGuide.Dispose();
        }
        base.Dispose(disposing);
    }
}

static class ScrollCapture {
    const int Columns = 20;
    const int Channels = Columns * 3;
    const int MaxHeight = 16000;
    const int MaxPixels = 12000000;

    internal static async Task<Bitmap> Grab(Rectangle area) {
        if (area.Width < 80 || area.Height < 180)
            throw new Exception("Select a scrollable area at least 80 × 180 pixels.");
        Point previous = Cursor.Position;
        var target = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
        var strips = new List<Bitmap>();
        Cursor.Position = target;
        try {
            await Task.Yield();
            DwmFlush();
            DwmFlush();
            byte[] before;
            using (ScreenShot first = ScreenShot.Take(area)) {
                strips.Add(first.Crop(new Rectangle(Point.Empty, area.Size)));
                before = Sample(first.Image);
            }
            int height = area.Height;
            for (int i = 0; i < 120 && height < MaxHeight && (long)height * area.Width < MaxPixels; i++) {
                if (Cancelled(target)) return null;
                mouse_event(0x0800, 0, 0, -120, UIntPtr.Zero);
                await Task.Delay(380);
                if (Cancelled(target)) return null;
                using (ScreenShot next = ScreenShot.Take(area)) {
                    byte[] after = Sample(next.Image);
                    int offset = FindShift(before, after, area.Height);
                    if (offset == 0) {
                        if (height == area.Height)
                            throw new Exception("Nothing scrolled. Start above the bottom and select content in the active window.");
                        break;
                    }
                    if (offset < 0)
                        throw new Exception("The images could not be joined. Select a larger content area and exclude fixed headers or footers.");
                    if (height + offset > MaxHeight || (long)(height + offset) * area.Width > MaxPixels)
                        throw new Exception("The scrolling image reached its size limit. Capture the page in shorter sections.");
                    strips.Add(next.Crop(new Rectangle(0, area.Height - offset, area.Width, offset)));
                    height += offset;
                    before = after;
                }
                if (i == 119) throw new Exception("The scrolling image reached its length limit. Capture the page in shorter sections.");
            }
            if (height == area.Height || height >= MaxHeight || (long)height * area.Width >= MaxPixels)
                throw new Exception("The scrolling image reached its size limit. Capture a narrower area or a shorter section.");
            var result = new Bitmap(area.Width, height, PixelFormat.Format24bppRgb);
            try {
                using (var g = Graphics.FromImage(result)) {
                    int top = 0;
                    foreach (Bitmap strip in strips) {
                        g.DrawImageUnscaled(strip, 0, top);
                        top += strip.Height;
                    }
                }
                return result;
            } catch { result.Dispose(); throw; }
        } finally {
            if (Cursor.Position == target) Cursor.Position = previous;
            foreach (Bitmap strip in strips) strip.Dispose();
        }
    }
    static bool Cancelled(Point target) {
        return Cursor.Position != target || (GetAsyncKeyState((int)Keys.Escape) & 0x8000) != 0;
    }
    internal static byte[] Sample(Bitmap image) {
        BitmapData data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try {
            var result = new byte[image.Height * Channels];
            var row = new byte[image.Width * 3];
            for (int y = 0; y < image.Height; y++) {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                for (int x = 0; x < Columns; x++) {
                    int at = (x + 1) * image.Width / (Columns + 1) * 3;
                    Array.Copy(row, at, result, y * Channels + x * 3, 3);
                }
            }
            return result;
        } finally { image.UnlockBits(data); }
    }
    internal static int FindShift(byte[] before, byte[] after, int height) {
        if (Difference(before, after, height, 0) < .35) return 0;
        double best = double.MaxValue;
        int shift = -1;
        for (int delta = 1; delta <= height * 3 / 4; delta++) {
            double score = Difference(before, after, height, delta);
            if (score < best) { best = score; shift = delta; }
        }
        return best <= 4.0 ? shift : -1;
    }
    static double Difference(byte[] before, byte[] after, int height, int shift) {
        int margin = Math.Min(16, height / 14);
        int start = margin;
        int end = height - shift - margin;
        int stride = Math.Max(1, (end - start) / 82);
        long total = 0;
        int count = 0;
        for (int y = start; y < end; y += stride) {
            int old = (y + shift) * Channels;
            int newer = y * Channels;
            for (int x = 0; x < Channels; x++) total += Math.Abs(before[old + x] - after[newer + x]);
            count += Channels;
        }
        return (double)total / count;
    }
    [DllImport("dwmapi.dll")] static extern int DwmFlush();
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extra);
}















static class UiText {
    static readonly Dictionary<string, string> Thai = new Dictionary<string, string>(StringComparer.Ordinal) {
        { "CapKab Settings", "ตั้งค่า CapKab" },
        { "Capture modes", "โหมดแคปภาพ" },
        { "Click a mode to capture, or press its shortcut.", "คลิกโหมดที่ต้องการเพื่อแคป หรือกดคีย์ลัดที่แสดง" },
        { "Preferences", "ตัวเลือก" },
        { "File names", "ชื่อไฟล์" },
        { "Name prefix", "ชื่อขึ้นต้น" },
        { "Name your captures. Choose from 6 formats.",
            "ตั้งชื่อบันทึกภาพ เลือกได้ 6 แบบ" },
        { "$Y year · $M month · $D day · $H hour · $N minute · $S second · $MS millisecond",
            "$Y ปี · $M เดือน · $D วัน · $H ชั่วโมง · $N นาที · $S วินาที · $MS มิลลิวินาที" },
        { "%COMPUTERNAME% computer · %USERNAME% user",
            "%COMPUTERNAME% ชื่อคอมพิวเตอร์ · %USERNAME% ชื่อผู้ใช้" },
        { "Example", "ตัวอย่าง" },
        { "Date and time", "วันและเวลา" },
        { "Running number", "เลขลำดับ" },
        { "Date and number", "วันที่และเลขลำดับ" },
        { "Computer and time", "ชื่อเครื่องและเวลา" },
        { "User and time", "ชื่อผู้ใช้และเวลา" },
        { "Random code", "รหัสสุ่ม" },
        { "Remove characters that Windows does not allow in file names.", "ลบอักขระที่ Windows ไม่อนุญาตให้ใช้ในชื่อไฟล์" },
        { "Rectangle", "กรอบสี่เหลี่ยม" },
        { "Fullscreen", "เต็มหน้าจอ" },
        { "Scrolling window", "เลื่อนหน้าต่าง" },
        { "Fixed size", "ขนาดคงที่" },
        { "Freehand", "วาดอิสระ" },
        { "General", "ทั่วไป" },
        { "Keyboard shortcuts", "คีย์ลัด" },
        { "Save folder", "โฟลเดอร์บันทึก" },
        { "Open save folder", "เปิดโฟลเดอร์บันทึก" },
        { "Browse…", "เลือกโฟลเดอร์…" },
        { "Auto-save PNG after capture", "บันทึก PNG อัตโนมัติหลังแคป" },
        { "Play a quiet shutter sound", "เล่นเสียงชัตเตอร์เบา ๆ" },
        { "Run when Windows starts", "เปิดพร้อม Windows" },
        { "Auto-hide when clicking [X]", "ซ่อนลงถาดระบบเมื่อกด [X]" },
        { "Switch to English", "สลับเป็นภาษาอังกฤษ" },
        { "Switch to dark mode", "สลับเป็นธีมมืด" },
        { "Switch to light mode", "สลับเป็นธีมสว่าง" },
        { "Click a field and press the new shortcut.", "คลิกช่องแล้วกดคีย์ลัดใหม่" },
        { "Save", "บันทึก" },
        { "Settings", "ตั้งค่า" },
        { "Exit", "ออกจากโปรแกรม" },
        { "Choose where to save screenshots", "เลือกโฟลเดอร์สำหรับบันทึกภาพหน้าจอ" },
        { "Key unavailable", "ใช้คีย์ลัดไม่ได้" },
        { "Choose an absolute save folder.", "กรุณาเลือกโฟลเดอร์บันทึก" },
        { "Assign a different shortcut to each mode.", "ตั้งคีย์ลัดให้ต่างกันในแต่ละโหมด" },
        { "Windows could not register {0}. Choose another shortcut.", "Windows ใช้คีย์ลัด {0} ไม่ได้ กรุณาเลือกคีย์ลัดอื่น" },
        { "Save screenshot", "บันทึกภาพหน้าจอ" },
        { "PNG image", "ภาพ PNG" },
        { "The fixed size is larger than the screen under the pointer.", "ขนาดที่กำหนดใหญ่กว่าหน้าจอที่เมาส์ชี้อยู่" },
        { "Windows could not capture the screen.", "Windows แคปหน้าจอไม่ได้ในขณะนี้" },
        { "Drag over the scrollable content. Esc cancels.", "ลากครอบเนื้อหาที่เลื่อนได้ กด Esc เพื่อยกเลิก" },
        { "Hold and draw around the area. Esc cancels.", "กดค้างแล้ววาดรอบพื้นที่ กด Esc เพื่อยกเลิก" },
        { "Drag to select an area. Esc cancels.", "ลากเพื่อเลือกพื้นที่ กด Esc เพื่อยกเลิก" },
        { "Select a scrollable area at least 80 × 180 pixels.", "เลือกพื้นที่ที่เลื่อนได้อย่างน้อย 80 × 180 พิกเซล" },
        { "Nothing scrolled. Start above the bottom and select content in the active window.", "เลื่อนภาพไม่ได้ ให้เริ่มเหนือส่วนท้ายแล้วเลือกเนื้อหาในหน้าต่างที่กำลังใช้งาน" },
        { "The images could not be joined. Select a larger content area and exclude fixed headers or footers.", "ต่อภาพไม่ได้ ให้เลือกพื้นที่เนื้อหาที่ใหญ่ขึ้นโดยเว้นหัวและท้ายหน้าที่ติดอยู่กับจอ" },
        { "The scrolling image reached its size limit. Capture the page in shorter sections.", "ภาพถึงขนาดสูงสุดแล้ว ให้แบ่งแคปทีละช่วง" },
        { "The scrolling image reached its length limit. Capture the page in shorter sections.", "ภาพถึงความยาวสูงสุดแล้ว ให้แบ่งแคปทีละช่วง" },
        { "The scrolling image reached its size limit. Capture a narrower area or a shorter section.", "ภาพถึงขนาดสูงสุดแล้ว ให้แคปพื้นที่แคบลงหรือแบ่งเป็นช่วงสั้นลง" }
    };

    internal static bool HasTranslation(string text) { return Thai.ContainsKey(text); }
    internal static string Translate(string text, bool thai) {
        string translated;
        return thai && Thai.TryGetValue(text, out translated) ? translated : text;
    }
}





enum FileNamePattern { DateTime, RunningNumber, DateAndNumber, ComputerAndTime, UserAndTime, Random }

static class CaptureNames {
    internal const int MaxPrefixLength = 60;
    const string Date = "$Y-$M-$D";
    const string Timestamp = Date + "_$H-$N-$S-$MS";
    internal static readonly string[] Templates = { Timestamp, "NAME", Date + "_NAME",
        "%COMPUTERNAME%_" + Timestamp, "%USERNAME%_" + Timestamp, "RANDOM" };
    static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    internal static bool ValidPrefix(string prefix) {
        return prefix.Length <= MaxPrefixLength && prefix.IndexOfAny(Invalid) == -1;
    }

    internal static bool UsesNumber(FileNamePattern pattern) {
        return pattern == FileNamePattern.RunningNumber || pattern == FileNamePattern.DateAndNumber;
    }

    static string Head(string prefix) { return prefix.Length == 0 ? "" : prefix + "_"; }
    static string Time(DateTime now) { return now.ToString("yyyy-MM-dd_HH-mm-ss-fff", CultureInfo.InvariantCulture); }
    static string Day(DateTime now) { return now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }

    static string ValidPart(string value) {
        foreach (char forbidden in Invalid) value = value.Replace(forbidden, '_');
        return value.Length <= 48 ? value : value.Substring(0, 48);
    }

    static string NumberHead(string prefix, FileNamePattern pattern, DateTime now) {
        return Head(prefix) + (pattern == FileNamePattern.DateAndNumber ? Day(now) + "_" : "");
    }

    internal static string Stem(string prefix, FileNamePattern pattern, DateTime now, long number, bool example = false) {
        switch (pattern) {
            case FileNamePattern.RunningNumber:
            case FileNamePattern.DateAndNumber:
                return NumberHead(prefix, pattern, now) + number.ToString("D5", CultureInfo.InvariantCulture);
            case FileNamePattern.ComputerAndTime:
                return Head(prefix) + ValidPart(Environment.MachineName) + "_" + Time(now);
            case FileNamePattern.UserAndTime:
                return Head(prefix) + ValidPart(Environment.UserName) + "_" + Time(now);
            case FileNamePattern.Random:
                return Head(prefix) + (example ? (number == 1 ? "a7d24ce5b3" : "c9e631fa42") : Guid.NewGuid().ToString("N").Substring(0, 10));
            default:
                return Head(prefix) + Time(now);
        }
    }

    internal static long NextNumber(string folder, string prefix, FileNamePattern pattern, DateTime now) {
        if (!UsesNumber(pattern) || !Directory.Exists(folder)) return 1;
        string head = NumberHead(prefix, pattern, now);
        long largest = 0;
        foreach (string path in Directory.EnumerateFiles(folder, "*.png")) {
            string name = Path.GetFileNameWithoutExtension(path);
            if (!name.StartsWith(head, StringComparison.OrdinalIgnoreCase)) continue;
            string suffix = name.Substring(head.Length);
            long found;
            if (suffix.Length >= 5 && suffix.Length <= 16 &&
                long.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out found))
                largest = Math.Max(largest, found);
        }
        return largest + 1;
    }

    internal static string Suggest(string folder, string prefix, FileNamePattern pattern) {
        DateTime now = DateTime.Now;
        return Stem(prefix, pattern, now, NextNumber(folder, prefix, pattern, now)) + ".png";
    }
}

















