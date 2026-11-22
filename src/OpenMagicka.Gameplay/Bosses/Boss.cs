using System.Numerics;
using OpenMagicka.Gameplay.Combat;
using OpenMagicka.Gameplay.Entities;
using OpenMagicka.Physics;

namespace OpenMagicka.Gameplay.Bosses;

/// <summary>
/// A named boss in the world (docs/gameplay/BOSSES.md §1): one entity that runs its own state machine on the world's
/// time from the moment it is added, whether or not its fight runs yet (the fight says so through
/// <see cref="FightStarted"/>), and owns its parts: the zones that take its damage and the bodies that block. It is
/// drawn as one skinned model. Its parts come into the manager with it and leave with it; <see cref="Withdraw"/>
/// takes it all out at the end of the frame. Host and client run the same states; only the host decides (OM-42).
/// </summary>
public abstract class Boss : Entity
{
    private readonly List<Entity> parts = [];
    private bool withdrawn;

    protected Boss(PhysicsWorld world, BossVitals vitals, Random random)
    {
        World = world ?? throw new ArgumentNullException(nameof(world));
        Vitals = vitals ?? throw new ArgumentNullException(nameof(vitals));
        Random = random ?? throw new ArgumentNullException(nameof(random));
    }

    protected PhysicsWorld World { get; }

    /// <summary>The boss's dice: one source, the host's.</summary>
    protected Random Random { get; }

    public BossVitals Vitals { get; }

    public float HitPoints => Vitals.HitPoints;

    public float MaxHitPoints => Vitals.MaxHitPoints;

    /// <summary>Whether the fight runs (the fight sets it each frame, after the scripts).</summary>
    public bool FightStarted { get; set; }

    /// <summary>Only the host chooses, rolls and spawns; a client plays what it is told (OM-42).</summary>
    public bool IsHost { get; set; } = true;

    /// <summary>The model drawn for it (an asset, as a level names it).</summary>
    public abstract string Model { get; }

    /// <summary>Whether it is drawn and its parts are in the world now (not under the ground).</summary>
    public abstract bool IsVisible { get; }

    /// <summary>Its model's place in the world: the root its bones are posed from.</summary>
    public abstract Matrix4x4 RootTransform { get; }

    /// <summary>Each bone's skinning transform this frame, in the world.</summary>
    public abstract ReadOnlySpan<Matrix4x4> BoneTransforms { get; }

    /// <summary>The white flash of a blow, 1 fading to 0.</summary>
    public float Flash => MathF.Max(flashTimer, 0) / FlashTime;

    private float flashTimer;

    /// <summary>How long a blow's flash lasts.</summary>
    public const float FlashTime = 0.1f;

    /// <summary>How hurt it looks (its wound map's amount): the share of its hit points gone.</summary>
    public float Wounds => Math.Clamp(1 - Vitals.HitPoints / Vitals.MaxHitPoints, 0, 1);

    public override bool IsRemovable => withdrawn;

    /// <summary>Its zones and bodies: entities of their own, in the manager while it is.</summary>
    public IReadOnlyList<Entity> Parts => parts;

    /// <summary>A part to add with it (made before it is added).</summary>
    protected T Part<T>(T part) where T : Entity
    {
        parts.Add(part);
        return part;
    }

    /// <summary>It leaves the world at the end of the frame, with its parts (its fight ended, or a new one is made).</summary>
    public void Withdraw() => withdrawn = true;

    /// <summary>
    /// A hit on one of its zones (<paramref name="zone"/>): the boss decides whether it takes it at all, then by the rules
    /// of <see cref="BossVitals"/>. Ignored by default.
    /// </summary>
    protected internal virtual DamageResult DamageZone(BossZone zone, DamageEntry damage, Entity? attacker, Vector3 attackPosition) => DamageResult.None;

    /// <summary>A blow landed: the boss flashes.</summary>
    protected void Flashes() => flashTimer = FlashTime;

    /// <summary>The flash fades; then the boss's own frame.</summary>
    public sealed override void Update(float dt)
    {
        flashTimer = MathF.Max(flashTimer - dt, 0);
        Think(dt);
    }

    /// <summary>One frame of the boss's own logic.</summary>
    protected abstract void Think(float dt);

    /// <summary>Raises a presentation cue for the engine.</summary>
    protected void RaiseCue(Animation.AnimationCue cue) => Manager?.RaiseCue(this, cue);

    protected internal override void OnAdded()
    {
        foreach (var part in parts)
        {
            Manager!.Add(part);
        }
    }

    /// <summary>It is let go: so are the parts that never came in with it (a boss the level could not take).</summary>
    protected internal override void OnRemoved()
    {
        withdrawn = true;
        foreach (var part in parts.Where(p => p.Manager is null))
        {
            part.OnRemoved();
        }
    }

    protected internal override IEnumerable<PhysicsBody> Bodies => parts.SelectMany(p => p.Bodies);
}
