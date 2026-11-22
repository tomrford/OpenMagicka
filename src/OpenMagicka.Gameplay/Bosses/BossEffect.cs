using System.Numerics;
using OpenMagicka.Gameplay.Animation;

namespace OpenMagicka.Gameplay.Bosses;

/// <summary>
/// One of a boss's own effects that it starts, moves and stops by name (a tremor where its body meets the ground): the
/// engine follows it while it is on. Starting it while on moves it instead.
/// </summary>
public sealed class BossEffect(Boss owner, string effect)
{
    private readonly Boss owner = owner ?? throw new ArgumentNullException(nameof(owner));

    /// <summary>The boss whose it is: gone with it, it is off.</summary>
    public Boss Owner => owner;

    public string Effect { get; } = effect ?? throw new ArgumentNullException(nameof(effect));

    /// <summary>Whether it shows: started, not stopped, and its boss still in the world.</summary>
    public bool IsOn => on && owner.Manager is not null && !owner.IsRemovable;

    private bool on;

    public Vector3 Position { get; private set; }

    public Vector3 Direction { get; private set; } = Vector3.UnitX;

    /// <summary>On at a place (or moved there when on already).</summary>
    public void Start(Vector3 at, Vector3? direction = null)
    {
        (Position, Direction) = (at, direction ?? Vector3.UnitX);
        if (!IsOn)
        {
            on = true;
            owner.Manager?.RaiseCue(owner, new ItemEffectCue(Effect, at, Direction, Follow: true, this));
        }
    }

    public void Stop()
    {
        if (on)
        {
            on = false;
            owner.Manager?.RaiseCue(owner, new ItemEffectEndCue(this));
        }
    }
}
