using System.Numerics;
using OpenMagicka.Gameplay.Characters;
using OpenMagicka.Gameplay.Entities;

namespace OpenMagicka.Engine.Scripting;

/// <summary>
/// A boss of <c>Data/Characters/&lt;name&gt;</c> (a spawnBoss's generic one): it comes in at the place, turned its
/// way, as a new character once the last is gone (the original keeps one and puts it back).
/// </summary>
public sealed class TemplateBoss(string asset, int uniqueId) : IFightBoss
{
    public Character? Character { get; private set; }

    public float HitPoints => Character?.HitPoints ?? 0;

    public float MaxHitPoints => Character?.MaxHitPoints ?? 0;

    public bool IsDead => Character is null or { IsDead: true };

    public void Enter(ScriptScene scene, Matrix4x4 at)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var manager = scene.World.Entities;
        if (!scene.IsHost || Character is { IsDead: false, Manager: not null } || scene.World.Physics.IsStepping
            || manager.Entities.Count >= EntityManager.Capacity
            || manager.Bestiary?.Find(asset) is not { } found)
        {
            return;
        }
        var (template, animations) = found;
        Character = new Character(template, scene.World.Physics, at.Translation + Vector3.UnitY * (template.Length / 2 + template.Radius),
            random: new Random(scene.Random.Next()), animations: animations);
        manager.Add(Character);
        Character.UniqueId = uniqueId;
        Character.Body.Face(ScriptPlaces.Forward(at));
    }

    public void Message(BossMessages message)
    {
    }
}

/// <summary>
/// <c>spawnBoss boss= area= id= prop= generic= delayed= healthBarWidth= freezeTime= onDeath= onDamage= …</c>: the
/// boss joins the fight at the place (the fight set up first): a prop of <c>Data/PhysicsEntities</c> with
/// <c>prop</c>, a character of <c>Data/Characters</c> with <c>generic</c>, else the named boss of the game's
/// code, made anew (<see cref="NamedBosses"/>; one not made yet: none, or the stand-in). The fight starts unless <c>delayed</c>. A prop the
/// game lacks fails the loading. As the original, a replay does it again.
/// </summary>
public sealed class SpawnBossAction : ScriptAction
{
    private IFightBoss? boss;

    public override void Replay() => Execute();

    public string Id { get; set; } = "";
    public string Boss { get; set; } = "";
    public bool Prop { get; set; }
    public bool Generic { get; set; }
    public string Template { get; set; } = "";
    public bool Delayed { get; set; }
    public string Area { get; set; } = "";
    public float HealthAppearDelay { get; set; }
    public float FreezeTime { get; set; }
    public float HealthBarWidth { get; set; } = 0.8f;
    public int MeshId { get; set; } = -1;
    public string OnDeath { get; set; } = "";
    public string OnDamage { get; set; } = "";

    public override void Initialize()
    {
        var id = Id.Length > 0 ? Hash(Id) : 0;
        if (Prop)
        {
            if (Scene.Assets is { } assets)
            {
                var template = assets.Prop("data/physicsentities/" + Boss) ?? throw new InvalidDataException($"<spawnBoss boss=\"{Boss}\">: no such prop");
                boss = new PropBoss(template, id, OnDeath.Length > 0 ? Hash(OnDeath) : 0, OnDamage.Length > 0 ? Hash(OnDamage) : 0);
            }
            return;
        }
        boss = Generic ? new TemplateBoss("data/characters/" + Boss, id) : Scene.Assets?.Boss(Boss);
    }

    protected override void Execute()
    {
        if (boss is null && !Prop && !Generic)
        {
            boss = StandInBoss.For(Scene.Game, Boss);                         // a named boss not made yet
        }
        Join(Scene, boss, Area, Delayed, FreezeTime, HealthBarWidth);
    }

