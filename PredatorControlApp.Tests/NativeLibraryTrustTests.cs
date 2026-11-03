using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using PredatorControlApp;
using Xunit;

namespace PredatorControlApp.Tests;

public class NativeLibraryTrustTests
{
    [Fact]
    public void LoadedNativeLibraryUsesHashSpecificEmbeddedCopy()
    {
        var assembly = typeof(PredatorNativeCore).Assembly;
        string name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("predator_core.dll", StringComparison.OrdinalIgnoreCase));
        using Stream embedded = assembly.GetManifestResourceStream(name)!;
        string digest = Convert.ToHexString(SHA256.HashData(embedded));

        Assert.True(PredatorNativeCore.IsNativeCoreAvailable);
        Assert.Equal("Embedded, verified", PredatorNativeCore.LoadSource);
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PredatorControlApp", "native", "v1.5.5", digest, "predator_core.dll");
        using var cached = File.OpenRead(path);
        Assert.Equal(digest, Convert.ToHexString(SHA256.HashData(cached)));
    }
}
