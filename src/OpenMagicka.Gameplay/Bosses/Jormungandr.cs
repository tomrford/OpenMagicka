using System.Numerics;
using OpenMagicka.Gameplay.Animation;
using OpenMagicka.Gameplay.Characters;
using OpenMagicka.Gameplay.Combat;
using OpenMagicka.Gameplay.Entities;
using OpenMagicka.Gameplay.Spells;
using OpenMagicka.Physics;

namespace OpenMagicka.Gameplay.Bosses;

/// <summary>
/// Jörmungandr, the serpent that ends chapter 2 (docs/gameplay/BOSSES.md §2): it sleeps until its fight starts, wakes,
/// then digs under the ground and either bursts up under a player to bite and spit, or leaps over one in an arc. Only its
/// head takes damage; its spine is a solid chain of capsules that knocks back what it touches. The warning before each
/// move shortens as it loses hit points. It is posed from its model's bones each frame and dies once its death clip ends.
/// </summary>
public sealed partial class Jormungandr : Boss
{
    /// <summary>Its model (its skeleton and clips).</summary>
    public const string Asset = "models/bosses/jormungandr/jormungandr";

    public const float FullHealth = 10000;

    /// <summary>One blow taking this much staggers it.</summary>
    public const float HitTolerance = 250;

    /// <summary>What it loses ramming a shield or a solid barrier.</summary>
    public const float RamDamage = 750;

    /// <summary>The warning before each move: this long at full health, the other near death.</summary>
    public const float LongWarning = 2.5f, ShortWarning = 0.75f;

    public const float HeadRadius = 1.1f, HeadLead = 0.6f;

    /// <summary>The head's part number (the only one that takes damage).</summary>
    public const int HeadPart = 1;

    /// <summary>The spine's points, tail to neck (each capsule laid from one towards the one before).</summary>
    public static readonly IReadOnlyList<string> SpineBones =
        [.. Enumerable.Range(1, 11).Reverse().Select(i => $"back{i:00}"), "center", .. Enumerable.Range(1, 12).Select(i => $"front{i:00}"), "NeckBase", "NeckMid"];

    /// <summary>Where along the spine its body counts as being (the capsule from front05).</summary>
    private const int SpineCentre = 16;

    private readonly BossPose pose;
    private readonly int head;
    private readonly int[] spine;
    private readonly Vector3[] chain;
    private readonly HitMemory headHits = new(), spineHits = new();
    private readonly BossEffect riseTremor, splash, frontTremor, backTremor, spray;

    /// <param name="at">Where it lies (a locator's frame: its place and the way it faces).</param>
    public Jormungandr(PhysicsWorld world, BossPose pose, Matrix4x4 at, Random random)
        : base(world, new BossVitals(FullHealth, ResistanceTable.Neutral.With(Elements.Earth, 0.75f).With(Elements.Ice, 0.75f)), random)
    {
        this.pose = pose ?? throw new ArgumentNullException(nameof(pose));
        foreach (var clip in Clips.All)
        {
            pose.Clip(clip);                                                  // a model without them is not its: before any body is made
        }
        head = pose.Bone("HeadBase");
        spine = [.. SpineBones.Select(pose.Bone)];
        chain = new Vector3[spine.Length];
        var radii = Enumerable.Range(0, spine.Length - 1).Select(k => MathF.Min(0.333f + 0.2f * k, 1)).ToArray();
        var lengths = Enumerable.Range(0, spine.Length - 1)
            .Select(k => MathF.Max(Vector3.Distance(pose.BindPosition(spine[k]), pose.BindPosition(spine[k + 1])), 0.01f)).ToArray();
        Head = Part(new BossZone(this, world, HeadPart, HeadRadius));
        Spine = Part(new BossBody(this, world, [.. radii.Reverse()], [.. lengths.Reverse()], spine.Length - 2 - SpineCentre));
        (riseTremor, splash, frontTremor, backTremor, spray) = (new(this, Effects.Tremor), new(this, Effects.Splash), new(this, Effects.Tremor),
            new(this, Effects.Tremor), new(this, Effects.Spray));
        (orientation, position, direction) = (at, at.Translation, Forward(at));
        Change(States.Sleep);
    }

