using System;
using System.IO;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests
{
    /// <summary>
    /// Settings saves write settings.json.&lt;guid&gt;.tmp and delete it in a finally block.
    /// A process killed mid-save leaves that temp file behind forever, so Load() sweeps stale
    /// ones. These tests pin the sweep contract: stale files go, fresh and unrelated ones stay.
    /// </summary>
    [Collection("NoParallel")]
    public class SettingsTempFileHygieneTests
    {
        private static string MakeDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "pc_settings_hygiene_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void CleanupStaleTempFilesIn_DeletesOrphanedTemp_Files()
        {
            string dir = MakeDir();
            try
            {
                string main = Path.Combine(dir, "settings.json");
                string stale = main + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(main, "{}");
                File.WriteAllText(stale, "{}");
                File.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromHours(3));

                int removed = AppSettings.CleanupStaleTempFilesIn(main);

                Assert.Equal(1, removed);
                Assert.False(File.Exists(stale));
                Assert.True(File.Exists(main), "the live settings file must never be touched");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void CleanupStaleTempFilesIn_KeepsFreshTempFile_AndUnrelatedFiles()
        {
            string dir = MakeDir();
            try
            {
                string main = Path.Combine(dir, "settings.json");
                string freshTmp = main + "." + Guid.NewGuid().ToString("N") + ".tmp";
                string otherTmp = Path.Combine(dir, "somethingelse.json." + Guid.NewGuid().ToString("N") + ".tmp");

                File.WriteAllText(freshTmp, "{}");           // written now: a live save may own it
                File.WriteAllText(otherTmp, "{}");
                File.SetLastWriteTimeUtc(otherTmp, DateTime.UtcNow - TimeSpan.FromHours(3));

                int removed = AppSettings.CleanupStaleTempFilesIn(main);

                Assert.Equal(0, removed);
                Assert.True(File.Exists(freshTmp));
                Assert.True(File.Exists(otherTmp));
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        [Fact]
        public void CleanupStaleTempFilesIn_MissingDirectory_IsSafeNoOp()
        {
            string missing = Path.Combine(Path.GetTempPath(), "pc_settings_absent_" + Guid.NewGuid().ToString("N"), "settings.json");

            Assert.Equal(0, AppSettings.CleanupStaleTempFilesIn(missing));
            Assert.Equal(0, AppSettings.CleanupStaleTempFilesIn(""));
        }
    }
}
