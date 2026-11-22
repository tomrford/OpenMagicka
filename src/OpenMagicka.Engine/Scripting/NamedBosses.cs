using System.Numerics;
using OpenMagicka.Engine.Characters;
using OpenMagicka.Gameplay.Bosses;
using OpenMagicka.Gameplay.Entities;

namespace OpenMagicka.Engine.Scripting;

/// <summary>
/// A named boss of OpenMagicka's own code in a fight (docs/gameplay/BOSSES.md §1): each <see cref="Enter"/> makes its
/// world entity anew at the place (the one before leaves), as the original's spawn and checkpoint replay set its boss up
/// whole again; the fight's frame tells it whether the fight runs. Only the host makes it. A level that cannot take a new
/// one (full, or stepping) keeps the one it had, and tries again each frame. Its hit points and death are its entity's;
/// with none it is not dead.
/// </summary>
/// <param name="size">The entities a boss takes in the level, itself with its parts (the room it needs).</param>
public sealed class NamedBoss(string name, int size, Func<ScriptScene, Matrix4x4, Boss> make) : IFightBoss
{
    private readonly Func<ScriptScene, Matrix4x4, Boss> make = make ?? throw new ArgumentNullException(nameof(make));
    private readonly int size = size > 0 ? size : throw new ArgumentOutOfRangeException(nameof(size), size, "must be positive");

    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>The boss in the world now, if any.</summary>
    public Boss? Boss { get; private set; }

    public float HitPoints => Boss?.HitPoints ?? 0;

    public float MaxHitPoints => Boss?.MaxHitPoints ?? 0;

    public bool IsDead => Boss is { IsDead: true };

    private (ScriptScene Scene, Matrix4x4 At)? waiting;

    public void Enter(ScriptScene scene, Matrix4x4 at)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (!scene.IsHost)
        {
            return;
        }
        waiting = (scene, at);
        TryEnter();
    }

    /// <summary>
    /// The new one comes in if the level has room for it beside the old one (which leaves at the end of this frame). With
    /// no old one in the world it waits for room; with one, that one stays as it was.
    /// </summary>
    private void TryEnter()
    {
        if (waiting is not var (scene, at) || scene.World.Physics.IsStepping || scene.World.Entities.IsBusy)
        {
            return;
        }
        var manager = scene.World.Entities;
        if (manager.Entities.Count + size > EntityManager.Capacity)
        {
            if (Boss is { Manager: not null, IsRemovable: false })
            {
                Console.Error.WriteLine($"boss: the level is full; '{Name}' stays as it was");
                waiting = null;
            }
            else
            {
                Boss = null;                                                  // none in the world: wait for room
            }
            return;
        }
        var boss = make(scene, at);
        try
        {
            manager.Add(boss);
        }
        catch (InvalidOperationException)
        {
            manager.Discard(boss);                                            // no room after all: the old one stays
            waiting = null;
            return;
        }
        Boss?.Withdraw();                                                     // the old one leaves only once the new one is in
        (Boss, waiting) = (boss, null);
    }

    public void Tick(bool fightRunning)
    {
        TryEnter();
        if (Boss is { } boss)
        {
            boss.FightStarted = fightRunning;
        }
    }

    /// <summary>As the original's Jörmungandr, the bosses made so far heed no message.</summary>
    public void Message(BossMessages message)
    {
    }
}

/// <summary>
/// The named bosses OpenMagicka has made (OM-42), by the name a script's spawnBoss gives (any case): each from the
/// game's own model of it. One not made yet is none (its fight has the debugging stand-in, <see cref="StandInBoss"/>).
/// </summary>
public static class NamedBosses
{
    /// <summary>The names made so far.</summary>
    public static IReadOnlyList<string> Names { get; } = ["jormungandr"];

    /// <summary>A named boss for a fight, its model read through <paramref name="models"/>; null for one not made, or whose model the game lacks.</summary>
    public static NamedBoss? Make(string name, Func<string, AnimatedModel?> models)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(models);
        switch (name.ToLowerInvariant())
        {
            case "jormungandr":
                if (models(Jormungandr.Asset) is not { } model)
                {
                    return null;
                }
                return new NamedBoss(name, Jormungandr.TakesUp, (scene, at) => new Jormungandr(scene.World.Physics, new BossPose(model.Skeleton, model.Clips), at,
                    new Random(scene.Random.Next())));
            default:
                return null;
        }
    }
}
