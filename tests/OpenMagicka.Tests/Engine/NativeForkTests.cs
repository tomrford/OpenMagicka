using System.Diagnostics;
using System.Security.Cryptography;

namespace OpenMagicka.Tests.Engine;

/// <summary>Our prebuilt FNA3D and FAudio (native/README.md) must match the submodules and patches they record.</summary>
public class NativeForkTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "OpenMagicka.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new DirectoryNotFoundException("the repository root was not found");
    }

    private static Dictionary<string, string> Recorded(string platform, string record = "SOURCE.txt") =>
        File.ReadAllLines(Path.Combine(Root, "native", platform, record))
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("FNA3D ", StringComparison.Ordinal) || line.StartsWith("MojoShader", StringComparison.Ordinal)
                || line.StartsWith("FAudio ", StringComparison.Ordinal) || line.Contains(" sha256 ", StringComparison.Ordinal))
            .Select(line => line.Split(' '))
            .ToDictionary(parts => parts[0], parts => parts[^1]);

    private static string Head(string submodule)
    {
        using var git = Process.Start(new ProcessStartInfo("git", ["-C", Path.Combine(Root, submodule), "rev-parse", "HEAD"])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var head = git.StandardOutput.ReadToEnd().Trim();
        git.WaitForExit();
        return head;
    }

    private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    [Theory]
    [InlineData("win-x64")]
    [InlineData("osx-arm64")]
    public void TheBuild_WasMadeFromTheSubmodulesFnaPins(string platform)
    {
        var recorded = Recorded(platform);

        Assert.Equal(Head("lib/FNA/lib/FNA3D"), recorded["FNA3D"]);
        Assert.Equal(Head("lib/FNA/lib/FNA3D/MojoShader"), recorded["MojoShader"]);
    }

    [Theory]
    [InlineData("win-x64", "FNA3D.dll")]
    [InlineData("osx-arm64", "libFNA3D.0.dylib")]
    public void TheBuild_HasEveryPatch_AsItIsNow_AndIsTheShippedLibrary(string platform, string library)
    {
        var recorded = Recorded(platform);
        var patches = Directory.GetFiles(Path.Combine(Root, "native", "patches", "MojoShader"), "*.patch");

        Assert.NotEmpty(patches);
        Assert.All(patches, patch => Assert.Equal(Sha256(patch), recorded["MojoShader/" + Path.GetFileName(patch)]));
        Assert.Equal(patches.Length, recorded.Keys.Count(k => k.StartsWith("MojoShader/", StringComparison.Ordinal)));
        Assert.Equal(Sha256(Path.Combine(Root, "native", platform, library)), recorded[library]);
    }

    [Theory]
    [InlineData("MojoShader-macos")]
    [InlineData("FNA3D-macos")]
    public void TheMacBuild_HasEveryMacOnlyPatch_AsItIsNow(string directory)
    {
        var recorded = Recorded("osx-arm64");
        var patches = Directory.GetFiles(Path.Combine(Root, "native", "patches", directory), "*.patch");

        Assert.NotEmpty(patches);
        Assert.All(patches, patch => Assert.Equal(Sha256(patch), recorded[directory + "/" + Path.GetFileName(patch)]));
        Assert.Equal(patches.Length, recorded.Keys.Count(k => k.StartsWith(directory + "/", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheWindowsFAudio_WasMadeFromTheSubmoduleFnaPins_WithEveryPatch_AndIsTheShippedLibrary()
    {
        var recorded = Recorded("win-x64", "FAudio.SOURCE.txt");
        var patches = Directory.GetFiles(Path.Combine(Root, "native", "patches", "FAudio"), "*.patch");

        Assert.Equal(Head("lib/FNA/lib/FAudio"), recorded["FAudio"]);
        Assert.NotEmpty(patches);
        Assert.All(patches, patch => Assert.Equal(Sha256(patch), recorded["FAudio/" + Path.GetFileName(patch)]));
        Assert.Equal(patches.Length, recorded.Keys.Count(k => k.StartsWith("FAudio/", StringComparison.Ordinal)));
        Assert.Equal(Sha256(Path.Combine(Root, "native", "win-x64", "FAudio.dll")), recorded["FAudio.dll"]);
    }
}