    /// <summary>The clips it plays, as its model names them.</summary>
    public static class Clips
    {
        public const string Intro = "intro", Leap = "dive_attack", Recoil = "dive_recoil", Emerge = "emerge", Submerge = "submerge", Idle = "idle",
            Venom = "venom", Bite = "attack", Hit = "hit", DieBelow = "die", DieAbove = "die_above";

        public static IReadOnlyList<string> All { get; } = [Intro, Leap, Recoil, Emerge, Submerge, Idle, Venom, Bite, Hit, DieBelow, DieAbove];
    }

    /// <summary>Its sounds (the Characters bank).</summary>
    public static class Sounds
    {
        public const string Death = "boss_jormungandr_death", Wakeup = "boss_jormungandr_wakeup", Dive = "boss_jormungandr_dive",
            Unburrow = "boss_jormungandr_unburrow", Leap = "boss_jormungandr_leap", Rise = "boss_jormungandr_rise", Spit = "boss_jormungandr_spit",
            Bite = "boss_jormungandr_bite", Pain = "boss_jormungandr_pain", PreSpit = "boss_jormungandr_prespit", PreBite = "boss_jormungandr_prebite",
            LeapDive = "boss_jormungandr_leapdive", HitObject = "boss_jormungandr_hitobject", Whip = "boss_jormungandr_whip";
    }

    /// <summary>Its effects.</summary>
    public static class Effects
    {
        public const string Splash = "jormungandr_dirt_splash", Tremor = "jormungandr_dirt_tremor", SplashTremor = "jormungandr_dirt_splash_tremor",
            Spray = "jormungandr_spit_spray", SpitSplash = "jormungandr_spit_splash", SpitTrail = "jormungandr_spit_projectile",
            BarrierDeath = "barrier_earth_death", Blood = "gore_splash_regular";
    }

    /// <summary>The head: the one part that takes damage, and deals the contact damage of its moves.</summary>
    public BossZone Head { get; }

    /// <summary>The body from the tail to the neck: solid, it knocks back what it touches.</summary>
    public BossBody Spine { get; }

    public override string Model => Asset;

    public override bool IsVisible => drawn;

    public override Matrix4x4 RootTransform => pose.Root;

    public override ReadOnlySpan<Matrix4x4> BoneTransforms => pose.Skinned;

    /// <summary>Where it is: its root (the head's last place, after a leap).</summary>
    public override Vector3 Position => position;

    public override float Radius => HeadRadius;

    /// <summary>Its numbers float over its head.</summary>
    public override Vector3 NumbersAt => Head.Position + Vector3.UnitY * 1.5f;

    /// <summary>Dead once its death clip has played out (not at no hit points).</summary>
    public override bool IsDead => dead;

    /// <summary>The warning before each move now: from 2.5 s at full health down to 0.75 s near death.</summary>
    public float Warning => ShortWarning + (LongWarning - ShortWarning) * Vitals.HitPoints / Vitals.MaxHitPoints;

    /// <summary>The bites left in this surfacing.</summary>
    public int BitesLeft => bites;

    /// <summary>The wizard it means to strike.</summary>
    public Character? Target => target;

    public AnimationClock Clock => pose.Clock;

    /// <summary>Its root's frame: its place and the way it faces (pitched as it leaps).</summary>
    public Matrix4x4 Orientation => orientation;

    private Matrix4x4 orientation;
    private Vector3 position, direction;
    private Character? target;
    private bool dead, drawn, animating, staggered, midLeap, dirtSpawned, noGroundEffects;
    private float timer, deadTimer, stateSpeed = 1;
    private int bites;
    private Contact contact;

    /// <summary>What the head does to what it touches now.</summary>
    private enum Contact : byte { None, Leap, Emerge, Bite }

