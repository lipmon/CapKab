using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

static class PerformanceTest {
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    static FieldInfo Field(string name) {
        return typeof(CaptureApp).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
    }
    static double Median(List<double> values) {
        values.Sort();
        return (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2;
    }
    static double Megabytes(long bytes) { return bytes / (1024.0 * 1024.0); }
    static void Expect(bool condition, string message) {
        if (!condition) throw new Exception(message);
    }

    [STAThread] static void Main() {
        try {
            SetProcessDpiAwarenessContext(new IntPtr(-4));
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var app = new CaptureApp()) {
                app.RestWhenIdle();
                Run(app);
                Application.Run(app);
            }
        } catch (Exception ex) {
            Console.WriteLine(ex);
            Environment.ExitCode = 1;
        }
    }

    static async void Run(CaptureApp app) {
        // The temporary folder keeps the PNG paths short even when the project sits in a deeply nested folder.
        string scratchRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "CapKab-performance"));
        string destination = Path.Combine(scratchRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        try {
            Field("folder").SetValue(app, destination);
            Field("autosave").SetValue(app, true);
            Field("sound").SetValue(app, false);
            Field("filePrefix").SetValue(app, "Benchmark");
            Field("filePattern").SetValue(app, FileNamePattern.DateTime);
            Form window = (Form)Field("window").GetValue(app);
            Expect(!window.Visible, "Benchmark app should remain hidden.");
            await Task.Delay(500);

            using (Process process = Process.GetCurrentProcess()) {
                process.Refresh();
                long idleWorkingSet = process.WorkingSet64;
                long idlePrivate = process.PrivateMemorySize64;
                TimeSpan cpuStart = process.TotalProcessorTime;
                Stopwatch idle = Stopwatch.StartNew();
                await Task.Delay(5000);
                idle.Stop();
                process.Refresh();
                double cpuMilliseconds = (process.TotalProcessorTime - cpuStart).TotalMilliseconds;
                Console.WriteLine("Idle window {0:F2} s: CPU {1:F2} ms, total-machine CPU {2:F3}%",
                    idle.Elapsed.TotalSeconds, cpuMilliseconds,
                    cpuMilliseconds / idle.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100);
                Console.WriteLine("Just started: working set {0:F2} MiB, private memory {1:F2} MiB",
                    Megabytes(idleWorkingSet), Megabytes(idlePrivate));
                Console.WriteLine("Waiting in the tray (as with --background): working set {0:F2} MiB, private memory {1:F2} MiB",
                    Megabytes(process.WorkingSet64), Megabytes(process.PrivateMemorySize64));

                Rectangle bounds = SystemInformation.VirtualScreen;
                Console.WriteLine("Screen size {0} x {1}, logical processors {2}", bounds.Width, bounds.Height, Environment.ProcessorCount);
                for (int i = 0; i < 3; i++) using (var warmup = ScreenShot.Take(bounds)) { }
                uint gdiBefore = GetGuiResources(process.Handle, 0);
                var captureTimes = new List<double>();
                var saveTimes = new List<double>();
                for (int i = 0; i < 20; i++) {
                    Stopwatch timer = Stopwatch.StartNew();
                    using (var snapshot = ScreenShot.Take(bounds)) {
                        timer.Stop();
                        captureTimes.Add(timer.Elapsed.TotalMilliseconds);
                        timer.Restart();
                        string saved = CaptureApp.SavePng(snapshot.Image, destination, "Snapshot");
                        timer.Stop();
                        saveTimes.Add(timer.Elapsed.TotalMilliseconds);
                        using (var readback = new Bitmap(saved))
                            Expect(readback.Size == bounds.Size, "Saved screenshot dimensions mismatch.");
                        File.Delete(saved);
                    }
                }
                uint gdiAfter = GetGuiResources(process.Handle, 0);
                Expect(gdiAfter <= gdiBefore + 2, "Repeated screenshots leaked GDI handles.");
                Console.WriteLine("20 full-screen captures: median snapshot {0:F2} ms, median PNG save {1:F2} ms",
                    Median(captureTimes), Median(saveTimes));
                Console.WriteLine("GDI resources before {0}, after {1}", gdiBefore, gdiAfter);

                // What the area picker costs before the user can start dragging: grab the desktop and paint it once.
                var pickerTimes = new List<double>();
                IntPtr screenDc = GetDC(IntPtr.Zero);
                IntPtr targetDc = CreateCompatibleDC(screenDc);
                IntPtr targetBitmap = CreateCompatibleBitmap(screenDc, bounds.Width, bounds.Height);
                IntPtr previousBitmap = SelectObject(targetDc, targetBitmap);
                try {
                    for (int i = 0; i < 10; i++) {
                        Stopwatch timer = Stopwatch.StartNew();
                        using (var snapshot = ScreenShot.Take(bounds))
                            snapshot.Paint(targetDc, new Rectangle(Point.Empty, bounds.Size));
                        GdiFlush();
                        pickerTimes.Add(timer.Elapsed.TotalMilliseconds);
                    }
                } finally {
                    SelectObject(targetDc, previousBitmap);
                    DeleteObject(targetBitmap);
                    DeleteDC(targetDc);
                    ReleaseDC(IntPtr.Zero, screenDc);
                }
                Console.WriteLine("10 area-picker openings (grab + first paint, off screen): median {0:F2} ms", Median(pickerTimes));

                var entry = typeof(CaptureApp).GetMethod("Capture", BindingFlags.Instance | BindingFlags.NonPublic);
                var entryTimes = new List<double>();
                var completionTimes = new List<double>();
                for (int i = 0; i < 10; i++) {
                    Stopwatch timer = Stopwatch.StartNew();
                    entry.Invoke(app, new object[] { CaptureMode.Fullscreen });
                    entryTimes.Add(timer.Elapsed.TotalMilliseconds);
                    await (Task)Field("saveQueue").GetValue(app);
                    await Task.Yield();
                    while ((int)Field("pendingSaves").GetValue(app) > 0) await Task.Delay(1);
                    completionTimes.Add(timer.Elapsed.TotalMilliseconds);
                }
                Expect(Directory.GetFiles(destination, "*.png").Length == 10, "Capture entry point lost a screenshot.");
                Console.WriteLine("10 normal captures: median trigger-to-queue {0:F2} ms, trigger-to-file {1:F2} ms",
                    Median(entryTimes), Median(completionTimes));
                await Task.Delay(300);
                process.Refresh();
                Console.WriteLine("Post-capture process working set {0:F2} MiB, private memory {1:F2} MiB, peak working set {2:F2} MiB",
                    Megabytes(process.WorkingSet64), Megabytes(process.PrivateMemorySize64), Megabytes(process.PeakWorkingSet64));
                // The app hands its memory back three seconds after the last save.
                await Task.Delay(4000);
                process.Refresh();
                long restingWorkingSet = process.WorkingSet64;
                Console.WriteLine("Resting again after captures: working set {0:F2} MiB, private memory {1:F2} MiB",
                    Megabytes(restingWorkingSet), Megabytes(process.PrivateMemorySize64));
                Expect(restingWorkingSet < idleWorkingSet, "Memory was not handed back to Windows after the captures.");
                // Handing memory back must not make the next capture slow.
                Stopwatch wake = Stopwatch.StartNew();
                entry.Invoke(app, new object[] { CaptureMode.Fullscreen });
                double wakeMilliseconds = wake.Elapsed.TotalMilliseconds;
                await (Task)Field("saveQueue").GetValue(app);
                await Task.Yield();
                while ((int)Field("pendingSaves").GetValue(app) > 0) await Task.Delay(1);
                Console.WriteLine("First capture after resting: trigger-to-queue {0:F2} ms", wakeMilliseconds);
                Console.WriteLine("Performance and capture regression checks passed.");
            }
        } catch (Exception ex) {
            Console.WriteLine(ex);
            Environment.ExitCode = 1;
        } finally {
            string resolved = Path.GetFullPath(destination);
            if (resolved.StartsWith(scratchRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
                foreach (string file in Directory.GetFiles(resolved)) File.Delete(file);
                Directory.Delete(resolved);
                if (Directory.GetFileSystemEntries(scratchRoot).Length == 0) Directory.Delete(scratchRoot);
            }
            app.ExitThread();
        }
    }
}
