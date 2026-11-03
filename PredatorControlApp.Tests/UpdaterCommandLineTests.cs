using System;
using System.Diagnostics;
using System.IO;
using System.Security;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests;

public class UpdaterCommandLineTests
{
    [Fact]
    public void UpdateScript_RunsWithSpacedPathsAndReceivesArguments()
    {
        string dir = Path.Combine(Path.GetTempPath(), "Predator update test " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string script = Path.Combine(dir, "update script.cmd");
            string marker = Path.Combine(dir, "staged marker.txt");
            string target = Path.Combine(dir, "target app.exe");
            File.WriteAllText(script, "@echo off\r\n>\"%~1\" echo %~2^|%~3\r\n");
            using var process = Process.Start(Updater.CreateUpdateProcessStartInfo(script, marker, target, 12345));
            Assert.NotNull(process);
            Assert.True(process!.WaitForExit(5000));
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(target + "|12345", File.ReadAllText(marker).Trim());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("%PATH%")]
    [InlineData("x&whoami")]
    [InlineData("x|whoami")]
    [InlineData("x\"whoami")]
    [InlineData("x\nwhoami")]
    public void UpdateScript_RejectsCommandMetacharacters(string invalid)
    {
        Assert.Throws<SecurityException>(() => Updater.CreateUpdateProcessStartInfo("script.cmd", "staged.exe", invalid, 1));
        Assert.Throws<SecurityException>(() => Updater.CreateUpdateProcessStartInfo("script.cmd", invalid, "target.exe", 1));
        Assert.Throws<SecurityException>(() => Updater.CreateUpdateProcessStartInfo(invalid, "staged.exe", "target.exe", 1));
    }
}