    protected override void Think(float dt)
    {
        if (State == States.Sleep && FightStarted)
        {
            Change(States.Intro);
        }
        CheckStagger();
        Run(dt);
        var frozen = Vitals.Has(StatusEffects.Frozen);
        Clock.Speed = frozen ? 0 : stateSpeed * (Vitals.Has(StatusEffects.Cold) ? 0.5f : 1);
        if (dead)
        {
            if ((deadTimer += dt) > 1)
            {
                headHits.Forget();
                spineHits.Forget();
            }
        }
        else
        {
            headHits.Update(dt);
            spineHits.Update(dt);
        }
        PlaceParts(dt);
        Tremors();
        Vitals.Update(dt, dead);
        TouchGround();
        HeadContacts();
        SpineContacts();
    }

    /// <summary>Posed and its parts laid on its bones while it plays; parked under the level while it does not.</summary>
    private void PlaceParts(float dt)
    {
        if (!animating)
        {
            Head.Park();
            Spine.Park();
            return;
        }
        Clock.Update(dt);
        pose.Place(orientation);
        for (var i = 0; i < spine.Length; i++)
        {
            chain[spine.Length - 1 - i] = pose.Position(spine[i]);             // neck first: each capsule from a bone back towards the one before
        }
        Spine.Place(chain);
        Head.Place(pose.Position(head) + Forward(orientation) * HeadLead);
    }

    /// <summary>
    /// A hit on the head: ignored with nothing in it, while it is hidden or dead, or as it dives back under; else by the
    /// bosses' rules, a blow that lands makes it flash, and one taking 250 or more staggers it. Its blood shows where it
    /// was struck.
    /// </summary>
    protected internal override DamageResult DamageZone(BossZone zone, DamageEntry damage, Entity? attacker, Vector3 attackPosition)
    {
        if ((damage.Amount == 0 && damage.Magnitude == 0) || !drawn || dead || zone.Part != HeadPart || Clock.Clip?.Name == Clips.Submerge)
        {
            return DamageResult.None;
        }
        var before = Vitals.HitPoints;
        var hit = Vitals.Damage(damage);
        if ((hit.Result & (DamageResult.Hit | DamageResult.Damaged | DamageResult.Killed)) != 0 && !hit.Result.HasFlag(DamageResult.Healed))
        {
            Flashes();
            if (before - Vitals.HitPoints >= HitTolerance)
            {
                staggered = true;
            }
        }
        if (hit.Bleeds && BloodAndGore)
        {
            RaiseCue(new WorldEffectCue(Effects.Blood, attackPosition, Vector3.UnitX));
        }
        return hit.Result;
    }

    /// <summary>A sound of the Characters bank: at the head, or (as most of its are) heard alike everywhere.</summary>
    private void Sound(string cue, bool positional = false) =>
        RaiseCue(new SoundCue(cue, SoundBank.Characters, Head.Position, direction) { Flat = !positional });

    private void Shake(float magnitude, float duration) => RaiseCue(new CameraShakeCue(Head.IsPlaced ? Head.Position : position, magnitude, duration));

    /// <summary>A shake of a random strength between 0.5 and 1.</summary>
    private float ShakeStrength() => 0.5f + 0.5f * Random.NextSingle();

    /// <summary>The way a frame faces (XNA's forward, −Z).</summary>
    public static Vector3 Forward(Matrix4x4 m) => new(-m.M31, -m.M32, -m.M33);

    /// <summary>A frame at a place facing a way, upright as far as it can be.</summary>
    public static Matrix4x4 Facing(Vector3 at, Vector3 forward)
    {
        var right = Vector3.Cross(forward, Vector3.UnitY);
        right = right.LengthSquared() > 1e-12f ? Vector3.Normalize(right) : Vector3.UnitX;
        var up = Vector3.Cross(right, forward);
        return new Matrix4x4(
            right.X, right.Y, right.Z, 0,
            up.X, up.Y, up.Z, 0,
            -forward.X, -forward.Y, -forward.Z, 0,
            at.X, at.Y, at.Z, 1);
    }
}
