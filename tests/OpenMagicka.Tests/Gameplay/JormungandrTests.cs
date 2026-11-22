using System.Numerics;
using OpenMagicka.Gameplay;
using OpenMagicka.Gameplay.Animation;
using OpenMagicka.Gameplay.Bosses;
using OpenMagicka.Gameplay.Characters;
using OpenMagicka.Gameplay.Combat;
using OpenMagicka.Gameplay.Entities;
using OpenMagicka.Gameplay.Spells;
using static OpenMagicka.Tests.Gameplay.CharacterTests;
using States = OpenMagicka.Gameplay.Bosses.Jormungandr.States;

namespace OpenMagicka.Tests.Gameplay;

/// <summary>Jörmungandr (BOSSES.md §2), on a stand-in of its model: a straight spine in its bind pose, its clips with their lengths.</summary>
public class JormungandrTests
{
    /// <summary>Dice that throw what a test says, in order (then 0.5).</summary>
    internal sealed class Dice(params double[] throws) : Random
    {
        private readonly Queue<double> throws = new(throws);

        public override double NextDouble() => throws.TryDequeue(out var value) ? value : 0.5;

        public override float NextSingle() => (float)NextDouble();

        public override int Next(int maxValue) => Math.Min((int)(NextDouble() * maxValue), maxValue - 1);
    }

    /// <summary>
    /// The serpent's spine as its model has it, along Z from the tail behind to the neck ahead at −Z, its head reared up over
    /// its root (as its clips hold it out of the ground), and its clips with no keys.
    /// </summary>
    internal static BossPose Model()
    {
        var bones = new List<SkeletonBone> { new("root", -1, BonePose.Identity, Matrix4x4.Identity) };
        void Bone(string name, float z, float y = 0.19f)
        {
            var at = new Vector3(0, y, z);
            bones.Add(new SkeletonBone(name, 0, BonePose.Identity with { Translation = at }, Matrix4x4.CreateTranslation(-at)));
        }
        var spine = Jormungandr.SpineBones;
        for (var i = 0; i < spine.Count; i++)
        {
            Bone(spine[i], 21.2f - i * (35.4f / (spine.Count - 1)));
        }
        Bone("HeadBase", 0.6f, 1.5f);
        var lengths = new Dictionary<string, float>
        {
            ["intro"] = 10, ["dive_attack"] = 2.667f, ["dive_recoil"] = 2.667f, ["emerge"] = 1.333f, ["submerge"] = 1.333f, ["idle"] = 1.333f,
            ["venom"] = 2.667f, ["attack"] = 2.5f, ["hit"] = 1.333f, ["die"] = 5, ["die_above"] = 3.542f,
        };
        return new BossPose(new Skeleton(bones), lengths.ToDictionary(c => c.Key, c => new AnimationClip(c.Key, c.Value)));
    }

    private sealed class Arena : IDisposable
    {
        public Arena(Random? dice = null, params Vector3[] wizards)
        {
            World = CharacterTests.World();
            World.Entities.Session = new GameSession();
            foreach (var at in wizards.DefaultIfEmpty(new Vector3(0, 0.9f, 0)))
            {
                var player = World.Entities.Session.AddPlayer();
                var avatar = Spawn(World, Goblin with { MaxHitPoints = 100000, Factions = Factions.Player0 }, at);
                (avatar.IsPlayer, avatar.Player, player.Avatar) = (true, player, avatar);
                Wizards.Add(avatar);
            }
            Serpent = new Jormungandr(World.Physics, Model(), Matrix4x4.CreateTranslation(-5, 0, 5), dice ?? new Dice());
            World.Entities.Add(Serpent);
            World.Entities.Cue += (_, cue) => Cues.Add(cue);
        }

        public GameWorld World { get; }

        public Jormungandr Serpent { get; }

        public List<Character> Wizards { get; } = [];

        public List<AnimationCue> Cues { get; } = [];

        public IEnumerable<string> Sounds => Cues.OfType<SoundCue>().Select(c => c.Cue);

        public void Run(float seconds)
        {
            for (var t = 0f; t < seconds - 1e-4f; t += Tick)
            {
                World.Tick(Tick);
            }
        }

        /// <summary>Ticks until the serpent is in a state (at most a minute).</summary>
        public void RunUntil(States state, float limit = 60)
        {
            for (var t = 0f; Serpent.State != state; t += Tick)
            {
                Assert.True(t < limit, $"still {Serpent.State}, not {state}");
                World.Tick(Tick);
            }
        }

        public void Dispose() => World.Dispose();
    }

    private static DamageEntry Blow(float amount) => new(AttackProperties.Damage, Elements.Arcane, amount, 1);

