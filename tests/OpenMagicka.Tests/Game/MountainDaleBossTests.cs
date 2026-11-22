using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using OpenMagicka.Content;
using OpenMagicka.Content.Levels;
using OpenMagicka.Content.Xnb;
using OpenMagicka.Engine.Input;
using OpenMagicka.Engine.Scripting;
using OpenMagicka.Game.Play;
using OpenMagicka.Gameplay.Bosses;
using OpenMagicka.Gameplay.Combat;
using OpenMagicka.Tests.Content;
using States = OpenMagicka.Gameplay.Bosses.Jormungandr.States;

namespace OpenMagicka.Tests.Game;

/// <summary>Chapter 2's end on the game's files: Jörmungandr, from its scene's own scripts to its death and a checkpoint's retry.</summary>
public class MountainDaleBossTests
{
    private const float Tick = 1 / 60f;
    private static readonly Point Screen = new(1280, 720);

    private static LevelPlay BossScene()
    {
        var files = new ContentFileSystem(GameContent.ContentDirectory!);
        var loader = new ContentLoader(files, XnbReaderRegistry.Magicka);
        var path = PlayGame.FindLevel(files, "MountainDale")!;
        var play = LevelPlay.Open(LevelReader.Read(new MemoryStream(files.ReadAllBytes(path)), path), files, loader);
        play.Start();
        play.Level.ChangeScene(new SpawnPoint(LegacyStringHash.Lower("md_s6BF"), [LegacyStringHash.Compute("start")], true), ScriptTransition.None, 0,
            keep: false);
        Wait(play, 0.5f);
        return play;
    }

    private static void Wait(LevelPlay play, float seconds, Func<bool>? until = null)
    {
        for (var tick = 0; tick < seconds / Tick && until?.Invoke() != true; tick++)
        {
            play.Tick(Tick, new ControlFrame(new KeyboardState(), default, default, default), Screen);
        }
    }

    private static Jormungandr Serpent(LevelPlay play) =>
        Assert.IsType<Jormungandr>(Assert.IsType<NamedBoss>(Assert.Single(play.Level.Game.BossFight.Bosses)).Boss);

    private static void Fire(LevelPlay play, string trigger) => play.Scene!.Scripts!.ExecuteTrigger(ScriptScene.Hash(trigger), null, ignoreConditions: true);

    [GameContentFact]
    public void TheSerpent_SleepsAtItsLocator_WakesWithItsFight_SurfacesAndDies_AndTheLevelEnds()
    {
        using var play = BossScene();
        var serpent = Serpent(play);
        Assert.Equal(States.Sleep, serpent.State);
        Assert.True(serpent.IsVisible);
        Assert.False(play.Level.Game.BossFight.IsRunning);
        Assert.Equal(-5.29f, serpent.Position.X, 1);                                             // spawn_jormungandr

        Fire(play, "Start Move");                                                                 // the dialog with Vlad done
        Wait(play, 2.5f);
        Assert.True(play.Level.Game.BossFight.IsRunning);
        Assert.Equal(States.Intro, serpent.State);
        Wait(play, 12, () => serpent.State == States.Underground);
        Assert.Equal(States.Underground, serpent.State);

        Wait(play, 30, () => serpent.IsVisible && serpent.Head.IsPlaced && serpent.Clock.Clip?.Name != Jormungandr.Clips.Submerge);
        Assert.True(serpent.Head.IsPlaced, $"{serpent.State}");
        Assert.True(serpent.Head.Position.Y > -5, $"the head is at {serpent.Head.Position}");     // up out of the ground, not under the level
        serpent.Head.Damage(new DamageEntry(AttackProperties.Damage, OpenMagicka.Gameplay.Spells.Elements.Arcane, 10000, 1), null, serpent.Head.Position);

        Wait(play, 30, () => serpent.IsDead);
        Assert.True(serpent.IsDead, $"{serpent.State}");
        Wait(play, 0.5f);
        Assert.Contains(play.Scene!.Scripts!.Triggers, t => t.Key == ScriptScene.Hash("End bossfight") && t.HasTriggered);
    }

    [GameContentFact]
    public void ARetryAtTheFightsCheckpoint_BringsAWholeSerpentBackAsleep_ThenWakesItAgain()
    {
        using var play = BossScene();
        Fire(play, "Start Move");
        Wait(play, 2.5f);
        Fire(play, "Start Fight");                                                                // the checkpoint, half a second on
        Wait(play, 1);
        Assert.NotNull(play.Level.Checkpoint);
        var first = Serpent(play);
        Wait(play, 10, () => first.Head.IsPlaced);
        first.Head.Damage(new DamageEntry(AttackProperties.Damage, OpenMagicka.Gameplay.Spells.Elements.Arcane, 3000, 1), null, first.Head.Position);

        Assert.True(play.Level.RestartAtCheckpoint());
        Wait(play, 0.1f);
        var again = Serpent(play);
        Assert.NotSame(first, again);
        Assert.Single(play.World.Entities.Entities.OfType<Jormungandr>());
        Assert.Equal(Jormungandr.FullHealth, again.HitPoints);
        Assert.Contains(again.State, new[] { States.Sleep, States.Intro });                     // the replay's activateBoss wakes it
        Wait(play, 0.5f);
        Assert.Equal(States.Intro, again.State);
    }
}
