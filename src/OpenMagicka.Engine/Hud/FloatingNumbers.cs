using System.Globalization;
using System.Numerics;
using OpenMagicka.Gameplay.Bosses;
using OpenMagicka.Gameplay.Characters;
using OpenMagicka.Gameplay.Combat;
using OpenMagicka.Gameplay.Entities;

namespace OpenMagicka.Engine.Hud;

/// <summary>A number over a head: where, its text (digits, a heal's with a leading '+'), its colour, its scale (of 22 × 32 a glyph) and alpha.</summary>
public readonly record struct FloatingNumber(Vector3 At, string Text, Vector4 Colour, float Scale, float Alpha);

/// <summary>
/// The damage and heal numbers over characters' heads, and over named bosses' (docs/engine/ARCHITECTURE.md §19), read
/// from their health each tick: a blow's number follows its target for a third of a second, adding the blows that come meanwhile, then shows
/// 0.4 s; burning, poison, bleeding and healing add up and show once a second for 0.7 s. A number stays opaque and
/// drops away at its very end; a big one is bigger.
/// </summary>
public sealed class FloatingNumbers
{
    public const float BlowLock = 0.333f, BlowLife = 0.4f, TallyEvery = 1, TallyLife = 0.7f, MaxScaleGain = 2, ScaleStep = 1000;

    public static readonly Vector4 Damage = new(1, 0.9f, 0, 1), Heal = new(0, 1, 0, 1), Burning = new(1.8f, 0.6f, 0.4f, 1), Poison = new(1, 1.2f, 0, 1),
        Bleeding = new(1.25f, 0.125f, 0.063f, 1);

    private enum Kind : byte { Burning, Poison, Bleeding, Heal }

    private sealed class Watch(float health, int blows)
    {
        public float Health { get; set; } = health;
        public int Blows { get; set; } = blows;
        public float[] Tally { get; } = new float[4];
        public float Clock { get; set; }
        public Shown? Blow { get; set; }
    }

    private sealed class Shown(Func<Vector3>? follow, Vector3 at, float value, Vector4 colour, float life, float locked)
    {
        public Func<Vector3>? Follow { get; set; } = follow;
        public Vector3 At { get; set; } = at;
        public float Value { get; set; } = value;
        public Vector4 Colour { get; } = colour;
        public float Life { get; } = life;
        public float Left { get; set; } = life;
        public float Locked { get; set; } = locked;
    }

    private readonly Dictionary<Entity, Watch> watched = new(ReferenceEqualityComparer.Instance);
    private readonly List<Shown> shown = [];
    private readonly List<Entity> gone = [];
    private readonly HashSet<Entity> seen = new(ReferenceEqualityComparer.Instance);

    /// <summary>What a number is read from: a character's or a boss's health, blows, statuses and head.</summary>
    private readonly record struct Subject(Entity Entity, float Health, int Blows, StatusSet Statuses, Func<Vector3> Head);

    /// <summary>The most numbers shown at once (as the original's).</summary>
    public const int MaxShown = 1024;

    public int Count => shown.Count;

    /// <summary>A tick, after the world's: health changes become numbers; heals wait outside cutscenes.</summary>
    public void Update(float dt, IEnumerable<Character> characters, bool inCutscene, IEnumerable<Boss>? bosses = null)
    {
        ArgumentNullException.ThrowIfNull(characters);
        seen.Clear();
        foreach (var character in characters)
        {
            Read(new Subject(character, character.HitPoints, character.BlowsTaken, character.Statuses, () => Head(character)), dt, inCutscene);
        }
        foreach (var boss in bosses ?? [])
        {
            Read(new Subject(boss, boss.HitPoints, boss.BlowsTaken, boss.Vitals.Statuses, () => boss.NumbersAt), dt, inCutscene);
        }
        gone.Clear();
        foreach (var entity in watched.Keys)
        {
            if (!seen.Contains(entity))
            {
                gone.Add(entity);
            }
        }
        foreach (var entity in gone)
        {
            watched.Remove(entity);
        }
        for (var i = shown.Count - 1; i >= 0; i--)
        {
            var number = shown[i];
            if (number.Locked > 0)
            {
                number.Locked -= dt;
                if (number.Follow is { } follow)
                {
                    number.At = follow();
                }
            }
            else if ((number.Left -= dt) <= 0)
            {
                shown.RemoveAt(i);
            }
        }
    }

