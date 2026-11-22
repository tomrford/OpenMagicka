using OpenMagicka.Engine.Scripting;
using OpenMagicka.Gameplay.Combat;
using OpenMagicka.Gameplay.Items;
using OpenMagicka.Gameplay.Props;
using static OpenMagicka.Tests.Engine.ScriptWorldActionsTests;
using static OpenMagicka.Tests.Gameplay.CharacterTests;
using Numerics = System.Numerics;

namespace OpenMagicka.Tests.Engine;

/// <summary>The boss fights' actions (LEVELS_TRIGGERS.md §9.6): spawnBoss, spawnGenericBoss, activateBoss, endBossFight, bossMessage.</summary>
public class ScriptBossActionsTests
{
    private static readonly PropTemplate Lock = new("lock_big", false, false, true, 500, 1000, ResistanceTable.Neutral,
        new PropBox(new Numerics.Vector3(-1, 0, -1), new Numerics.Vector3(2), Numerics.Quaternion.Identity), [], "", [], []);

    private sealed class Bosses(IFightBoss? named = null) : IScriptAssets
    {
        public ItemTemplate? Item(string asset) => null;

        public PropTemplate? Prop(string asset) => asset == "data/physicsentities/lock_big" ? Lock : null;

        public IFightBoss? Boss(string name) => name == "grimnir" ? named : null;
    }

    private sealed class Listener : IFightBoss
    {
        public List<BossMessages> Heard { get; } = [];

        public float HitPoints => 1;

        public float MaxHitPoints => 1;

        public bool IsDead => false;

        public void Enter(ScriptScene scene, Numerics.Matrix4x4 at)
        {
        }

        public void Message(BossMessages message) => Heard.Add(message);
    }

    private static ScriptScene Fight(OpenMagicka.Gameplay.GameWorld world, string triggers, IFightBoss? named = null) =>
        Scene(world, triggers, new Bosses(named));

    [Fact]
    public void APropBoss_Joins_DelayedUntilActivated_AndDiesWithItsProp()
    {
        using var world = World();
        var scene = Fight(world, """
            <Trigger id="spawn" autorun="false"><Then><spawnBoss boss="lock_big" prop="true" area="spot" id="Lock" delayed="true" onDeath="opened" healthBarWidth="0.5"/></Then></Trigger>
            <Trigger id="go" autorun="false"><Then><activateBoss/></Then></Trigger>
            <Trigger id="opened" autorun="false"><Then><counter name="open" action="inc" value="1"/></Then></Trigger>
            <Trigger id="won" autorun="false"><If><bossDead/></If><Then><counter name="won" action="inc" value="1"/></Then></Trigger>
            """);
        var fight = scene.Game.BossFight;

        Fire(scene, "spawn");
        var prop = Assert.IsType<PhysicsProp>(world.Entities.FindByUniqueId(ScriptScene.Hash("lock")));
        Assert.True(prop.IsBoss);
        Assert.Same(fight, scene.Game.Boss);
        Assert.Equal(0.5f, fight.HealthBarWidth);
        Assert.False(fight.IsRunning);
        Fire(scene, "go");
        Assert.True(fight.IsRunning);
        Assert.Equal(1, fight.NormalizedHealth, 3);

        prop.Damage(new DamageEntry(AttackProperties.Damage, OpenMagicka.Gameplay.Spells.Elements.Earth, 500, 1), null, prop.Position);
        scene.Update(Tick);
        Assert.Equal(0.5f, fight.NormalizedHealth, 2);
        prop.Kill();
        world.Tick(Tick);
        scene.Update(Tick);
        Assert.True(fight.Dead);
        Assert.Equal(1, scene.Level.Counter(ScriptScene.Hash("open")));
        Fire(scene, "won");
        Assert.Equal(1, scene.Level.Counter(ScriptScene.Hash("won")));
        Assert.Null(OpenMagicka.Engine.Scripting.EntityState.Of(prop));                         // a checkpoint does not keep a boss's prop
    }

    [Fact]
    public void AGenericBoss_IsASpawnedCharacter_AndEndBossFightClearsTheFight()
    {
        using var world = World();
        var scene = Fight(world, """
            <Trigger id="spawn" autorun="false"><Then><spawnGenericBoss type="orc" area="spot" id="Chief" health="0.5" order="idle"/></Then></Trigger>
            <Trigger id="end" autorun="false"><Then><endBossFight/></Then></Trigger>
            """);
        var fight = scene.Game.BossFight;

        Fire(scene, "spawn");
        var chief = Assert.IsType<OpenMagicka.Gameplay.Characters.Character>(world.Entities.FindByUniqueId(ScriptScene.Hash("chief")));
        Assert.True(fight.IsRunning);
        Assert.Equal(0.5f, fight.NormalizedHealth, 2);
        Assert.Same(chief, Assert.IsType<CharacterBoss>(Assert.Single(fight.Bosses)).Character);

        Fire(scene, "end");
        Assert.Empty(fight.Bosses);
        Assert.Null(scene.Game.Boss);
    }