    internal static void Join(ScriptScene scene, IFightBoss? boss, string area, bool delayed, float freezeTime, float healthBarWidth)
    {
        var fight = scene.Game.BossFight;
        if (!fight.IsSetup)
        {
            fight.Setup(freezeTime, healthBarWidth);
        }
        if (boss is null)
        {
            return;
        }
        fight.Join(scene, boss, scene.Places.Placement(LegacyHash(area), scene.Random));
        if (!delayed)
        {
            fight.Start();
        }
    }

    private static int LegacyHash(string name) => Content.LegacyStringHash.Lower(name);
}

/// <summary>
/// <c>spawnGenericBoss type= area= id= order= … delayed= healthBarWidth=</c>: one character, made and dressed as
/// a spawn's, joins the boss fight; the fight starts unless <c>delayed</c>. Not replayed, as the original.
/// </summary>
public sealed class SpawnGenericBossAction : SpawnAction
{
    public bool Delayed { get; set; }
    public float HealthAppearDelay { get; set; }
    public float FreezeTime { get; set; }
    public float HealthBarWidth { get; set; } = 0.8f;

    public override void Replay()
    {
    }

    protected override object? SaveTag() => null;

    protected override void LoadTag(object? tag)
    {
    }

    protected override void Execute()
    {
        var manager = Scene.World.Entities;
        if (!Scene.IsHost || manager.Entities.Count >= EntityManager.Capacity || manager.Bestiary?.Find("data/characters/" + Type) is not { } found)
        {
            return;
        }
        var character = Make(found.Template, found.Animations);
        SpawnBossAction.Join(Scene, new CharacterBoss(character), Area, Delayed, FreezeTime, HealthBarWidth);
    }
}

/// <summary><c>activateBoss</c>: the fight with a delayed boss starts.</summary>
public sealed class ActivateBossAction : ScriptAction
{
    /// <summary>As the original, a replay does it again.</summary>
    public override void Replay() => Execute();

    protected override void Execute() => Scene.Game.BossFight.Start();
}

/// <summary><c>endBossFight</c>: the fight is over, its bar gone.</summary>
public sealed class EndBossFightAction : ScriptAction
{
    /// <summary>As the original, a replay does it again.</summary>
    public override void Replay() => Execute();

    protected override void Execute() => Scene.Game.BossFight.Reset(Scene.Game);
}

/// <summary><c>bossMessage message=</c>: the fight's bosses are told something (a named boss's script acts on it, OM-42).</summary>
public sealed class BossMessageAction : ScriptAction
{
    /// <summary>As the original, a replay does it again.</summary>
    public override void Replay() => Execute();

    public BossMessages Message { get; set; }

    protected override void Execute() => Scene.Game.BossFight.Message(Message);
}

/// <summary>
/// A debugging stand-in for a named boss OpenMagicka has not made yet (OM-42), so its level can be played on: with
/// <see cref="Variable"/> set to 1 (<see cref="ScriptGame.SkipMissingBosses"/>), it joins the fight and is dead once
/// the fight starts, so the level's <c>bossDead</c> ending runs. Without it the fight has no boss, and a line says so.
/// </summary>
public sealed class StandInBoss(string name) : IFightBoss
{
    public const string Variable = "OPENMAGICKA_SKIP_BOSSES";

    public string Name { get; } = name;

    public float HitPoints => 0;

    public float MaxHitPoints => 1;

    public bool IsDead => true;

    /// <summary>Whether the environment asks for the missing bosses' fights to be skipped.</summary>
    public static bool SkipFromEnvironment() => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } value && value != "0";

    /// <summary>The stand-in when the game skips missing bosses; else none, and the console is told.</summary>
    internal static StandInBoss? For(ScriptGame game, string name)
    {
        if (game.SkipMissingBosses)
        {
            Console.Error.WriteLine($"boss: '{name}' is not made yet; its fight is skipped ({Variable})");
            return new StandInBoss(name);
        }
        Console.Error.WriteLine($"boss: '{name}' is not made yet, so its fight cannot be won; set {Variable}=1 to skip it");
        return null;
    }

    public void Enter(ScriptScene scene, System.Numerics.Matrix4x4 at)
    {
    }

    public void Message(BossMessages message)
    {
    }
}
