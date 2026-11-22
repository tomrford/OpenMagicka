using System.Numerics;
using OpenMagicka.Gameplay.Entities;
using OpenMagicka.Physics;

namespace OpenMagicka.Gameplay.Bosses;

/// <summary>
/// A boss's solid body (a serpent's spine, docs/gameplay/BOSSES.md §1): a chain of capsules, each an immovable body
/// laid from one point of the chain towards the next with the length it was given, that blocks what walks into it
/// and takes no damage. The boss lays it each frame from its bones; while the boss is under the ground it is parked
/// far below the level. It is no target: shots and missiles pass it by.
/// </summary>
public sealed class BossBody : Entity
{
    private readonly PhysicsWorld world;
    private readonly PhysicsBody[] bodies;
    private readonly Vector3[] points;

    /// <param name="radii">Each capsule's radius, from the chain's first point on.</param>
    /// <param name="lengths">Each capsule's length, end to end (its caps included).</param>
    /// <param name="centre">The capsule whose middle is the body's position (what the AI sees of it).</param>
    public BossBody(Boss owner, PhysicsWorld world, IReadOnlyList<float> radii, IReadOnlyList<float> lengths, int centre)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        this.world = world ?? throw new ArgumentNullException(nameof(world));
        ArgumentNullException.ThrowIfNull(radii);
        ArgumentNullException.ThrowIfNull(lengths);
        if (radii.Count == 0 || radii.Count != lengths.Count)
        {
            throw new ArgumentException("a body needs as many lengths as radii, and one capsule at least", nameof(lengths));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(centre);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(centre, radii.Count);
        (Radii, Lengths, Centre) = ([.. radii], [.. lengths], centre);
        points = new Vector3[radii.Count + 1];
        bodies = new PhysicsBody[radii.Count];
        for (var i = 0; i < bodies.Length; i++)
        {
            var cylinder = MathF.Max(Lengths[i] - 2 * Radii[i], 0);
            bodies[i] = world.AddBody(new CapsuleShape(Radii[i], cylinder), BossZone.Parked, 1, BodyOptions.Static);
            (bodies[i].Tag, bodies[i].AffectedByGravity) = (this, false);
        }
        Park();
    }

    public Boss Owner { get; }

    public IReadOnlyList<float> Radii { get; }

    public IReadOnlyList<float> Lengths { get; }

    public int Centre { get; }

    public int Count => bodies.Length;

    /// <summary>In the world, not parked under it.</summary>
    public bool IsPlaced { get; private set; }

    /// <summary>A capsule's axis as laid: from its point towards the next, its length long.</summary>
    public Segment Capsule(int index)
    {
        var (from, to) = (points[index], points[index + 1]);
        var along = to - from;
        var length = along.Length();
        return new Segment(from, length > 1e-6f ? along / length * Lengths[index] : Vector3.Zero);
    }

    /// <summary>Where the centre capsule starts (on the ground, as the original's: at a height of 1).</summary>
    public override Vector3 Position => Capsule(Centre).Origin with { Y = 1 };

    public override float Radius => Radii[Centre];

    public override bool IsDead => Owner.IsDead;

    public override bool IsRemovable => Owner.IsRemovable || Owner.Manager is null;

    /// <summary>Lays the chain through the points (one more than the capsules); not during a physics step.</summary>
    public void Place(ReadOnlySpan<Vector3> chain)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(chain.Length, points.Length, nameof(chain));
        chain.CopyTo(points);
        IsPlaced = true;
        Lay();
    }

    /// <summary>The chain moves far below the level (its shape kept).</summary>
    public void Park()
    {
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = BossZone.Parked + new Vector3(0, 0, i);
        }
        IsPlaced = false;
        Lay();
    }

    private void Lay()
    {
        if (world.IsDisposed || world.IsStepping)
        {
            return;
        }
        for (var i = 0; i < bodies.Length; i++)
        {
            if (bodies[i].IsRemoved)
            {
                continue;
            }
            var capsule = Capsule(i);
            var axis = capsule.Delta.LengthSquared() > 1e-12f ? Vector3.Normalize(capsule.Delta) : Vector3.UnitY;
            bodies[i].Position = capsule.At(0.5f);
            bodies[i].Orientation = Turn(Vector3.UnitY, axis);
        }
    }

    /// <summary>The shortest turn taking one unit vector onto another.</summary>
    private static Quaternion Turn(Vector3 from, Vector3 to)
    {
        var dot = Vector3.Dot(from, to);
        if (dot < -0.99999f)
        {
            return Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        }
        var axis = Vector3.Cross(from, to);
        return Quaternion.Normalize(new Quaternion(axis, 1 + dot));
    }

    /// <summary>
    /// Whether an entity, by its own shape, touches any capsule as laid (within <paramref name="slack"/> more), and where on
    /// the entity (the capsule nearest it); a parked body touches nothing.
    /// </summary>
    public bool Touches(Entity entity, float slack, out Vector3 at)
    {
        ArgumentNullException.ThrowIfNull(entity);
        at = default;
        if (!IsPlaced)
        {
            return false;
        }
        var best = float.MaxValue;
        for (var i = 0; i < bodies.Length; i++)
        {
            var capsule = Capsule(i);
            if (!entity.SegmentIntersect(capsule, Radii[i] + slack, out var hit))
            {
                continue;
            }
            var squared = Segment.DistanceSquared(capsule, new Segment(hit, Vector3.Zero), out _, out _);
            if (squared < best)
            {
                (best, at) = (squared, hit);
            }
        }
        return best < float.MaxValue;
    }

    /// <summary>The middle of the chain as laid, and how far from it its farthest point is (where to look for what it touches).</summary>
    public (Vector3 Centre, float Reach) Bounds()
    {
        var centre = Vector3.Zero;
        foreach (var point in points)
        {
            centre += point;
        }
        centre /= points.Length;
        var reach = 0f;
        foreach (var point in points)
        {
            reach = MathF.Max(reach, Vector3.Distance(point, centre));
        }
        return (centre, reach + Radii.Max());
    }

    /// <summary>It is no target of shots or blows: they find it by its bodies, if at all.</summary>
    public override bool SegmentIntersect(Segment segment, float radius, out Vector3 hit)
    {
        hit = default;
        return false;
    }

    public override float? Entry(Segment segment) => null;

    public override bool ArcIntersect(Vector3 origin, Vector3 direction, float range, float angle, float heightTolerance, out Vector3 hit)
    {
        hit = default;
        return false;
    }

    protected internal override IEnumerable<PhysicsBody> Bodies => bodies;

    protected internal override void OnRemoved()
    {
        foreach (var body in bodies)
        {
            if (!body.IsRemoved && !world.IsDisposed)
            {
                world.Remove(body);
            }
        }
    }
}
