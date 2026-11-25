using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

static class UninstallTests {
    static int checks;
    static void Assert(bool value, string message) {
        if (!value) throw new Exception(message);
        checks++;
    }
    static void Reject(Action action, string message) {
        bool rejected = false;
        try { action(); } catch (IOException) { rejected = true; } catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, message);
    }
    static int Main() {
        // The temporary folder keeps these paths short even when the project sits in a deeply nested folder.
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CapKab-uninstall-tests-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try {
            string folder = Path.Combine(root, "CapKab");
            string installed = Path.Combine(folder, "CapKab.exe");
            Directory.CreateDirectory(folder);
            File.WriteAllText(installed, "isolated executable substitute");
            string unrelated = Path.Combine(folder, "keep.txt");
            File.WriteAllText(unrelated, "user content must survive");
            Program.ValidateUninstallTarget(installed, installed);
            Program.ValidateUninstallTarget(installed.ToUpperInvariant(), installed);
            Reject(delegate { Program.ValidateUninstallTarget(unrelated, installed); }, "Different executable was accepted.");
            Reject(delegate { Program.ValidateUninstallTarget(Path.Combine(root, "CapKab.exe"), installed); }, "Different installation directory was accepted.");
            string worker = Path.Combine(root, "CapKab-Uninstall-" + Guid.NewGuid().ToString("N") + ".exe");
            Program.ValidateUninstallWorker(worker, root + Path.DirectorySeparatorChar);
            Reject(delegate { Program.ValidateUninstallWorker(Path.Combine(folder, Path.GetFileName(worker)), root); }, "Nested worker directory was accepted.");
            Reject(delegate { Program.ValidateUninstallWorker(Path.Combine(root, "CapKab-Uninstall-invalid.exe"), root); }, "Invalid worker identifier was accepted.");
            Reject(delegate { Program.ValidateUninstallWorker(Path.Combine(root, "Other-" + Guid.NewGuid().ToString("N") + ".exe"), root); }, "Wrong worker filename was accepted.");
            using (var lockedFile = new FileStream(installed, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                Reject(delegate { Program.DeleteUninstallExecutable(installed, installed, 150); }, "Locked executable deletion incorrectly succeeded.");
                Assert(File.Exists(installed), "Failure deleted the locked executable.");
                Assert(File.ReadAllText(unrelated) == "user content must survive", "Failure changed an unrelated file.");
            }
            Reject(delegate { Program.DeleteUninstallExecutable(unrelated, installed, 0); }, "Cleanup accepted an unrelated path.");
            Program.DeleteUninstallExecutable(installed, installed, 500);
            Assert(!File.Exists(installed), "Uninstall did not remove the executable substitute.");
            Program.RemoveEmptyInstallDirectory(folder);
            Assert(Directory.Exists(folder) && File.Exists(unrelated), "Uninstall removed a nonempty directory.");
            File.Delete(unrelated);
            Program.RemoveEmptyInstallDirectory(folder);
            Assert(!Directory.Exists(folder), "Empty installation directory remained.");

            string mutexName = @"Local\CapKab.UninstallTest." + Guid.NewGuid().ToString("N");
            using (var occupied = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false)) {
                var owner = new Thread(delegate() {
                    using (var mutex = Program.AcquireUninstallMutex(mutexName)) {
                        occupied.Set();
                        release.WaitOne();
                        mutex.ReleaseMutex();
                    }
                });
                owner.Start();
                Assert(occupied.WaitOne(2000), "Mutex test did not initialize.");
                try { Reject(delegate { using (var mutex = Program.AcquireUninstallMutex(mutexName)) { mutex.ReleaseMutex(); } }, "Uninstall ignored an active app mutex."); }
                finally { release.Set(); owner.Join(); }
            }
            using (var mutex = Program.AcquireUninstallMutex(mutexName)) {
                bool blocked = false;
                var contender = new Thread(delegate() {
                    try { using (var competing = Program.AcquireUninstallMutex(mutexName)) competing.ReleaseMutex(); }
                    catch (IOException) { blocked = true; }
                });
                contender.Start(); contender.Join();
                Assert(blocked, "Cleanup mutex did not remain held.");
                mutex.ReleaseMutex();
            }
            using (var current = Process.GetCurrentProcess()) {
                long ticks = current.StartTime.ToUniversalTime().Ticks;
                Program.WaitForUninstallParent(current.Id, ticks + 1, 100);
                Assert(true, "PID reuse guard returned.");
                var timer = Stopwatch.StartNew();
                Reject(delegate { Program.WaitForUninstallParent(current.Id, ticks, 100); }, "Parent wait did not time out.");
                Assert(timer.ElapsedMilliseconds < 2000, "Parent wait was not bounded.");
            }
            Program.WaitForUninstallParent(int.MaxValue, 1, 100);
            Assert(true, "Already-exited parent was handled.");
            Console.WriteLine("PASS: " + checks + " isolated uninstall checks; no HKCU or installed application was changed.");
            return 0;
        } catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally {
            // Only remove known disposable test files, preserving anything unexpected.
            string installed = Path.Combine(root, "CapKab", "CapKab.exe");
            string unrelated = Path.Combine(root, "CapKab", "keep.txt");
            if (File.Exists(installed)) File.Delete(installed);
            if (File.Exists(unrelated)) File.Delete(unrelated);
            Program.RemoveEmptyInstallDirectory(Path.Combine(root, "CapKab"));
            Program.RemoveEmptyInstallDirectory(root);
        }
    }
}
