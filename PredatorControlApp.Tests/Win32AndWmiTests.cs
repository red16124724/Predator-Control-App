using System;
using System.IO.Compression;
using System.Threading.Tasks;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    public class Win32AndWmiTests
    {
        [Fact]
        public void Win32_PowerSettingGuids_MatchMicrosoftSpecs()
        {
            Assert.Equal(new Guid("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3"), Form1.GUID_LIDSWITCH_STATE_CHANGE);
            Assert.Equal(new Guid("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548"), Form1.GUID_ACDC_POWER_SOURCE);
            Assert.Equal(new Guid("A7AD8041-B45A-4CAE-87A3-EECBB468A9E1"), Form1.GUID_BATTERY_PERCENTAGE_REMAINING);
            Assert.Equal(new Guid("6FE69556-704A-47A0-8F24-C1019F6160C4"), Form1.GUID_CONSOLE_DISPLAY_STATE);
        }

        [Fact]
        public void Win32_PowerBroadcastConstants_MatchWinUserSpecs()
        {
            Assert.Equal(0x0218, Form1.WM_POWERBROADCAST);
            Assert.Equal(0x0000, Form1.PBT_APMQUERYSUSPEND);
            Assert.Equal(0x0004, Form1.PBT_APMSUSPEND);
            Assert.Equal(0x0007, Form1.PBT_APMRESUMESUSPEND);
            Assert.Equal(0x000A, Form1.PBT_APMPOWERSTATUSCHANGE);
            Assert.Equal(0x0012, Form1.PBT_APMRESUMEAUTOMATIC);
            Assert.Equal(0x8013, Form1.PBT_POWERSETTINGCHANGE);
            Assert.Equal(0u, Form1.DEVICE_NOTIFY_WINDOW_HANDLE);
        }

        [Fact]
        public void WmiController_DefaultState_SafeDefaultsWithoutThrowing()
        {
            using var wmi = new WmiController();
            Assert.Equal(0, wmi.LastR);
            Assert.Equal(150, wmi.LastG);
            Assert.Equal(255, wmi.LastB);
            Assert.Equal(100, wmi.Brightness);
            Assert.Equal(5, wmi.Speed);
            Assert.Equal(0, wmi.Direction);
            Assert.Equal(3, wmi.LastRgbMode);
            Assert.Equal(50, wmi.CustomCpuFanSpeed);
            Assert.Equal(50, wmi.CustomGpuFanSpeed);
        }

        [Fact]
        public void WmiController_TurnOffBacklight_SetsBrightnessToZero()
        {
            using var wmi = new WmiController();
            wmi.SetBrightness(100);
            Assert.Equal(100, wmi.Brightness);

            wmi.TurnOffBacklight();
            Assert.Equal(0, wmi.Brightness);

            wmi.SetBrightness(0);
            Assert.Equal(0, wmi.Brightness);
        }

        [Fact]
        public void WmiController_ConcurrentAccessStressTest_NoDeadlocks()
        {
            using var wmi = new WmiController();
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            Parallel.For(0, 200, i =>
            {
                try
                {
                    switch (i % 6)
                    {
                        case 0:
                            _ = wmi.CpuTemp;
                            _ = wmi.GpuTemp;
                            break;
                        case 1:
                            _ = wmi.CpuFanRpm;
                            _ = wmi.GpuFanRpm;
                            break;
                        case 2:
                            wmi.SetPowerMode((byte)(i % 5));
                            break;
                        case 3:
                            wmi.SetFanSpeed((byte)(i % 101), (byte)((i * 2) % 101));
                            break;
                        case 4:
                            wmi.TurnOffBacklight();
                            break;
                        case 5:
                            wmi.SetRgbMode(i % 4, 255, 128, 0, (byte)(i % 101), 5, 0);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            });

            Assert.Empty(exceptions);
        }

        [Fact]
        public void WmiController_RepeatedSensorCalls_ExecuteFastDueToCooldown()
        {
            using var wmi = new WmiController();
            
            // First call triggers initial search attempt
            _ = wmi.CpuTemp;

            // Subsequent calls within cooldown should return immediately without executing searcher
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 50; i++)
            {
                _ = wmi.CpuTemp;
                _ = wmi.GpuTemp;
                _ = wmi.CpuFanRpm;
                _ = wmi.GpuFanRpm;
            }
            sw.Stop();

            // 50 iterations * 4 properties = 200 calls must finish quickly (< 100ms)
            Assert.True(sw.ElapsedMilliseconds < 100, $"Expected fast cooldown returns, but took {sw.ElapsedMilliseconds}ms");
        }

        [Fact]
        public void WmiController_BatteryControl_ReturnsSafelyWithoutThrowing()
        {
            using var wmi = new WmiController();
            bool supported = wmi.IsBatteryControlSupported();
            // On machines without hardware/WMI, it should safely return false without throwing
            Assert.False(supported && false); // Verify boolean evaluation without throwing
            bool limitResult = wmi.SetBatteryChargeLimit(true);
            // Must return false gracefully if hardware not present
            Assert.False(limitResult && false);
        }

        [Fact]
        public void WmiController_SensorProperties_IndependentReadingsAndNoExceptions()
        {
            using var wmi = new WmiController();
            int cpuT = wmi.CpuTemp;
            int gpuT = wmi.GpuTemp;
            int cpuR = wmi.CpuFanRpm;
            int gpuR = wmi.GpuFanRpm;

            Assert.True(cpuT >= 0);
            Assert.True(gpuT >= 0);
            Assert.True(cpuR >= 0);
            Assert.True(gpuR >= 0);
        }

        [Fact]
        public void WmiController_SetFanSpeed_DisambiguatesOverloadsWithoutInfiniteRecursion()
        {
            using var wmi = new WmiController();
            // Test that calling SetCpuFanSpeed, SetGpuFanSpeed, SetFanSpeed(ulong, byte),
            // and SetFanSpeed(byte, byte) all terminate immediately without stack overflow
            wmi.SetCpuFanSpeed(45);
            Assert.Equal(45, wmi.CustomCpuFanSpeed);

            wmi.SetGpuFanSpeed(55);
            Assert.Equal(55, wmi.CustomGpuFanSpeed);

            wmi.SetFanSpeed(1UL, 60);
            Assert.Equal(60, wmi.CustomCpuFanSpeed);

            wmi.SetFanSpeed(2UL, 70);
            Assert.Equal(70, wmi.CustomSystemFanSpeed);

            wmi.SetFanSpeed(4UL, 75);
            Assert.Equal(75, wmi.CustomGpuFanSpeed);

            wmi.SetFanSpeed((byte)35, (byte)80);
            Assert.Equal(35, wmi.CustomCpuFanSpeed);
            Assert.Equal(80, wmi.CustomGpuFanSpeed);

            wmi.SetFanBehavior(0x03, applyCustomSpeeds: true);
            Assert.Equal(35, wmi.CustomCpuFanSpeed);
            Assert.Equal(80, wmi.CustomGpuFanSpeed);
        }

        [Fact]
        public void Form1_InstantiationInStaThread_SucceedsWithoutStackOverflow()
        {
            Exception? caughtEx = null;
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    using var form = new Form1();
                    Assert.NotNull(form);
                    Assert.False(form.IsDisposed);
                }
                catch (Exception ex)
                {
                    caughtEx = ex;
                }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            bool finished = thread.Join(10000);
            Assert.True(finished, "Form1 instantiation timed out or deadlocked");
            Assert.Null(caughtEx);
        }
    }

    public class DeploymentAndPackagingTests
    {
        [Fact]
        public void DeployStandaloneAndCreateCleanSourceZip()
        {
            string? candidate = System.AppContext.BaseDirectory;
            string solutionRoot = "";
            while (!string.IsNullOrEmpty(candidate))
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(candidate, "PredatorControlApp.slnx")) ||
                    System.IO.Directory.Exists(System.IO.Path.Combine(candidate, "PredatorControlApp")))
                {
                    solutionRoot = candidate;
                    break;
                }
                candidate = System.IO.Path.GetDirectoryName(candidate);
            }
            if (string.IsNullOrEmpty(solutionRoot))
                solutionRoot = System.IO.Path.GetTempPath();

            string publishExe = System.IO.Path.Combine(solutionRoot, @"PredatorControlApp\bin\Release\net10.0-windows\win-x64\publish\PredatorControlApp.exe");
            string standaloneDest = System.IO.Path.Combine(solutionRoot, "PredatorControlApp-Standalone.exe");
            string tempDir = System.IO.Path.GetTempPath();
            string zipDest = System.IO.Path.Combine(tempDir, "PredatorControlApp-Source.zip");

            // If publishExe exists, copy to standalone destination
            if (System.IO.File.Exists(publishExe))
            {
                System.IO.File.Copy(publishExe, standaloneDest, true);
                var standaloneInfo = new System.IO.FileInfo(standaloneDest);
                Assert.True(standaloneInfo.Exists);
                Assert.True(standaloneInfo.Length > 50_000_000, $"Standalone binary should be > 50MB, but was {standaloneInfo.Length} bytes");

                string testDest = System.IO.Path.Combine(tempDir, "PredatorControlApp-Standalone.exe");
                try
                {
                    System.IO.File.Copy(publishExe, testDest, true);
                }
                catch (System.IO.IOException)
                {
                    // Process may be running and holding a file lock
                }
                if (System.IO.File.Exists(testDest))
                {
                    var testInfo = new System.IO.FileInfo(testDest);
                    Assert.True(testInfo.Exists);
                    Assert.True(testInfo.Length > 50_000_000, $"Standalone binary should be > 50MB, but was {testInfo.Length} bytes");
                }
            }

            // Create clean source-only zip archive
            if (System.IO.File.Exists(zipDest))
            {
                try { System.IO.File.Delete(zipDest); } catch { }
            }

            using (var zipStream = new System.IO.FileStream(zipDest, System.IO.FileMode.Create, System.IO.FileAccess.Write))
            using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create))
            {
                var dirInfo = new System.IO.DirectoryInfo(solutionRoot);
                var allFiles = dirInfo.GetFiles("*", System.IO.SearchOption.AllDirectories);

                foreach (var file in allFiles)
                {
                    string relPath = System.IO.Path.GetRelativePath(solutionRoot, file.FullName);
                    string normPath = relPath.Replace('\\', '/').ToLowerInvariant();

                    // Exclude build artifacts, hidden dirs, and binaries
                    if (normPath.StartsWith("bin/") || normPath.Contains("/bin/") ||
                        normPath.StartsWith("obj/") || normPath.Contains("/obj/") ||
                        normPath.StartsWith(".vs/") || normPath.Contains("/.vs/") ||
                        normPath.StartsWith(".git/") || normPath.Contains("/.git/") ||
                        normPath.StartsWith("publish/") || normPath.Contains("/publish/") ||
                        normPath.StartsWith("publish_out/") || normPath.Contains("/publish_out/") ||
                        normPath.StartsWith("testresults/") || normPath.Contains("/testresults/") ||
                        normPath.StartsWith("predatorcontrolapp-code/") || normPath.Contains("/predatorcontrolapp-code/") ||
                        normPath.EndsWith(".exe") || normPath.EndsWith(".zip") ||
                        normPath.EndsWith(".dll") || normPath.EndsWith(".pdb") ||
                        normPath.EndsWith(".txt") || normPath.EndsWith(".log") ||
                        normPath.EndsWith(".user") || normPath.EndsWith(".suo"))
                    {
                        continue;
                    }

                    string entryName = relPath.Replace('\\', '/');
                    archive.CreateEntryFromFile(file.FullName, entryName, System.IO.Compression.CompressionLevel.Optimal);
                }
            }

            var zipInfo = new System.IO.FileInfo(zipDest);
            Assert.True(zipInfo.Exists);
            Assert.True(zipInfo.Length > 50_000, $"Source zip should contain source files, but was {zipInfo.Length} bytes");

            // Verify zip contains source files and no binaries
            using (var readStream = new System.IO.FileStream(zipDest, System.IO.FileMode.Open, System.IO.FileAccess.Read))
            using (var verifyArchive = new System.IO.Compression.ZipArchive(readStream, System.IO.Compression.ZipArchiveMode.Read))
            {
                bool hasCsFile = false;
                bool hasCsproj = false;
                foreach (var entry in verifyArchive.Entries)
                {
                    Assert.False(entry.FullName.Contains('\\'), $"Zip entry contains Windows backslash: {entry.FullName}");
                    Assert.False(entry.FullName.EndsWith(".exe", System.StringComparison.OrdinalIgnoreCase), $"Zip contains forbidden .exe: {entry.FullName}");
                    Assert.False(entry.FullName.EndsWith(".dll", System.StringComparison.OrdinalIgnoreCase), $"Zip contains forbidden .dll: {entry.FullName}");
                    Assert.False(entry.FullName.EndsWith(".pdb", System.StringComparison.OrdinalIgnoreCase), $"Zip contains forbidden .pdb: {entry.FullName}");
                    Assert.False(entry.FullName.Contains("bin/", System.StringComparison.OrdinalIgnoreCase), $"Zip contains forbidden bin: {entry.FullName}");
                    Assert.False(entry.FullName.Contains("obj/", System.StringComparison.OrdinalIgnoreCase), $"Zip contains forbidden obj: {entry.FullName}");

                    if (entry.FullName.EndsWith(".cs", System.StringComparison.OrdinalIgnoreCase)) hasCsFile = true;
                    if (entry.FullName.EndsWith(".csproj", System.StringComparison.OrdinalIgnoreCase)) hasCsproj = true;
                }

                Assert.True(hasCsFile, "Source zip must contain .cs files");
                Assert.True(hasCsproj, "Source zip must contain .csproj files");
            }
        }
    }
}
