using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Setup {
    const string Product = ProductInfo.Name;
    [STAThread] static void Main(string[] args) {
        Application.EnableVisualStyles();
        try {
            Assembly assembly = typeof(Setup).Assembly;
            using (Stream payload = assembly.GetManifestResourceStream("CapKab.exe")) {
                if (payload == null || payload.ReadByte() != 'M' || payload.ReadByte() != 'Z') throw new Exception("The installer package is damaged.");
                payload.Position = 0;
                if (args.Length == 1 && args[0] == "--verify-package") {
                    using (var hash = System.Security.Cryptography.SHA256.Create())
                        Console.WriteLine(BitConverter.ToString(hash.ComputeHash(payload)).Replace("-", ""));
                    return;
                }
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Product);
                if (MessageBox.Show("Install CapKab for your Windows account?\n\nFreeware for personal and commercial use.\nIt will open when installation finishes.",
                    Product + " Setup", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
                if (LegacyAppIsRunning()) {
                    MessageBox.Show("Exit Banana Capture from the system tray, then run Setup again.", Product,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                string target = Path.Combine(folder, "CapKab.exe");
                bool acquired;
                using (var mutex = new Mutex(true, @"Local\CapKab.1", out acquired)) {
                    if (!acquired) {
                        MessageBox.Show("Exit CapKab from the system tray, then run Setup again.", Product, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    try {
                        Directory.CreateDirectory(folder);
                        string staged = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
                        string stagedLicense = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
                        try {
                            using (var file = new FileStream(staged, FileMode.CreateNew, FileAccess.Write)) payload.CopyTo(file);
                            using (Stream license = assembly.GetManifestResourceStream("License.txt")) {
                                if (license == null) throw new Exception("The installer license is missing.");
                                using (var file = new FileStream(stagedLicense, FileMode.CreateNew, FileAccess.Write)) license.CopyTo(file);
                            }
                            string licensePath = Path.Combine(folder, "LICENSE.txt");
                            if (File.Exists(licensePath)) File.Replace(stagedLicense, licensePath, null);
                            else File.Move(stagedLicense, licensePath);
                            if (File.Exists(target)) File.Replace(staged, target, null);
                            else File.Move(staged, target);
                        } finally { File.Delete(staged); File.Delete(stagedLicense); }
                        CreateShortcut(target, folder);
                        using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\CapKab")) {
                            key.SetValue("DisplayName", Product);
                            key.SetValue("DisplayVersion", ProductInfo.Version);
                            key.SetValue("Publisher", ProductInfo.Owner);
                            key.SetValue("InstallLocation", folder);
                            key.SetValue("DisplayIcon", target);
                            key.SetValue("UninstallString", "\"" + target + "\" --uninstall");
                            key.SetValue("NoModify", 1);
                            key.SetValue("NoRepair", 1);
                        }
                        MigrateSettings();
                        MigrateStartup(target);
                    } finally { mutex.ReleaseMutex(); }
                }
                Process.Start(new ProcessStartInfo(target, "--settings") { WorkingDirectory = folder });
            }
        } catch (Exception ex) {
            if (args.Length > 0) { Console.WriteLine(ex); Environment.ExitCode = 1; }
            else MessageBox.Show(ex.Message, Product + " Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static bool LegacyAppIsRunning() {
        try {
            using (var mutex = Mutex.OpenExisting(@"Local\BananaCapture.1")) {
                bool acquired;
                try { acquired = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { acquired = true; }
                if (acquired) mutex.ReleaseMutex();
                return !acquired;
            }
        } catch (WaitHandleCannotBeOpenedException) { return false; }
    }

    static void MigrateSettings() {
        using (var current = Registry.CurrentUser.OpenSubKey(@"Software\CapKab"))
            if (current != null) return;
        using (var legacy = Registry.CurrentUser.OpenSubKey(@"Software\Banana Capture")) {
            if (legacy == null) return;
            using (var current = Registry.CurrentUser.CreateSubKey(@"Software\CapKab")) {
                foreach (string name in new[] { "Folder", "FilePrefix", "Language" }) {
                    string value = legacy.GetValue(name) as string;
                    if (value == null) continue;
                    if (name == "FilePrefix" && value == "Banana") value = Product;
                    current.SetValue(name, value, RegistryValueKind.String);
                }
                foreach (string name in new[] { "FilePattern", "Sound", "AutoSave", "HideOnClose", "DarkMode",
                    "Width", "Height", "Region", "Screen", "Scroll", "Fixed", "Freehand" }) {
                    object value = legacy.GetValue(name);
                    if (value is int) current.SetValue(name, value, RegistryValueKind.DWord);
                }
            }
        }
    }

    static void MigrateStartup(string target) {
        const string run = @"Software\Microsoft\Windows\CurrentVersion\Run";
        using (var key = Registry.CurrentUser.OpenSubKey(run, true)) {
            if (key == null || key.GetValue(Product) != null) return;
            string legacyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BananaCapture", "BananaCapture.exe");
            string expected = "\"" + legacyPath + "\" --background";
            string command = key.GetValue("BananaCapture") as string;
            if (!string.Equals(command, expected, StringComparison.OrdinalIgnoreCase)) return;
            key.SetValue(Product, "\"" + target + "\" --background", RegistryValueKind.String);
            key.DeleteValue("BananaCapture", false);
        }
    }

    static void CreateShortcut(string target, string folder) {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Product + ".lnk");
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = target;
        link.Arguments = "--settings";
        link.WorkingDirectory = folder;
        link.Description = "Capture an area or all screens";
        link.Save();
    }
}




