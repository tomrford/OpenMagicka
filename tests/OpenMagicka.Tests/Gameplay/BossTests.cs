using System.Numerics;
using OpenMagicka.Gameplay;
using OpenMagicka.Gameplay.Bosses;
using OpenMagicka.Gameplay.Combat;
using OpenMagicka.Gameplay.Entities;
using OpenMagicka.Gameplay.Spells;
using OpenMagicka.Physics;
using static OpenMagicka.Tests.Gameplay.CharacterTests;

namespace OpenMagicka.Tests.Gameplay;

/// <summary>What every named boss is built from (BOSSES.md §1): its vitals, its damage zones, its solid body, its parts' lifetime.</summary>
public class BossTests
{
    /// <summary>A boss with a head and a body of two capsules along X, above the ground or not as a test says.</summary>
    internal sealed class Dummy : Boss
    {
        public Dummy(PhysicsWorld world, ResistanceTable? resistances = null)
            : base(world, new BossVitals(1000, resistances ?? ResistanceTable.Neutral), new Random(1))
        {
            Head = Part(new BossZone(this, world, 1, 1));
            Spine = Part(new BossBody(this, world, [0.5f, 0.5f], [2, 2], 0));
        }

        public BossZone Head { get; }

        public BossBody Spine { get; }

        public bool Up { get; set; }

        public int Frames { get; private set; }

        public List<(int Part, DamageEntry Damage)> Hits { get; } = [];

        public override string Model => "models/bosses/dummy";

        public override bool IsVisible => Up;

        public override Matrix4x4 RootTransform => Matrix4x4.Identity;

        public override ReadOnlySpan<Matrix4x4> BoneTransforms => [];

        public override Vector3 Position => Vector3.Zero;

        public override float Radius => 1;

        public override bool IsDead => Vitals.HitPoints <= 0;

        public void Show(Vector3 head)
        {
            Up = true;
            Head.Place(head);
            Spine.Place([new Vector3(0, 0.5f, 3), new Vector3(2, 0.5f, 3), new Vector3(4, 0.5f, 3)]);
        }

        protected override void Think(float dt)
        {
            Frames++;
            Vitals.Update(dt, IsDead);
        }

        protected override DamageResult DamageZone(BossZone zone, DamageEntry damage, Entity? attacker, Vector3 attackPosition)
        {
            Hits.Add((zone.Part, damage));
            var hit = Vitals.Damage(damage);
            if ((hit.Result & (DamageResult.Damaged | DamageResult.Killed)) != 0)
            {
                Flashes();
            }
            return hit.Result;
        }
    }

    private static DamageEntry Blow(Elements element, float amount, float magnitude = 1, AttackProperties attack = AttackProperties.Damage) =>
        new(attack, element, amount, magnitude);

    [Fact]
    public void AHit_IsShiftedByTheModifiers_AndScaledByTheMeanMultiplier()
    {
        var vitals = new BossVitals(10000, ResistanceTable.Neutral.With(Elements.Earth, 0.75f).With(Elements.Ice, 0.75f).With(Elements.Fire, 1, modifier: 10.7f));

        Assert.Equal(DamageResult.Damaged | DamageResult.Hit, vitals.Damage(Blow(Elements.Earth, 400)).Result);
        Assert.Equal(10000 - 300, vitals.HitPoints, 3);                                           // earth does 75%
        vitals.Damage(Blow(Elements.Earth | Elements.Lightning, 100, 2));
        Assert.Equal(10000 - 300 - 175, vitals.HitPoints, 3);                                     // the mean of 0.75 and 1
        vitals.Damage(Blow(Elements.Fire, 100));
        Assert.Equal(10000 - 300 - 175 - 110, vitals.HitPoints, 3);                               // +10 (a modifier counts whole)
        var deflected = new BossVitals(100, ResistanceTable.Neutral.With(Elements.Arcane, 0));
        Assert.Equal(DamageResult.Deflected, deflected.Damage(Blow(Elements.Arcane, 50)).Result);
        Assert.Equal(100, deflected.HitPoints);
    }

