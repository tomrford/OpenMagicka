using System.Security.Cryptography;

namespace OpenMagicka.Game;

/// <summary>
/// Which FNA3D driver to run when the user has not chosen one (FNA3D_FORCE_DRIVER). The original
/// effects are translated by MojoShader for each driver, and two of its translations get some of
/// them wrong in the stock FNA3D (OM-63): for SDL_GPU (Vulkan, FNA3D's first pick) the additive
/// light shafts come out in the wrong colours, and for D3D11 the characters' effect does not
/// compile. Our own FNA3D build (native/README.md) fixes both, so with it FNA3D picks as it likes;
/// with the stock one the OpenGL translation, which draws them all right, is the default.
/// On macOS OpenGL stays the default with our build too: FNA3D's first pick there is SDL_GPU on
/// Metal, whose translation still draws some models wrong or not at all.
/// </summary>
public static class GraphicsDriver
{
    public const string Variable = "FNA3D_FORCE_DRIVER";

    public const string Preferred = "OpenGL";

    /// <summary>The build record shipped next to our FNA3D (native/&lt;rid&gt;/SOURCE.txt, renamed).</summary>
    public const string OwnFna3dRecord = "FNA3D.OpenMagicka.txt";

    private static readonly string[] LibraryNames = ["FNA3D.dll", "libFNA3D.0.dylib", "libFNA3D.so.0"];

    /// <summary>The driver to set, or null to leave the choice to the user or to FNA3D.</summary>
    public static string? Default(string? requested, bool ownFna3d, bool macOS = false) =>
        string.IsNullOrWhiteSpace(requested) && (!ownFna3d || macOS) ? Preferred : null;

    /// <summary>
    /// Whether the FNA3D library in <paramref name="directory"/> is our build: the one whose hash its
    /// build record holds. A stock FNA3D there (a run without the launcher's copy, an override)
    /// is not taken for ours, so effects it would crash on stay refused.
    /// </summary>
    public static bool HasOwnFna3d(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var record = Path.Combine(directory, OwnFna3dRecord);
        try
        {
            if (!File.Exists(record))
            {
                return false;
            }
            var lines = File.ReadAllLines(record);
            foreach (var name in LibraryNames)
            {
                var prefix = name + " sha256 ";
                var recorded = lines.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..].Trim();
                if (recorded is null)
                {
                    continue;
                }
                var library = Path.Combine(directory, name);
                return File.Exists(library)
                    && string.Equals(recorded, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(library))), StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary><see cref="HasOwnFna3d(string)"/> for the running game's folder.</summary>
    public static bool HasOwnFna3d() => HasOwnFna3d(AppContext.BaseDirectory);
}
