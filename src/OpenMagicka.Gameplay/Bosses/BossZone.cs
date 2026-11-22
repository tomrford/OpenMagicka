using System.Numerics;
using OpenMagicka.Gameplay.Combat;
using OpenMagicka.Gameplay.Entities;
using OpenMagicka.Physics;

namespace OpenMagicka.Gameplay.Bosses;

/// <summary>
/// A boss's sphere that takes its damage (a head, docs/gameplay/BOSSES.md §1): an entity the spells and blows find as
/// they find a character, which hands each hit to its boss with its part's number. Its body only marks it for the
/// missiles: it pushes nothing. While its boss is under the ground it is parked far below the level and takes no hits.
/// </summary>
public sealed class BossZone : Entity
{
    /// <summary>Where a hidden zone waits, as the original's: far below any level.</summary>
    public static readonly Vector3 Parked = new(0, -100, 0);

    private readonly PhysicsWorld world;
    private Vector3 position = Parked;

    public BossZone(Boss owner, PhysicsWorld world, int part, float radius)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.world = world ?? throw new ArgumentNullException(nameof(world));
        Part = part;
        Radius = radius > 0 && float.IsFinite(radius) ? radius : throw new ArgumentOutOfRangeException(nameof(radius), radius, "must be positive");
        Body = world.AddBody(new SphereShape(radius), Parked, 1, BodyOptions.Static);
        (Body.Tag, Body.AffectedByGravity, Body.Listener) = (this, false, PassListener.Instance);
    }

    public Boss Owner { get; }

    /// <summary>Which of its boss's parts it is (the original's part index).</summary>
    public int Part { get; }

    public PhysicsBody Body { get; }

    public override Vector3 Position => position;

    public override float Radius { get; }

    public override bool IsDead => Owner.IsDead;

    public override bool IsRemovable => Owner.IsRemovable || Owner.Manager is null;

    /// <summary>It can be hurt while it is in the world.</summary>
    public override bool IsDamageable => IsPlaced;

    /// <summary>In the world, not parked under it.</summary>
    public bool IsPlaced { get; private set; }

    /// <summary>The zone moves to a place in the world (with its body, not during a physics step).</summary>
    public void Place(Vector3 at)
    {
        position = at;
        IsPlaced = true;
        Move(at);
    }

    /// <summary>The zone leaves the world for its parking place.</summary>
    public void Park()
    {
        position = Parked;
        IsPlaced = false;
        Move(Parked);
    }

    private void Move(Vector3 at)
    {
        if (!Body.IsRemoved && !world.IsDisposed && !world.IsStepping)
        {
            Body.Position = at;
        }
    }

    public override DamageResult Damage(DamageEntry damage, Entity? attacker, Vector3 attackPosition) =>
        IsPlaced ? Owner.DamageZone(this, damage, attacker, attackPosition) : DamageResult.None;

    protected internal override IEnumerable<PhysicsBody> Bodies => [Body];

    protected internal override void OnRemoved()
    {
        if (!Body.IsRemoved && !world.IsDisposed)
        {
            world.Remove(Body);
        }
    }

    /// <summary>Nothing is pushed by the zone, nor pushes it.</summary>
    private sealed class PassListener : IContactListener
    {
        public static readonly PassListener Instance = new();

        public ContactResponse OnContact(PhysicsBody body, in Contact contact) => ContactResponse.Ignore;
    }
}
