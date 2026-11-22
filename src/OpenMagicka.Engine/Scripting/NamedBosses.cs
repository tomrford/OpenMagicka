using System.Numerics;
using OpenMagicka.Engine.Characters;
using OpenMagicka.Gameplay.Bosses;
using OpenMagicka.Gameplay.Entities;

namespace OpenMagicka.Engine.Scripting;

/// <summary>
/// A named boss of OpenMagicka's own code in a fight (docs/gameplay/BOSSES.md §1): each <see cref="Enter"/> makes its
/// world entity anew at the place (the one before leaves once the new one is in), as the original's spawn and checkpoint
/// replay set its boss up whole again; the fight's frame tells it whether the fight runs, and the fight's end takes it
/// out. Only the host makes it. A level that has no room for a new one (or is stepping) keeps the one it had; with none,
/// the fight has none of it, which counts as dead, as a generic boss the level could not make. Its hit points and death
/// are its entity's.
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

    public bool IsDead => Boss is null or { IsDead: true };

    public void Enter(ScriptScene scene, Matrix4x4 at)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var manager = scene.World.Entities;
        if (!scene.IsHost || scene.World.Physics.IsStepping || manager.IsBusy)
        {
            return;
        }
        if (Boss is { Manager: null } or { IsRemovable: true })
        {
            Boss = null;                                                      // gone with the world (a checkpoint's restart)
        }
        if (manager.Entities.Count + size > EntityManager.Capacity)
        {
            Console.Error.WriteLine($"boss: the level is full; '{Name}' cannot come in anew");
            return;
        }
        var boss = make(scene, at);
        try
        {
            manager.Add(boss);
        }
        catch (InvalidOperationException e)
        {
            Console.Error.WriteLine($"boss: '{Name}' cannot come in: {e.Message}");
            manager.Discard(boss);
            return;
        }
        Boss?.Withdraw();
        Boss = boss;
    }

    public void Tick(bool fightRunning)
    {
        if (Boss is { } boss)
        {
            boss.FightStarted = fightRunning;
        }
    }

    /// <summary>The fight is over (endBossFight, a checkpoint's restart): the boss leaves the world, as the original's is no longer run nor drawn.</summary>
    public void Leave()
    {
        Boss?.Withdraw();
        Boss = null;
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