    /// <summary>Everything forgotten (the numbers turned off).</summary>
    public void Clear()
    {
        watched.Clear();
        shown.Clear();
    }

    public void Write(List<FloatingNumber> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        foreach (var number in shown)
        {
            var magnitude = MathF.Round(MathF.Abs(number.Value));
            if (magnitude < 1)
            {
                continue;
            }
            var text = (number.Value < 0 ? "+" : "") + magnitude.ToString(CultureInfo.InvariantCulture);
            var scale = (1 + MathF.Min(MaxScaleGain, magnitude / ScaleStep)) * 0.5f;
            var alpha = 1 - MathF.Pow(1 - Math.Clamp(number.Left / number.Life, 0, 1), 5);
            into.Add(new FloatingNumber(number.At, text, number.Colour, scale, alpha));
        }
    }

    /// <summary>A character's head: its centre raised by half its length and its radius.</summary>
    public static Vector3 Head(Character character)
    {
        ArgumentNullException.ThrowIfNull(character);
        return character.Position + Vector3.UnitY * (character.Template.Length / 2 + character.Template.Radius);
    }

    private void Read(Subject subject, float dt, bool inCutscene)
    {
        seen.Add(subject.Entity);
        if (!watched.TryGetValue(subject.Entity, out var watch))
        {
            watched[subject.Entity] = new Watch(subject.Health, subject.Blows);
            return;
        }
        var change = subject.Health - watch.Health;
        var struck = subject.Blows != watch.Blows;
        (watch.Health, watch.Blows) = (subject.Health, subject.Blows);
        if (change < 0 && struck)
        {
            Blow(subject, watch, -change);
        }
        else if (change < 0 && StatusKind(subject.Statuses) is { } status)
        {
            watch.Tally[(int)status] += -change;                              // a loss of no blow and no status (a script's) shows nothing
        }
        else if (change > 0)
        {
            watch.Tally[(int)Kind.Heal] += change;
        }
        watch.Clock += dt;
        if (watch.Clock >= TallyEvery)
        {
            watch.Clock = 0;
            Flush(subject, watch, inCutscene);
        }
    }

    private void Blow(Subject subject, Watch watch, float amount)
    {
        if (watch.Blow is { Locked: > 0 } open)
        {
            open.Value += amount;                                             // blows within a third of a second add up
            return;
        }
        watch.Blow = new Shown(subject.Head, subject.Head(), amount, Damage, BlowLife, BlowLock);
        Show(watch.Blow);
    }

    private void Flush(Subject subject, Watch watch, bool inCutscene)
    {
        for (var kind = 0; kind < watch.Tally.Length; kind++)
        {
            var amount = watch.Tally[kind];
            if (amount >= 0.5f && !(kind == (int)Kind.Heal && inCutscene))
            {
                var colour = (Kind)kind switch { Kind.Burning => Burning, Kind.Poison => Poison, Kind.Bleeding => Bleeding, _ => Heal };
                Show(new Shown(null, subject.Head(), kind == (int)Kind.Heal ? -amount : amount, colour, TallyLife, 0));
            }
            watch.Tally[kind] = 0;
        }
    }

    private void Show(Shown number)
    {
        if (shown.Count < MaxShown)
        {
            shown.Add(number);
        }
    }

    private static Kind? StatusKind(StatusSet statuses) =>
        statuses.Has(StatusEffects.Burning) ? Kind.Burning : statuses.Has(StatusEffects.Poisoned) ? Kind.Poison
        : statuses.Has(StatusEffects.Bleeding) ? Kind.Bleeding : null;
}