    [Fact]
    public void ColdOnTheWet_Freezes_AndAFrozenBossTakesPhysicalBlowsPastItsArmourThrice()
    {
        var vitals = new BossVitals(10000, ResistanceTable.Neutral);

        vitals.Damage(Blow(Elements.Water, 0, 1, AttackProperties.Status));
        vitals.Update(Tick, dead: false);
        Assert.True(vitals.Has(StatusEffects.Wet));
        vitals.Damage(Blow(Elements.Cold, 0, 1, AttackProperties.Status));
        vitals.Update(Tick, dead: false);
        Assert.True(vitals.Has(StatusEffects.Frozen));
        Assert.False(vitals.Has(StatusEffects.Wet));

        var blow = vitals.Damage(Blow(Elements.Earth, 500, 0.5f));
        Assert.Equal((500 - 200) * 3, blow.Dealt, 3);                                             // and a magnitude of at least 1
        Assert.False(blow.Bleeds);                                                                // the frozen do not bleed
        Assert.True(vitals.Damage(Blow(Elements.Earth, 100)) is { Dealt: 0, Result: var r } && r.HasFlag(DamageResult.Deflected));
    }

    [Fact]
    public void Lightning_HurtsTheWetTwice_FireTheGreasedTwice_AndColdPutsOutFire()
    {
        var vitals = new BossVitals(10000, ResistanceTable.Neutral);
        vitals.Damage(Blow(Elements.Water, 0, 1, AttackProperties.Status));
        vitals.Update(Tick, dead: false);
        Assert.Equal(200, vitals.Damage(Blow(Elements.Lightning, 100)).Dealt, 3);

        var greased = new BossVitals(10000, ResistanceTable.Neutral);
        greased.AddStatus(StatusEffect.Create(StatusEffects.Greased, 0, 1));
        greased.Update(Tick, dead: false);
        Assert.Equal(200, greased.Hurt(100, Elements.Fire), 3);

        var burning = new BossVitals(10000, ResistanceTable.Neutral);
        burning.Damage(Blow(Elements.Fire, 50, 1, AttackProperties.Status));
        Assert.True(burning.Statuses[StatusEffects.Burning].IsActive);
        Assert.Equal(DamageResult.StatusRemoved, burning.Damage(Blow(Elements.Cold, 0, 1, AttackProperties.Status)).Result & ~DamageResult.Deflected);
        Assert.False(burning.Statuses[StatusEffects.Burning].IsActive);
        Assert.True(burning.Statuses.IsDrying);
    }

    [Fact]
    public void Statuses_BurnTheHitPoints_AndADeadBossKeepsNone()
    {
        var vitals = new BossVitals(10000, ResistanceTable.Neutral);
        vitals.Damage(Blow(Elements.Poison, 60, 1, AttackProperties.Status));
        for (var i = 0; i < 60; i++)
        {
            vitals.Update(Tick, dead: false);
        }
        Assert.InRange(vitals.HitPoints, 10000 - 61, 10000 - 59);
        vitals.Update(Tick, dead: true);
        Assert.Equal(StatusEffects.None, vitals.Statuses.Active);
        vitals.Reset();
        Assert.Equal(10000, vitals.HitPoints);
    }

    [Fact]
    public void ABoss_BringsItsPartsIn_AndTakesThemOutWhenItIsWithdrawn()
    {
        using var world = World();
        var boss = new Dummy(world.Physics);

        world.Entities.Add(boss);
        Assert.Contains(boss.Head, world.Entities.Entities);
        Assert.Contains(boss.Spine, world.Entities.Entities);
        world.Tick(Tick);
        Assert.Equal(1, boss.Frames);

        boss.Withdraw();
        world.Tick(Tick);
        Assert.Empty(world.Entities.Entities);
        Assert.True(boss.Head.Body.IsRemoved);
        Assert.Null(world.Physics.CastSegment(new Vector3(1, 5, 3), new Vector3(1, -5, 3))?.Body);   // the body went with it: the floor
    }