    [Fact]
    public void ItSleeps_DrawnAndStill_UntilItsFightStarts_ThenWakesForTenSeconds_AndGoesUnder()
    {
        using var arena = new Arena();
        var serpent = arena.Serpent;

        arena.Run(3);
        Assert.Equal(States.Sleep, serpent.State);
        Assert.True(serpent.IsVisible);
        Assert.Equal(0, serpent.Clock.Time);
        Assert.True(serpent.Head.IsPlaced);                                                     // asleep but there: a blow lands
        Assert.True(serpent.Head.Damage(Blow(10), null, serpent.Head.Position).HasFlag(DamageResult.Damaged));

        serpent.FightStarted = true;
        arena.Run(Tick);
        Assert.Equal(States.Intro, serpent.State);
        Assert.Contains(Jormungandr.Sounds.Wakeup, arena.Sounds);
        arena.Run(9.5f);
        Assert.Equal(States.Intro, serpent.State);
        Assert.Contains(Jormungandr.Sounds.Dive, arena.Sounds);                                  // 2.2 s in
        arena.Run(0.6f);
        Assert.Equal(States.Underground, serpent.State);
        Assert.False(serpent.IsVisible);
        Assert.False(serpent.Head.IsPlaced);
        Assert.False(serpent.Spine.IsPlaced);
        Assert.Equal(DamageResult.None, serpent.Head.Damage(Blow(500), null, Vector3.Zero));
    }

    [Fact]
    public void UnderTheGround_AfterTheWarning_ItRisesUnderAWizard_HiddenForAnotherWarning_AndStrikesWhatItBurstsUnder()
    {
        using var arena = new Arena(new Dice(0.5, 0.5, 0.9, 0.1), new Vector3(3, 0.9f, 2));    // a seat, the shake, three bites, then a rise
        var (serpent, wizard) = (arena.Serpent, arena.Wizards[0]);
        serpent.FightStarted = true;
        arena.RunUntil(States.Underground);

        arena.Run(serpent.Warning - 0.1f);
        Assert.Equal(States.Underground, serpent.State);
        arena.Run(0.2f);
        Assert.Equal(States.Risen, serpent.State);
        Assert.Equal(wizard.Position - Vector3.UnitY, serpent.Position);
        Assert.Contains(arena.Cues, c => c is CameraShakeCue);
        Assert.Equal(3, serpent.BitesLeft);
        Assert.Equal(Jormungandr.Clips.Emerge, serpent.Clock.Clip?.Name);
        Assert.False(serpent.IsVisible);

        var health = wizard.HitPoints;
        arena.Run(serpent.Warning + 0.1f);
        Assert.True(serpent.IsVisible);
        Assert.Contains(Jormungandr.Sounds.Rise, arena.Sounds);
        Assert.True(wizard.HitPoints < health - 500, $"{health} → {wizard.HitPoints}");        // the emerge's 600 (knocked back too)
    }

    [Fact]
    public void Risen_ItBitesTheNear_UntilItsBitesAreSpent_ThenDivesBackUnder()
    {
        using var arena = new Arena(new Dice(0.5, 0.5, 0.1, 0.1), new Vector3(3, 0.9f, 2));    // two bites
        var serpent = arena.Serpent;
        serpent.FightStarted = true;
        arena.RunUntil(States.Risen);

        arena.RunUntil(States.Bite);
        Assert.Equal(1, serpent.BitesLeft);
        Assert.Contains(Jormungandr.Sounds.PreBite, arena.Sounds);
        arena.RunUntil(States.Risen);
        arena.RunUntil(States.Bite);
        Assert.Equal(0, serpent.BitesLeft);
        arena.RunUntil(States.Risen);
        arena.RunUntil(States.Underground);
        Assert.Contains(Jormungandr.Sounds.Dive, arena.Sounds);
        Assert.False(serpent.IsVisible);
    }

    [Fact]
    public void AWizardFarOff_IsSpatAt_FourGobsOfPoison_AndNeverTwiceRunning()
    {
        using var arena = new Arena(new Dice(0.5, 0.5, 0.1, 0.1), new Vector3(3, 0.9f, 2));
        var (serpent, wizard) = (arena.Serpent, arena.Wizards[0]);
        serpent.FightStarted = true;
        arena.RunUntil(States.Risen);
        wizard.Body.Body.Position = new Vector3(30, wizard.Position.Y, 2);                       // it runs off as the serpent comes up
        var gobs = new HashSet<Entity>();
        arena.World.Entities.Cue += (source, cue) => _ = source is Missile && cue is SoundCue { Cue: Jormungandr.Sounds.Spit } && gobs.Add(source);

        arena.RunUntil(States.Spit);
        Assert.Contains(Jormungandr.Sounds.PreSpit, arena.Sounds);
        arena.RunUntil(States.Risen);
        Assert.Equal(4, gobs.Count);
        arena.RunUntil(States.Bite);                                                             // far still, but no second spit
        Assert.Equal(1, serpent.BitesLeft);
    }

