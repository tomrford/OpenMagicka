using System.Numerics;
using OpenMagicka.Engine.Characters;
using OpenMagicka.Gameplay.Bosses;
using OpenMagicka.Gameplay.Entities;

namespace OpenMagicka.Engine.Scripting;

/// <summary>
/// A named boss of OpenMagicka's own code in a fight (docs/gameplay/BOSSES.md §1): each <see cref="Enter"/> makes its
/// world entity anew at the place (the one before leaves), as the original's spawn and checkpoint replay set its boss up
/// whole again; the fight's frame tells it whether the fight runs. Only the host makes it; a level that cannot take it
/// (full, or stepping) keeps the one it had. Its hit points and death are its entity's; with none it is not dead.
/// </summary>
public sealed class NamedBoss(string name, Func<ScriptScene, Matrix4x4, Boss> make) : IFightBoss
{
    private readonly Func<ScriptScene, Matrix4x4, Boss> make = make ?? throw new ArgumentNullException(nameof(make));

    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    /// <summary>The boss in the world now, if any.</summary>
    public Boss? Boss { get; private set; }

    public float HitPoints => Boss?.HitPoints ?? 0;

    public float MaxHitPoints => Boss?.MaxHitPoints ?? 0;

    public bool IsDead => Boss is { IsDead: true };

    public void Enter(ScriptScene scene, Matrix4x4 at)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var manager = scene.World.Entities;
        if (!scene.IsHost || scene.World.Physics.IsStepping)
        {
            return;
        }
        Boss?.Withdraw();
        Boss = null;
        var boss = make(scene, at);
        if (manager.Entities.Count + 1 + boss.Parts.Count > EntityManager.Capacity)
        {
            Console.Error.WriteLine($"boss: the level is full; '{Name}' cannot come in");
            manager.Discard(boss);
            return;
        }
        manager.Add(boss);
        Boss = boss;
    }

    public void Tick(bool fightRunning)
    {
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
    public static IReadOnlyList<string> Names { get; } = [];

    /// <summary>A named boss for a fight, its model read through <paramref name="models"/>; null for one not made, or whose model the game lacks.</summary>
    public static NamedBoss? Make(string name, Func<string, AnimatedModel?> models)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(models);
        return name.ToLowerInvariant() switch
        {
            _ => null,
        };
    }
}
