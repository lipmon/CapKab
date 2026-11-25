using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Win32;

static class UiTest {
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    // TestUi.ps1 points the settings at a disposable registry key; these helpers only ever touch that key.
    static bool SettingsWritable() {
        try { using (var key = Registry.CurrentUser.CreateSubKey(Program.ProductKey)) return key != null; }
        catch (Exception) { return false; }
    }
    static object SavedSetting(string name) {
        using (var key = Registry.CurrentUser.OpenSubKey(Program.ProductKey)) return key == null ? null : key.GetValue(name);
    }
    static void RemoveTestSettings() {
        if (!Program.ProductKey.EndsWith(".UiTest", StringComparison.Ordinal)) return;
        try { Registry.CurrentUser.DeleteSubKeyTree(Program.ProductKey, false); }
        catch (Exception) { }
    }
    static byte[] Pixels(Bitmap image, Rectangle area) {
        BitmapData data = image.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try {
            var result = new byte[area.Width * area.Height * 3];
            for (int y = 0; y < area.Height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), result, y * area.Width * 3, area.Width * 3);
            return result;
        } finally { image.UnlockBits(data); }
    }
    static async Task WaitForCapture(CaptureApp app) {
        DateTime limit = DateTime.UtcNow.AddSeconds(20);
        while ((bool)Field("busy").GetValue(app) || (int)Field("pendingSaves").GetValue(app) != 0) {
            if (DateTime.UtcNow > limit) throw new Exception("A capture did not finish.");
            await Task.Delay(20);
        }
    }
    static string OnlyPng(string folder, string complaint) {
        string[] files = Directory.GetFiles(folder, "*.png");
        Expect(files.Length == 1, complaint + " (found " + files.Length + " PNG files)");
        return files[0];
    }
    static void CropTest() {
        // An odd width leaves padding at the end of every pixel row, which a wrong row offset would expose.
        using (var shot = new ScreenShot(61, 37)) {
            for (int y = 0; y < 37; y++)
                for (int x = 0; x < 61; x++)
                    shot.Image.SetPixel(x, y, Color.FromArgb((x * 4) & 255, (y * 6) & 255, (x * 7 + y * 13) & 255));
            var area = new Rectangle(7, 5, 41, 23);
            using (Bitmap crop = shot.Crop(area)) {
                Expect(crop.Size == area.Size && crop.PixelFormat == PixelFormat.Format24bppRgb, "Crop has the wrong size or format.");
                for (int y = 0; y < area.Height; y++)
                    for (int x = 0; x < area.Width; x++)
                        Expect(crop.GetPixel(x, y).ToArgb() == Color.FromArgb(((x + 7) * 4) & 255, ((y + 5) * 6) & 255, ((x + 7) * 7 + (y + 5) * 13) & 255).ToArgb(),
                            "Crop copied the wrong pixel at " + x + "," + y);
            }
            using (Bitmap whole = shot.Crop(new Rectangle(Point.Empty, shot.Size)))
                Expect(whole.GetPixel(60, 36).ToArgb() == shot.Image.GetPixel(60, 36).ToArgb(), "Whole-image crop lost its last pixel.");
            bool rejected = false;
            try { shot.Crop(new Rectangle(50, 30, 20, 20)).Dispose(); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Expect(rejected, "A crop outside the screenshot was accepted.");
            using (var painted = new Bitmap(61, 37, PixelFormat.Format24bppRgb)) {
                using (var graphics = Graphics.FromImage(painted)) {
                    IntPtr surface = graphics.GetHdc();
                    try { shot.Paint(surface, new Rectangle(0, 0, 61, 37)); }
                    finally { graphics.ReleaseHdc(surface); }
                }
                Expect(painted.GetPixel(0, 0).ToArgb() == shot.Image.GetPixel(0, 0).ToArgb() &&
                    painted.GetPixel(60, 36).ToArgb() == shot.Image.GetPixel(60, 36).ToArgb(), "Painting the screenshot changed its pixels.");
            }
        }
    }

    // Runs after the capture modes: the shutter that confirms a capture, the option that silences it,
    // and what happens to the settings window around a capture.
    static async Task ShutterAndWindowChecks(CaptureApp app, Form window, string folder, bool settingsWritable) {
        var audioBox = (CheckBox)Field("audioBox").GetValue(app);
        Func<int> shutters = () => (int)Field("shutterCount").GetValue(app);
        Func<bool> soundOn = () => (bool)Field("sound").GetValue(app);

        // Capturing while the settings window is open: it must step aside, stay out of the picture and come back.
        app.ShowSettings();
        // A test process may not take the foreground, so keep the window above whatever else is on screen while measuring.
        window.TopMost = true;
        await Task.Delay(500);
        Rectangle inner = Rectangle.Inflate(window.Bounds, -16, -16);
        Rectangle desktop = SystemInformation.VirtualScreen;
        byte[] shown, hidden, captured;
        using (ScreenShot withWindow = ScreenShot.Take(inner)) shown = Pixels(withWindow.Image, new Rectangle(Point.Empty, inner.Size));
        int before = shutters();
        Invoke(app, "Capture", CaptureMode.Fullscreen);
        await WaitForCapture(app);
        Expect(shutters() == before + 1, "Capturing from the settings window did not play the shutter.");
        Expect(window.Visible, "The settings window did not come back after the capture.");
        string png = OnlyPng(folder, "Capturing from the settings window should save one PNG");
        using (var saved = new Bitmap(png))
            captured = Pixels(saved, new Rectangle(inner.X - desktop.X, inner.Y - desktop.Y, inner.Width, inner.Height));
        File.Delete(png);
        window.Hide();
        await Task.Delay(400);
        using (ScreenShot withoutWindow = ScreenShot.Take(inner)) hidden = Pixels(withoutWindow.Image, new Rectangle(Point.Empty, inner.Size));
        window.Show();
        window.TopMost = false;
        double match = 0, contrast = 0;
        for (int i = 0; i < captured.Length; i++) {
            double windowPart = shown[i] - hidden[i];
            match += (captured[i] - hidden[i]) * windowPart;
            contrast += windowPart * windowPart;
        }
        // 0 means the desktop behind the window was captured, 1 means the window itself was.
        double visibility = contrast == 0 ? 0 : match / contrast;
        if (Math.Sqrt(contrast / captured.Length) < 8) Console.WriteLine("Note: the desktop behind the settings window looks like the window, so its absence from the PNG could not be measured.");
        else Expect(visibility < 0.2, "The settings window was captured in the screenshot (visibility " + visibility.ToString("F2") + ").");

        // Cancelling the area picker is not a capture: no file and no shutter.
        before = shutters();
        Invoke(app, "Capture", CaptureMode.Rectangle);
        DateTime limit = DateTime.UtcNow.AddSeconds(10);
        bool cancelled = false;
        while (!cancelled) {
            Expect(DateTime.UtcNow < limit, "The area picker did not open.");
            await Task.Delay(50);
            foreach (Form form in Application.OpenForms) {
                if (!(form is AreaPicker)) continue;
                PostMessage(form.Handle, 0x204, new IntPtr(2), Coord(30, 30));
                cancelled = true;
                break;
            }
        }
        await WaitForCapture(app);
        Expect(shutters() == before && Directory.GetFiles(folder, "*.png").Length == 0, "A cancelled selection saved a file or played the shutter.");
        Expect(window.Visible, "The settings window did not come back after a cancelled selection.");

        if (settingsWritable) {
            // Silence the shutter the way a user does: Preferences, untick, Save.
            Invoke(app, "OpenPreferences");
            Expect(audioBox.Checked, "Preferences did not show the shutter as on.");
            audioBox.Checked = false;
            Invoke(app, "Apply");
            Expect(!soundOn() && Equals(SavedSetting("Sound"), 0), "Saving Preferences did not turn the shutter off.");
            before = shutters();
            Invoke(app, "Capture", CaptureMode.Fullscreen);
            await WaitForCapture(app);
            Expect(shutters() == before, "The shutter played although it is switched off.");
            File.Delete(OnlyPng(folder, "A silent capture should still save one PNG"));

            // Ticking the box without Save changes nothing, and Preferences show the saved state again.
            Invoke(app, "OpenPreferences");
            audioBox.Checked = true;
            Invoke(app, "SetPage", false);
            Expect(!soundOn(), "The shutter option took effect without Save.");
            Invoke(app, "OpenPreferences");
            Expect(!audioBox.Checked, "Preferences did not return to the saved shutter setting.");
            Invoke(app, "SetPage", false);

            // The choice survives a restart.
            var restarted = new CaptureApp();
            try { Expect(!(bool)Field("sound").GetValue(restarted), "The shutter came back on after a restart."); }
            finally { restarted.ExitThread(); }

            // And it can be switched back on.
            Invoke(app, "OpenPreferences");
            audioBox.Checked = true;
            Invoke(app, "Apply");
            Expect(soundOn() && Equals(SavedSetting("Sound"), 1), "Saving Preferences did not turn the shutter back on.");
            before = shutters();
            Invoke(app, "Capture", CaptureMode.Fullscreen);
            await WaitForCapture(app);
            Expect(shutters() == before + 1, "The shutter stayed silent after it was switched back on.");
            File.Delete(OnlyPng(folder, "A capture after switching the shutter on should save one PNG"));
        } else {
            Console.WriteLine("Note: the registry is read-only here, so saving the shutter option through Preferences was not exercised.");
            Field("sound").SetValue(app, false);
            before = shutters();
            Invoke(app, "Capture", CaptureMode.Fullscreen);
            await WaitForCapture(app);
            Expect(shutters() == before, "The shutter played although it is switched off.");
            File.Delete(OnlyPng(folder, "A silent capture should still save one PNG"));
            Field("sound").SetValue(app, true);
        }
        Console.WriteLine("Windows accepted the shutter for playback: " + (bool)Field("shutterAccepted").GetValue(app));

        // A minimized settings window keeps its taskbar button through a capture.
        window.WindowState = FormWindowState.Minimized;
        await Task.Delay(300);
        Invoke(app, "Capture", CaptureMode.Fullscreen);
        await WaitForCapture(app);
        Expect(window.Visible && window.WindowState == FormWindowState.Minimized, "A capture made the minimized settings window disappear.");
        File.Delete(OnlyPng(folder, "A capture with the window minimized should save one PNG"));
        window.WindowState = FormWindowState.Normal;
    }
    static FieldInfo Field(string name) { return typeof(CaptureApp).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic); }
    static void Invoke(CaptureApp app, string name, params object[] args) {
        typeof(CaptureApp).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(app, args);
    }
    static void Expect(bool condition, string complaint) { if (!condition) throw new Exception(complaint); }
    static IntPtr Coord(int x, int y) { return new IntPtr((y << 16) | x); }
    static void Preview(Form window, string name) {
        using (var image = new Bitmap(window.Width, window.Height)) {
            window.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            image.Save(Path.Combine("work", name + ".png"));
        }
    }
    // The language toggle is a square button labelled with the language it switches to.
    static void CheckLanguageLabel(Button button, string expected, string preview) {
        Expect(button.Width == button.Height && button.Text == expected, "Language toggle should be a square button labelled " + expected + ", not \"" + button.Text + "\".");
        Size needed = TextRenderer.MeasureText(button.Text, button.Font);
        Expect(needed.Width <= button.ClientSize.Width && needed.Height <= button.ClientSize.Height,
            "The " + expected + " label does not fit its button: " + needed + " in " + button.ClientSize);
        using (var image = new Bitmap(button.Width, button.Height)) {
            button.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            // The label must actually be drawn, clear of the button's border on every side.
            int background = image.GetPixel(3, 3).ToArgb(), left = image.Width, right = -1, top = image.Height, bottom = -1;
            for (int y = 2; y < image.Height - 2; y++)
                for (int x = 2; x < image.Width - 2; x++) {
                    if (image.GetPixel(x, y).ToArgb() == background) continue;
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                }
            Expect(right >= left && bottom >= top, "The " + expected + " label was not drawn.");
            Expect(left > 2 && top > 2 && right < image.Width - 3 && bottom < image.Height - 3,
                "The " + expected + " label touches the edge of its button.");
            image.Save(Path.Combine("work", preview + ".png"));
        }
    }
    static void StitchTest() {
        const int w = 227, h = 302, shift = 47;
        using (var content = new Bitmap(w, h + shift, PixelFormat.Format24bppRgb)) {
            var rng = new Random(77);
            for (int y = 0; y < content.Height; y++)
                for (int x = 0; x < content.Width; x++)
                    content.SetPixel(x, y, Color.FromArgb(rng.Next(256), rng.Next(256), rng.Next(256)));
            using (var before = content.Clone(new Rectangle(0, 0, w, h), PixelFormat.Format24bppRgb))
            using (var after = content.Clone(new Rectangle(0, shift, w, h), PixelFormat.Format24bppRgb)) {
                byte[] oldRows = ScrollCapture.Sample(before);
                byte[] newRows = ScrollCapture.Sample(after);
                Expect(ScrollCapture.FindShift(oldRows, newRows, h) == shift, "Scroll shift mismatch.");
                Expect(ScrollCapture.FindShift(newRows, newRows, h) == 0, "Scroll-end recognition failed.");
                byte[] unrelated = new byte[newRows.Length];
                rng.NextBytes(unrelated);
                Expect(ScrollCapture.FindShift(oldRows, unrelated, h) < 0, "Unrelated scroll strips were accepted.");
            }
        }
    }
    [STAThread] static void Main() {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try {
            Expect(Program.ProductKey != @"Software\CapKab", "Build this test with work\\TestUi.ps1 so that it cannot touch the real CapKab settings.");
            // Same DPI mode as the real program, so the layout checks run at this display's scaling.
            try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { SetProcessDPIAware(); }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            RemoveTestSettings();
            bool settingsWritable = SettingsWritable();
            StitchTest();
            CropTest();
            var defaults = (Shortcut[])typeof(CaptureApp).GetField("DefaultShortcuts", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Expect(defaults[0].Key == Keys.D4 && defaults[0].Modifiers == 1 && defaults[0].ToString() == "Alt + 4", "Rectangle default shortcut mismatch.");
            Expect(defaults[1].ToString() == "Ctrl + Alt + F10", "The fullscreen shortcut changed.");
            foreach (CaptureMode mode in new[] { CaptureMode.Rectangle, CaptureMode.Scrolling, CaptureMode.Freehand }) {
                using (var original = new ScreenShot(500, 300))
                using (var rendered = new Bitmap(original.Size.Width, original.Size.Height))
                using (var overlay = new AreaPicker(new Rectangle(Point.Empty, original.Size), original, mode)) {
                    Color background = Color.FromArgb(220, 185, 143);
                    using (var graphics = Graphics.FromImage(original.Image)) graphics.Clear(background);
                    overlay.DrawToBitmap(rendered, new Rectangle(Point.Empty, rendered.Size));
                    Expect(rendered.GetPixel(10, 10).ToArgb() == background.ToArgb(), "Capture overlay darkened the screen.");
                    var binding = BindingFlags.Instance | BindingFlags.NonPublic;
                    typeof(AreaPicker).GetMethod("OnMouseDown", binding).Invoke(overlay,
                        new object[] { new MouseEventArgs(MouseButtons.Left, 1, 20, 20, 0) });
                    typeof(AreaPicker).GetMethod("OnMouseMove", binding).Invoke(overlay,
                        new object[] { new MouseEventArgs(MouseButtons.Left, 0, 100, 70, 0) });
                    overlay.DrawToBitmap(rendered, new Rectangle(Point.Empty, rendered.Size));
                    Expect(rendered.GetPixel(20, 20).ToArgb() != background.ToArgb(),
                        "Dragging needs a visible selection boundary.");
                    Expect(rendered.GetPixel(10, 10).ToArgb() == background.ToArgb() &&
                        rendered.GetPixel(40, 60).ToArgb() == background.ToArgb() &&
                        rendered.GetPixel(150, 100).ToArgb() == background.ToArgb(),
                        "Selection shading or effects changed pixels away from the boundary.");
                    if (mode == CaptureMode.Freehand)
                        typeof(AreaPicker).GetMethod("OnMouseMove", binding).Invoke(overlay,
                            new object[] { new MouseEventArgs(MouseButtons.Left, 0, 20, 100, 0) });
                    typeof(AreaPicker).GetMethod("OnMouseUp", binding).Invoke(overlay,
                        new object[] { new MouseEventArgs(MouseButtons.Left, 1,
                            mode == CaptureMode.Freehand ? 20 : 100, mode == CaptureMode.Freehand ? 20 : 70, 0) });
                    using (var selectedImage = mode == CaptureMode.Freehand ? overlay.MakeFreehand() :
                        original.Crop(overlay.Selection))
                    using (var png = new MemoryStream()) {
                        selectedImage.Save(png, ImageFormat.Png);
                        png.Position = 0;
                        using (var savedImage = new Bitmap(png)) {
                            int opaquePixels = 0;
                            for (int y = 0; y < savedImage.Height; y++)
                                for (int x = 0; x < savedImage.Width; x++) {
                                    Color color = savedImage.GetPixel(x, y);
                                    if (color.A != 255) continue;
                                    opaquePixels++;
                                    Expect(color.ToArgb() == background.ToArgb(), "Saved PNG contains selection guides.");
                                }
                            Expect(opaquePixels > 100, "Selection output is empty.");
                        }
                    }
                }
            }
            using (var app = new CaptureApp())
            using (var timer = new Timer { Interval = 100 }) {
                Field("folder").SetValue(app, folder);
                Field("autosave").SetValue(app, true);
                Expect((bool)Field("sound").GetValue(app), "The shutter sound should be on until the user switches it off.");
                Field("fixedWidth").SetValue(app, 210);
                Field("fixedHeight").SetValue(app, 160);
                Form window = (Form)Field("window").GetValue(app);
                app.ShowSettings();
                Expect(window.Text.Contains("CapKab"), "Settings title has outdated branding.");
                var labels = (Label[])Field("hotkeyLabels").GetValue(app);
                var shortcuts = (Shortcut[])Field("shortcuts").GetValue(app);
                for (int i = 0; i < labels.Length; i++) labels[i].Text = shortcuts[i].ToString().Replace(" + ", "+");
                var folderLink = (Button)Field("folderLink").GetValue(app);
                var footer = ((TableLayoutPanel)window.Controls[0]).GetControlFromPosition(0, 2);
                int footerTop = footer.PointToScreen(Point.Empty).Y;
                int folderBottom = folderLink.PointToScreen(new Point(0, folderLink.Height)).Y;
                Expect(footerTop - folderBottom >= 12, "Need visible spacing between the folder button and footer.");
                Preview(window, "capture-preview");
                var namesTab = (Button)Field("namesTab").GetValue(app);
                namesTab.PerformClick();
                Expect(((Panel)Field("fileNames").GetValue(app)).Visible, "File names tab did not open.");
                var prefixBox = (TextBox)Field("prefixBox").GetValue(app);
                var namePatterns = (RadioButton[])Field("namePatternButtons").GetValue(app);
                var nameExample = (Label)Field("nameExample").GetValue(app);
                Expect(namePatterns.Length == 6 && prefixBox.Text == "CapKab", "Naming defaults missing.");
                Expect(namePatterns[(int)FileNamePattern.RunningNumber].Text == "NAME" &&
                    namePatterns[(int)FileNamePattern.DateAndNumber].Text == "$Y-$M-$D_NAME" &&
                    namePatterns[(int)FileNamePattern.Random].Text == "RANDOM", "Filename option labels are incorrect.");
                prefixBox.Text = "Project";
                namePatterns[(int)FileNamePattern.RunningNumber].Checked = true;
                Expect(nameExample.Text == "Project_00001.png", "Live example did not reflect selection.");
                var namePage = (Panel)Field("fileNames").GetValue(app);
                var namesForm = (TableLayoutPanel)namePage.Controls[0];
                var actions = namesForm.GetControlFromPosition(0, namesForm.RowCount - 1);
                Expect(actions.Controls.Count == 1 && actions.Controls[0] is Button &&
                    actions.Controls[0].Text == "Save" && actions.Controls[0].Enabled, "Expected an enabled Save button as the only action.");
                prefixBox.Text = "งาน";
                Expect(nameExample.Text == "งาน_00001.png", "Thai prefix did not update the example.");
                Preview(window, "names-preview");
                Font originalNamesFont = window.Font;
                using (var larger = new Font(originalNamesFont.FontFamily, 12f)) {
                    window.Font = larger;
                    Invoke(app, "ApplyLanguage", true);
                    window.PerformLayout();
                    Expect(nameExample.Bottom <= namesForm.Height && namesForm.Bottom <= namePage.ClientSize.Height, "Example is clipped.");
                    foreach (var option in namePatterns) {
                        Size needed = TextRenderer.MeasureText(option.Text, option.Font);
                        Expect(option.ClientSize.Width >= needed.Width + 12, "Template clipped: " + option.Text + " " + option.ClientSize.Width + " / " + needed.Width);
                    }
                    window.Font = originalNamesFont;
                }
                Invoke(app, "ApplyLanguage", false);
                prefixBox.Text = "Project";
                Invoke(app, "ApplyLanguage", true);
                Expect(namesTab.Text == "ชื่อไฟล์" && namePatterns[1].Text == "NAME" && namePatterns[1].Checked && !namePatterns[0].Checked, "Thai file name options missing.");
                Expect(nameExample.Text == "Project_00001.png", "Language switch changed the file pattern.");
                prefixBox.Text = "invalid/name";
                Expect(!CaptureNames.ValidPrefix(prefixBox.Text), "Invalid Windows prefix was accepted.");
                Invoke(app, "ApplyLanguage", false);
                Invoke(app, "SetPage", false);
                namesTab.PerformClick();
                Expect(prefixBox.Text == "CapKab" && namePatterns[0].Checked, "Unsaved naming settings did not reset.");
                Invoke(app, "SetPage", false);
                string nameDirectory = Path.Combine(folder, "naming-test");
                using (var sample = new Bitmap(16, 16)) {
                    string first = CaptureApp.SavePng(sample, nameDirectory, "งาน", FileNamePattern.RunningNumber);
                    string second = CaptureApp.SavePng(sample, nameDirectory, "งาน", FileNamePattern.RunningNumber);
                    Expect(Path.GetFileName(first) == "งาน_00001.png" && Path.GetFileName(second) == "งาน_00002.png",
                        "Sequential naming should preserve Thai and increment without overwriting.");
                    string daily = CaptureApp.SavePng(sample, nameDirectory, "งาน", FileNamePattern.DateAndNumber);
                    Expect(Path.GetFileName(daily).EndsWith("_00001.png"), "Daily sequence did not start separately.");
                    Expect(CaptureNames.Suggest(nameDirectory, "งาน", FileNamePattern.RunningNumber) == "งาน_00003.png",
                        "Manual save suggestion did not match automatic sequence.");
                    string manualPath = Path.Combine(nameDirectory, "manual.png");
                    CaptureApp.SaveSelectedPng(sample, manualPath);
                    using (var replacement = new Bitmap(9, 7)) {
                        using (var graphics = Graphics.FromImage(replacement)) graphics.Clear(Color.Blue);
                        CaptureApp.SaveSelectedPng(replacement, manualPath);
                    }
                    using (var saved = new Bitmap(manualPath))
                        Expect(saved.Size == new Size(9, 7) && saved.GetPixel(0, 0).ToArgb() == Color.Blue.ToArgb(),
                            "Manual overwrite did not replace the previous PNG completely.");
                    var disposed = new Bitmap(2, 2);
                    disposed.Dispose();
                    bool rejected = false;
                    try { CaptureApp.SaveSelectedPng(disposed, manualPath); }
                    catch (Exception) { rejected = true; }
                    Expect(rejected && Directory.GetFiles(nameDirectory, "*.tmp").Length == 0,
                        "Failed manual save must clean up its staging file.");
                    using (var saved = new Bitmap(manualPath))
                        Expect(saved.Size == new Size(9, 7) && saved.GetPixel(0, 0).ToArgb() == Color.Blue.ToArgb(),
                            "Failed manual save damaged the previous PNG.");
                }
                Invoke(app, "OpenPreferences");
                Expect(((CheckBox)Field("startupBox").GetValue(app)).Visible && ((CheckBox)Field("hideBox").GetValue(app)).Visible, "Settings options missing.");
                Preview(window, "settings-preview");
                var theme = (Button)Field("themeButton").GetValue(app);
                Expect(theme.Visible && theme.Width == theme.Height && theme.AccessibleName == "Switch to dark mode", "Square moon button missing.");
                theme.PerformClick();
                Expect(window.BackColor.GetBrightness() < .2f && theme.ForeColor.GetBrightness() > .8f && theme.AccessibleName == "Switch to light mode", "Dark theme did not apply.");
                Preview(window, "dark-settings-preview");
                Font original = window.Font;
                using (var large = new Font(original.FontFamily, 12f)) {
                    window.Font = large;
                    window.PerformLayout();
                    var prefs = (Panel)Field("preferences").GetValue(app);
                    Expect(!prefs.AutoScroll && !prefs.VerticalScroll.Visible, "Preferences should not have a scrollbar.");
                    var language = (Button)Field("languageButton").GetValue(app);
                    Expect(language.AccessibleName.Contains("Switch to Thai") && language.Size == theme.Size && language.Right <= theme.Left, "Language button is not aligned beside theme.");
                    CheckLanguageLabel(language, "TH", "language-th-preview");
                    language.PerformClick();
                    Expect(language.AccessibleName == "สลับเป็นภาษาอังกฤษ", "Thai UI must offer EN.");
                    CheckLanguageLabel(language, "EN", "language-en-preview");
                    var preferencesTab = (Button)Field("preferencesTab").GetValue(app);
                    Expect(preferencesTab.Text == "ตัวเลือก" && theme.AccessibleName == "สลับเป็นธีมสว่าง", "Thai controls did not translate.");
                    Expect(((Button)Field("folderLink").GetValue(app)).AccessibleName == "เปิดโฟลเดอร์บันทึก", "Folder button accessibility name did not translate.");
                    Expect(prefs.Controls[0].Bottom <= prefs.ClientSize.Height, "Preferences do not fit with larger Thai text: " + prefs.Controls[0].Bottom + " / " + prefs.ClientSize.Height);
                    window.Font = original;
                    Preview(window, "thai-preferences-preview");
                    language.PerformClick();
                    Expect(language.AccessibleName.Contains("Switch to Thai"), "English UI must offer TH.");
                    Expect(preferencesTab.Text == "Preferences" && theme.AccessibleName == "Switch to light mode", "English controls did not restore.");
                    window.Font = large;
                    foreach (TextBox keyField in (TextBox[])Field("shortcutFields").GetValue(app))
                        Expect(keyField.Height >= keyField.PreferredHeight, "A shortcut field clips larger text.");
                    Invoke(app, "SetPage", false);
                    var capture = (Panel)Field("captureModes").GetValue(app);
                    int rows = 0;
                    foreach (Control control in capture.Controls[0].Controls) {
                        if (control.GetType().Name != "ModeButton") continue;
                        rows++;
                        var room = control.ClientSize - control.Padding.Size;
                        Size textSize = TextRenderer.MeasureText(control.Text, control.Font);
                        Expect(room.Width >= textSize.Width && room.Height >= textSize.Height, "A capture row clips larger text.");
                    }
                    Expect(rows == 5, "Expected five capture rows in Settings.");
                    window.Font = original;
                }

                Invoke(app, "SetPage", false);
                Preview(window, "dark-capture-preview");
                Invoke(app, "ApplyLanguage", true);
                Preview(window, "thai-home-preview");
                Invoke(app, "ApplyLanguage", false);
                Invoke(app, "OpenPreferences");
                Expect(theme.AccessibleName == "Switch to light mode", "Theme changed while navigating tabs.");
                theme.PerformClick();
                Expect(window.BackColor.GetBrightness() > .8f, "Light theme did not apply.");
                Field("hideOnClose").SetValue(app, true);
                window.Close();
                Expect(!window.Visible && !window.IsDisposed, "Closing did not hide to tray.");
                Expect(window.BackColor.GetBrightness() > .8f, "Theme reset when the window was hidden.");
                int step = 0;
                DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                var modes = new[] { CaptureMode.Fullscreen, CaptureMode.FixedSize, CaptureMode.Rectangle, CaptureMode.Freehand };
                var sizes = new[] { SystemInformation.VirtualScreen.Size, new Size(210, 160), new Size(80, 50), new Size(80, 60) };
                bool dispatched = false, selected = false, burst = false;
                timer.Tick += (s,e) => {
                    try {
                        if (DateTime.UtcNow > deadline) throw new Exception("Interaction timed out at step " + step);
                        if (step == modes.Length) {
                            if (!burst) {
                                burst = true;
                                Invoke(app, "Capture", CaptureMode.Fullscreen);
                                Invoke(app, "Capture", CaptureMode.Fullscreen);
                                Expect((int)Field("pendingSaves").GetValue(app) == 2, "Two rapid captures did not queue.");
                                Invoke(app, "Capture", CaptureMode.Fullscreen);
                                Expect((bool)Field("busy").GetValue(app), "Third rapid capture was dropped instead of waiting for the save queue.");
                                return;
                            }
                            if ((bool)Field("busy").GetValue(app) || (int)Field("pendingSaves").GetValue(app) != 0) return;
                            Expect(Directory.GetFiles(folder, "*.png").Length == 3, "Three rapid captures did not all save.");
                            Expect((int)Field("shutterCount").GetValue(app) == modes.Length + 3, "Each of three rapid captures should play the shutter.");
                            foreach (string rapid in Directory.GetFiles(folder, "*.png")) File.Delete(rapid);
                            timer.Stop();
                            ShutterAndWindowChecks(app, window, folder, settingsWritable).ContinueWith(checks => {
                                if (checks.IsFaulted) {
                                    Console.WriteLine(checks.Exception.InnerException);
                                    Environment.ExitCode = 1;
                                    app.ExitThread();
                                    return;
                                }
                                Console.WriteLine("Four capture modes, three rapid saves, shutter on every capture and silent when switched off, clean capture from the open settings window, cancelled selection, minimized window, crop, safe manual overwrite, EN/TH language button, naming examples, scroll stitching, settings layout, and hide on close passed.");
                                app.ShowSettings();
                                Field("hideOnClose").SetValue(app, false);
                                window.Close();
                            }, TaskScheduler.FromCurrentSynchronizationContext());
                            return;
                        }
                        if (!dispatched) { dispatched = true; Invoke(app, "Capture", modes[step]); return; }
                        if (!selected && step >= 2) {
                            foreach (Form form in Application.OpenForms) {
                                if (!(form is AreaPicker)) continue;
                                PostMessage(form.Handle, 0x201, new IntPtr(1), Coord(20,20));
                                if (step == 3) {
                                    PostMessage(form.Handle, 0x200, new IntPtr(1), Coord(100,20));
                                    PostMessage(form.Handle, 0x200, new IntPtr(1), Coord(60,80));
                                    PostMessage(form.Handle, 0x202, IntPtr.Zero, Coord(20,20));
                                } else PostMessage(form.Handle, 0x202, IntPtr.Zero, Coord(100,70));
                                selected = true;
                                break;
                            }
                        }
                        if ((bool)Field("busy").GetValue(app) || (int)Field("pendingSaves").GetValue(app) != 0) return;
                        string[] files = Directory.GetFiles(folder, "*.png");
                        Expect(files.Length == 1, "Expected one automatic PNG for " + modes[step]);
                        using (var actual = new Bitmap(files[0])) {
                            Expect(actual.Size == sizes[step], "Incorrect PNG dimensions for " + modes[step]);
                            if (step == 3)
                                Expect(actual.GetPixel(0,59).A == 0 && actual.GetPixel(40,20).A == 255, "Freehand alpha mask incorrect.");
                        }
                        File.Delete(files[0]);
                        Expect((int)Field("shutterCount").GetValue(app) == step + 1, "No shutter sound after the capture in mode " + modes[step]);
                        step++;
                        dispatched = selected = false;
                    } catch (Exception ex) {
                        Console.WriteLine(ex);
                        Environment.ExitCode = 1;
                        timer.Stop();
                        app.ExitThread();
                    }
                };
                timer.Start();
                Application.Run(app);
                Expect(window.IsDisposed, "App did not close when auto-hide was disabled.");
            }
        } catch (Exception ex) { Console.WriteLine(ex); Environment.ExitCode = 1; }
        finally {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            RemoveTestSettings();
        }
    }
}
