    [Fact]
    public void AParkedZone_TakesNoHits_AndAPlacedOneHandsThemToItsBoss()
    {
        using var world = World();
        var boss = new Dummy(world.Physics);
        world.Entities.Add(boss);
        world.Tick(Tick);

        Assert.Equal(DamageResult.None, boss.Head.Damage(Blow(Elements.Earth, 100), null, Vector3.Zero));
        Assert.False(boss.Head.IsDamageable);
        Assert.DoesNotContain(boss.Head, world.Entities.Query(Vector3.Zero, 5));

        boss.Show(new Vector3(0, 1, 0));
        world.Tick(Tick);
        Assert.Contains(boss.Head, world.Entities.Query(Vector3.Zero, 1, ignoreProtected: true));
        Assert.True(boss.Head.Damage(Blow(Elements.Earth, 100), null, Vector3.Zero).HasFlag(DamageResult.Damaged));
        Assert.Equal(1, Assert.Single(boss.Hits).Part);
        Assert.Equal(900, boss.HitPoints);
        Assert.Equal(1, boss.Flash, 3);
        world.Tick(Boss.FlashTime);
        Assert.Equal(0, boss.Flash, 3);
        Assert.Equal(0.1f, boss.Wounds, 3);
    }

    [Fact]
    public void ABossesBody_IsSolid_WhereItIsLaid_AndTouchesWhatIsNearItsCapsules_ByItsShape()
    {
        using var world = World();
        var boss = new Dummy(world.Physics);
        world.Entities.Add(boss);
        var near = Spawn(world, at: new Vector3(1, 0.9f, 4.0f));
        var far = Spawn(world, at: new Vector3(1, 0.9f, 5));
        Assert.False(boss.Spine.Touches(near, 0.15f, out _));                                       // parked

        boss.Show(new Vector3(0, 1, 0));
        Assert.True(boss.Spine.Touches(near, 0.15f, out var at));
        Assert.Equal(3.6f, at.Z, 1);                                                              // on the goblin, facing the body
        Assert.False(boss.Spine.Touches(far, 0.15f, out _));
        var down = world.Physics.CastSegment(new Vector3(3, 5, 3), new Vector3(3, -5, 3));
        Assert.Equal(1, down?.Position.Y ?? 0, 2);                                                // the second capsule, radius 0.5 over 0.5

        var ball = world.Physics.AddBody(new SphereShape(0.3f), new Vector3(1, 0.5f, 1), 1);
        ball.AffectedByGravity = false;
        for (var i = 0; i < 60; i++)
        {
            ball.Velocity = new Vector3(0, 0, 4);
            world.Tick(Tick);
        }
        Assert.InRange(ball.Position.Z, 1.5f, 2.6f);                                             // stopped at the body, not through it
    }

    [Fact]
    public void ALevelWithoutRoomForItsParts_TakesNeitherTheBossNorThem_AndAddsOnAfter()
    {
        using var world = World();
        for (var i = world.Entities.Entities.Count; i < EntityManager.Capacity - 2; i++)
        {
            world.Entities.Add(new Dummy(world.Physics).Head);                                  // a stand-in entity apiece
        }
        var count = world.Entities.Entities.Count;
        var boss = new Dummy(world.Physics);

        Assert.Throws<InvalidOperationException>(() => world.Entities.Add(boss));
        Assert.Equal(count, world.Entities.Entities.Count);
        Assert.Null(boss.Manager);
        Assert.Null(boss.Head.Manager);
        Assert.NotEqual(NetId.None, world.Entities.Add(Spawn(world)));                          // its id is not reused
    }
}