    [Fact]
    public void OtherwiseItLeapsOverAWizard_HiddenForTheWarning_ThenThrownInAnArc_AndBackUnder()
    {
        using var arena = new Arena(new Dice(0.5, 0.5, 0.5, 0.99), new Vector3(0, 0.9f, 0));    // no rise: a leap
        var serpent = arena.Serpent;
        serpent.FightStarted = true;
        arena.RunUntil(States.Underground);
        arena.RunUntil(States.Leap);
        arena.Run(Tick);                                                                          // its way locked on its first frame

        Assert.False(serpent.IsVisible);
        Assert.Contains(Jormungandr.Sounds.Unburrow, arena.Sounds);
        var pivot = serpent.Orientation.Translation;
        Assert.Equal(-9.5f, pivot.Y, 1);                                                         // under the ground
        Assert.Equal(6, new Vector2(pivot.X, pivot.Z).Length(), 1);                               // 6 m past the wizard
        arena.Run(serpent.Warning);
        Assert.True(serpent.IsVisible);
        var pitch = Jormungandr.Forward(serpent.Orientation);
        arena.Run(1);
        Assert.NotEqual(pitch, Jormungandr.Forward(serpent.Orientation));                        // turning over
        arena.RunUntil(States.Underground, limit: 3);
    }

    [Fact]
    public void ABlowOf250_StaggersItRisen_AndItDivesUnderAtOnce()
    {
        using var arena = new Arena(new Dice(0.5, 0.5, 0.5, 0.1), new Vector3(3, 0.9f, 2));
        var serpent = arena.Serpent;
        serpent.FightStarted = true;
        arena.RunUntil(States.Risen);
        arena.Run(serpent.Warning + 0.2f);

        serpent.Head.Damage(Blow(249), null, serpent.Head.Position);
        arena.Run(Tick);
        Assert.Equal(States.Risen, serpent.State);
        serpent.Head.Damage(Blow(250), null, serpent.Head.Position);
        Assert.Equal(1, serpent.Flash, 3);
        arena.Run(Tick);
        Assert.Equal(States.HitRisen, serpent.State);
        Assert.Contains(Jormungandr.Sounds.Pain, arena.Sounds);
        arena.RunUntil(States.Underground, limit: 5);
        Assert.Equal(10000 - 499, serpent.HitPoints);
    }

    [Fact]
    public void ItTakesNoDamageAsItDivesUnder_AndEarthOrIceDoes75Percent()
    {
        using var arena = new Arena(new Dice(0.5, 0.5, 0, 0.1), new Vector3(3, 0.9f, 2));       // two bites
        var serpent = arena.Serpent;
        serpent.FightStarted = true;
        arena.RunUntil(States.Risen);
        arena.Run(serpent.Warning + 0.2f);
        serpent.Head.Damage(new DamageEntry(AttackProperties.Damage, Elements.Earth, 200, 1), null, serpent.Head.Position);
        Assert.Equal(10000 - 150, serpent.HitPoints);

        while (serpent.Clock.Clip?.Name != Jormungandr.Clips.Submerge)
        {
            arena.Run(Tick);
        }
        Assert.Equal(DamageResult.None, serpent.Head.Damage(Blow(100), null, serpent.Head.Position));
    }

    [Fact]
    public void WithNoHitPoints_ItDiesUnderTheGround_AndIsDeadOnceItsDeathHasPlayed()
    {
        using var arena = new Arena(new Dice(), new Vector3(3, 0.9f, 2));
        var serpent = arena.Serpent;
        arena.Run(Tick);
        serpent.Head.Damage(Blow(10000), null, serpent.Head.Position);                           // asleep, it takes the blow
        serpent.FightStarted = true;
        arena.RunUntil(States.Underground);
        arena.Run(Tick);

        Assert.Equal(States.Dead, serpent.State);
        Assert.Equal(Jormungandr.Clips.DieBelow, serpent.Clock.Clip?.Name);
        Assert.Contains(Jormungandr.Sounds.Death, arena.Sounds);
        Assert.True(serpent.IsVisible);
        Assert.False(serpent.IsDead);
        arena.Run(5.1f);
        Assert.True(serpent.IsDead);
        Assert.Equal(DamageResult.None, serpent.Head.Damage(Blow(100), null, serpent.Head.Position));
    }

    [Fact]
    public void TheWarning_ShortensAsItIsHurt_AndGoingUnderEndsItsStatuses()
    {
        using var arena = new Arena();
        var serpent = arena.Serpent;
        arena.Run(Tick);
        Assert.Equal(2.5f, serpent.Warning, 3);
        serpent.Head.Damage(Blow(5000), null, serpent.Head.Position);
        Assert.Equal(1.625f, serpent.Warning, 3);

        serpent.Head.Damage(new DamageEntry(AttackProperties.Status, Elements.Poison, 10, 1), null, serpent.Head.Position);
        arena.Run(Tick);
        Assert.True(serpent.Vitals.Has(StatusEffects.Poisoned));
        serpent.FightStarted = true;
        arena.RunUntil(States.Underground);
        arena.Run(Tick);
        Assert.False(serpent.Vitals.Has(StatusEffects.Poisoned));
    }

    [Fact]
    public void Frozen_ItsClipStands_AndItDoesNotTurn()
    {
        using var arena = new Arena();
        var serpent = arena.Serpent;
        serpent.FightStarted = true;
        arena.Run(1);
        var time = serpent.Clock.Time;
        serpent.Vitals.AddStatus(StatusEffect.Create(StatusEffects.Frozen, 0, 3));
        arena.Run(Tick);
        arena.Run(0.5f);
        Assert.Equal(time + Tick, serpent.Clock.Time, 2);
    }
}