    [Fact]
    public void ANamedBoss_ComesFromTheGamesCode_AndHearsTheMessages()
    {
        using var world = World();
        var grimnir = new Listener();
        var scene = Fight(world, """
            <Trigger id="spawn" autorun="false"><Then><spawnBoss boss="Grimnir" area="spot"/></Then></Trigger>
            <Trigger id="tell" autorun="false"><Then><bossMessage message="grimnirhurt"/></Then></Trigger>
            <Trigger id="none" autorun="false"><Then><spawnBoss boss="vlad" area="spot"/></Then></Trigger>
            """, grimnir);

        Fire(scene, "spawn");
        Fire(scene, "tell");
        Fire(scene, "none");

        Assert.Equal([BossMessages.GrimnirHurt], grimnir.Heard);
        Assert.Single(scene.Game.BossFight.Bosses);                                             // vlad's own code is not here yet (OM-42)
    }

    [Fact]
    public void ANamedBossNotMadeYet_IsAStandInThatDiesAsItsFightStarts_WhenMissingBossesAreSkipped()
    {
        foreach (var skip in new[] { true, false })
        {
            using var world = World();
            var scene = Fight(world, """
                <Trigger id="spawn" autorun="false"><Then><spawnBoss boss="fafnir" area="spot" delayed="true"/></Then></Trigger>
                <Trigger id="go" autorun="false"><Then><activateBoss/></Then></Trigger>
                <Trigger id="won" autorun="false"><If><bossDead/></If><Then><counter name="won" action="inc" value="1"/></Then></Trigger>
                """);
            scene.Game.SkipMissingBosses = skip;
            var fight = scene.Game.BossFight;

            Fire(scene, "spawn");
            scene.Update(Tick);
            Assert.False(fight.Dead);                                                             // not till the fight starts
            Fire(scene, "go");
            scene.Update(Tick);
            Fire(scene, "won");

            Assert.Equal(skip ? 1 : 0, scene.Level.Counter(ScriptScene.Hash("won")));
            Assert.Equal(skip, fight.Bosses.SingleOrDefault() is StandInBoss { Name: "fafnir" });
        }
    }

    [Fact]
    public void ANamedBoss_IsMadeAnewAtEachSpawn_IsToldWhetherItsFightRuns_AndIsDeadWhenItSaysSo()
    {
        using var world = World();
        var made = new List<OpenMagicka.Tests.Gameplay.BossTests.Dummy>();
        var named = new NamedBoss("grimnir", (scene, at) =>
        {
            var boss = new OpenMagicka.Tests.Gameplay.BossTests.Dummy(scene.World.Physics);
            made.Add(boss);
            return boss;
        });
        var scene = Fight(world, """
            <Trigger id="spawn" autorun="false" repeat="true"><Then><spawnBoss boss="grimnir" area="spot" delayed="true"/></Then></Trigger>
            <Trigger id="go" autorun="false" repeat="true"><Then><activateBoss/></Then></Trigger>
            """, named);
        var fight = scene.Game.BossFight;

        Fire(scene, "spawn");
        var first = Assert.Single(made);
        Assert.Contains(first, world.Entities.Entities);
        Assert.False(first.FightStarted);
        Fire(scene, "go");
        Assert.True(first.FightStarted);
        Assert.Equal(1, fight.NormalizedHealth, 3);

        first.Vitals.Damage(new DamageEntry(AttackProperties.Damage, OpenMagicka.Gameplay.Spells.Elements.Earth, 500, 1));
        Fire(scene, "spawn");                                                                     // a replay's spawn: a whole one again
        world.Tick(Tick);
        var second = made[1];
        Assert.DoesNotContain(first, world.Entities.Entities);
        Assert.DoesNotContain(first.Head, world.Entities.Entities);
        Assert.Contains(second, world.Entities.Entities);
        Assert.Same(second, named.Boss);
        Assert.Equal(1000, named.HitPoints);
        Assert.Single(fight.Bosses);

        Fire(scene, "go");
        second.Vitals.Damage(new DamageEntry(AttackProperties.Damage, OpenMagicka.Gameplay.Spells.Elements.Earth, 2000, 1));
        scene.Update(Tick);
        Assert.True(fight.Dead);
    }

    [Fact]
    public void APropBossTheGameLacks_FailsTheLoading()
    {
        using var world = World();
        Assert.Throws<InvalidDataException>(() => Fight(world, """<Trigger id="t"><Then><spawnBoss boss="vault" prop="true" area="spot"/></Then></Trigger>"""));
    }
}
