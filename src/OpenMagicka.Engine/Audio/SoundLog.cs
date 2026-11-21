namespace OpenMagicka.Engine.Audio;

/// <summary>
/// A debugging record of the sound (set <see cref="Variable"/> to 1): once a second, the cues started in it by name and
/// count, and the cues playing; a cue started on most ticks is marked, as one restarted every tick buzzes. A pause
/// writes what it paused and what it left sounding. Lines go to the console, so to the log file.
/// </summary>
public sealed class SoundLog(TextWriter output)
{
    public const string Variable = "OPENMAGICKA_SOUND_LOG";

    /// <summary>Starts in a second past which a cue counts as restarted every tick.</summary>
    public const int Restarting = 20;

    private readonly Dictionary<string, int> started = new(StringComparer.OrdinalIgnoreCase);
    private string lastPlaying = "";
    private float elapsed, clock;

    /// <summary>The log when the environment asks for it, writing to the console's error stream.</summary>
    public static SoundLog? FromEnvironment() =>
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } value && value != "0" ? new SoundLog(Console.Error) : null;

    public void Started(string name) => started[name] = started.GetValueOrDefault(name) + 1;

    /// <summary>A step of real time; past a second, its line (when anything started or what plays changed).</summary>
    public void Step(float dt, IEnumerable<ISoundCue> active)
    {
        elapsed += dt;
        clock += dt;
        if (elapsed < 1)
        {
            return;
        }
        var playing = Names(active.Where(c => c.IsPlaying && !c.IsStopping));
        if (started.Count > 0 || playing != lastPlaying)
        {
            var starts = string.Join(", ", started.OrderByDescending(s => s.Value).Select(s =>
                s.Value > 1 ? $"{s.Key} x{s.Value}{(s.Value >= Restarting * elapsed ? " (restarted every tick?)" : "")}" : s.Key));
            output.WriteLine($"sound {clock,7:F1}s: started [{starts}]; playing [{playing}]");
        }
        (lastPlaying, elapsed) = (playing, 0);
        started.Clear();
    }

    /// <summary>A pause: the cues it paused, and those it left sounding (any it passed by).</summary>
    public void Paused(IEnumerable<ISoundCue> paused, IEnumerable<ISoundCue> active) =>
        output.WriteLine($"sound {clock,7:F1}s: paused [{Names(paused)}]; left sounding [{Names(active.Where(c => !c.IsPaused && !c.IsStopped))}]");

    public void Resumed() => output.WriteLine($"sound {clock,7:F1}s: resumed");

    private static string Names(IEnumerable<ISoundCue> cues) =>
        string.Join(", ", cues.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key));
}
