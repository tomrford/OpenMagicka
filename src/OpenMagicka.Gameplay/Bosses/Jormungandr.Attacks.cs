using System.Numerics;
using OpenMagicka.Gameplay.Animation;
using OpenMagicka.Gameplay.Characters;
using OpenMagicka.Gameplay.Combat;
using OpenMagicka.Gameplay.Entities;
using OpenMagicka.Gameplay.Items;
using OpenMagicka.Gameplay.Spells;
using OpenMagicka.Gameplay.Spells.Effects;
using Barrier = OpenMagicka.Gameplay.Spells.Effects.Barrier;

namespace OpenMagicka.Gameplay.Bosses;

/// <summary>Jörmungandr's attacks, its choice of wizard, and its ground effects (docs/gameplay/BOSSES.md §2.4–2.8).</summary>
public sealed partial class Jormungandr
{
    /// <summary>The leap's contact (ours with the poison the original meant: its status entry has no element).</summary>
    public static readonly IReadOnlyList<DamageEntry> LeapDamage =
        [new(AttackProperties.Damage, Elements.Earth, 150, 1), new(AttackProperties.Status, Elements.Poison, 30, 1), new(AttackProperties.Pushed, Elements.Earth, 70, 2)];

    public static readonly IReadOnlyList<DamageEntry> EmergeDamage =
        [new(AttackProperties.Knockback, Elements.Earth, 150, 2), new(AttackProperties.Damage, Elements.Earth, 300, 2)];

    public static readonly IReadOnlyList<DamageEntry> BiteDamage =
        [new(AttackProperties.Damage, Elements.Earth, 325, 1), new(AttackProperties.Status, Elements.Poison, 30, 2), new(AttackProperties.Knockback, Elements.Earth, 70, 2)];

    public static readonly DamageEntry SpitPoison = new(AttackProperties.Status, Elements.Poison, 75, 4), SpitPush = new(AttackProperties.Pushed, Elements.Earth, 70, 1);

    public static readonly DamageEntry BodyKnock = new(AttackProperties.Knockback, Elements.Earth, 60, 1), BodyRam = new(AttackProperties.Knockback, Elements.Earth, 750, 1);

    /// <summary>The slack its body gives a touch (physics keeps what touches it just outside).</summary>
    private const float TouchSlack = 0.15f;

    private static readonly IReadOnlyList<EventGroup> SpitEvents =
    [
        new(new EventCondition(EventTrigger.Default), [new EffectEvent(Effects.SpitTrail, Follow: true, WorldAlign: false), new SoundEvent(Sounds.Spit, SoundBank.Characters, 1)]),
        new(new EventCondition(EventTrigger.Hit), [new EffectEvent(Effects.SpitSplash, false, false), new SoundEvent(Sounds.Spit, SoundBank.Characters, 1),
            new DamageEvent(SpitPoison), new RemoveEvent(0)]),
        new(new EventCondition(EventTrigger.Collision), [new SoundEvent(Sounds.Spit, SoundBank.Characters, 1), new EffectEvent(Effects.SpitSplash, false, false),
            new SplashEvent(SpitPoison, 2), new SplashEvent(SpitPush, 2), new RemoveEvent(0)]),
    ];

    /// <summary>How it picks its wizard.</summary>
    private enum Targeting : byte { Random, Angle, Strain }

    /// <summary>
    /// A living wizard: from a random player's seat on, the first alive; the one most nearly ahead of it; the one with
    /// the most elements queued. With none, it keeps the one it had, even a dead one.
    /// </summary>
    private void Choose(Targeting how)
    {
        var players = Manager?.Session?.Players;
        if (players is null)
        {
            return;
        }
        Character? Seat(int i) => i < players.Count && players[i].Avatar is { IsDead: false, Manager: not null } avatar ? avatar : null;
        switch (how)
        {
            case Targeting.Random:
                var first = Random.Next(GameSession.MaxPlayers);
                for (var i = 0; i < GameSession.MaxPlayers; i++)
                {
                    if (Seat((i + first) % GameSession.MaxPlayers) is { } found)
                    {
                        target = found;
                        return;
                    }
                }
                break;
            case Targeting.Angle:
                var (forward, best) = (Forward(orientation), float.MaxValue);
                for (var i = 0; i < players.Count; i++)
                {
                    if (Seat(i) is not { } avatar)
                    {
                        continue;
                    }
                    var towards = avatar.Position - position;
                    towards = towards.LengthSquared() > 1e-12f ? Vector3.Normalize(towards) : towards;
                    var angle = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(forward), towards), -1, 1));
                    if (angle < best)
                    {
                        (target, best) = (avatar, angle);
                    }
                }
                break;
            case Targeting.Strain:
                var most = 0;
                for (var i = 0; i < players.Count; i++)
                {
                    if (Seat(i) is { } avatar && avatar.SpellQueue.Count > most)
                    {
                        (target, most) = (avatar, avatar.SpellQueue.Count);
                    }
                }
                break;
        }
    }

    /// <summary>
    /// Where to leap from: six places 60° apart, 14 m from the wizard, from a random start, each put on the nav mesh; one
    /// put more than 2 m off, or with a liquid within 10 m above it, will not do. The last that does is taken, with the way
    /// from it to the wizard (ours: its own way, not the last place tried's).
    /// </summary>
    private (Vector3 Start, Vector3 Towards)? LeapStart(Vector3 at)
    {
        if (Manager is not { } manager)
        {
            return null;
        }
        var angle = Random.NextDouble() * Math.PI * 2;
        (Vector3, Vector3)? found = null;
        for (var i = 0; i < 6; i++, angle += Math.PI / 3)
        {
            var away = new Vector3((float)Math.Cos(angle), 0, (float)Math.Sin(angle));
            var wanted = at + away * 14;
            var spot = manager.Walkable(wanted);
            var wet = manager.Liquids.Any(l => l.Meets(spot, spot + Vector3.UnitY * 10, skipLiquid: false, skipFrozen: false, out _, out _));
            if (Vector3.Distance(spot, wanted) <= 2 && !wet)
            {
                found = (spot, Flat(at - spot) ?? -away);
            }
        }
        return found;
    }

    /// <summary>The shields and barriers within a radius of it die (it bursts up through them, or dives down through them).</summary>
    private void ClearBarriers(float radius)
    {
        if (Manager is not { } manager)
        {
            return;
        }
        foreach (var entity in manager.Query(position, radius))
        {
            switch (entity)
            {
                case SpellShield shield:
                    RaiseCue(new WorldEffectCue(Effects.BarrierDeath, shield.Position, Vector3.UnitX));
                    shield.Kill();
                    break;
                case Barrier barrier:
                    RaiseCue(new WorldEffectCue(Effects.BarrierDeath, barrier.Position, Vector3.UnitX));
                    barrier.Kill();
                    break;
            }
        }
    }

    /// <summary>
    /// A gob of venom at a wizard, from by the head: it drops straight onto where the wizard is now, launched flat (ours:
    /// a wizard above the head is aimed at in half a second, where the original's aim fails).
    /// </summary>
    private void SpitAt(Character aim, Vector3 from)
    {
        if (Manager is not { } manager || manager.Entities.Count >= EntityManager.Capacity || World.IsStepping)
        {
            return;
        }
        var gravity = World.Settings.Gravity;
        var fall = (from.Y - aim.Position.Y) / (-0.5f * gravity.Y);
        var time = fall > 1e-4f ? MathF.Sqrt(fall) : 0.5f;
        var velocity = (aim.Position - from - gravity * (time * time) / 2) / time;
        var gob = new Missile(World, Head, from, velocity, 0.25f, SpitEvents, canHitOwner: false);
        manager.Add(gob);
        gob.Start();
    }

    /// <summary>
    /// The head first touching the ground since it was last told to (as it emerges, leaps or dives back in) throws up a
    /// splash of dirt that shakes the ground, with the leap's own sound; while it bites it only sprays dirt where its neck
    /// meets the ground.
    /// </summary>
    private void TouchGround()
    {
        if (!Head.IsPlaced || noGroundEffects || dirtSpawned)
        {
            return;
        }
        var at = Head.Position;
        if (World.CastSegment(at + Vector3.UnitY * HeadRadius, at - Vector3.UnitY * HeadRadius, levelOnly: true) is null)
        {
            return;
        }
        if (State == States.Bite)
        {
            if (!bitSplash && Crossing(12, 13, 24) is { } neck)
            {
                RaiseCue(new WorldEffectCue(Effects.Splash, neck, Vector3.UnitX));
                bitSplash = true;
            }
            return;
        }
        if (Clock.Clip?.Name == Clips.Leap)
        {
            Sound(midLeap ? Sounds.LeapDive : Sounds.Leap);
        }
        riseTremor.Stop();
        splash.Stop();
        if (GroundUnder(at) is { } ground)
        {
            splash.Start(ground);
        }
        dirtSpawned = true;
        Shake(1, 0.7f);
    }

    /// <summary>
    /// Where its body meets the ground on each side of its middle, a tremor of dirt (none under the ground or asleep; ours:
    /// none while hidden mid-leap either, where the original shows them where the body last was). Waking, its front one
    /// shows from 2 s on.
    /// </summary>
    private void Tremors()
    {
        if (State is States.Underground or States.Sleep || !animating)
        {
            frontTremor.Stop();
            backTremor.Stop();
            return;
        }
        Tremor(backTremor, Crossing(12, 11, 0));
        var front = Crossing(12, 13, 24);
        var waking = State == States.Intro && !Clock.IsCrossFading && Clock.Time > 2;
        Tremor(frontTremor, State == States.Intro ? (waking ? front ?? GroundUnder(pose.Position(spine[11])) : null) : front);
    }

    private static void Tremor(BossEffect tremor, Vector3? at)
    {
        if (at is { } place)
        {
            tremor.Start(place);
        }
        else
        {
            tremor.Stop();
        }
    }

    /// <summary>The first place, along the spine from one of its points towards another (excluded), where it crosses the ground.</summary>
    private Vector3? Crossing(int from, int next, int end)
    {
        var step = next > from ? 1 : -1;
        var origin = pose.Position(spine[from]);
        for (var i = next; i != end; i += step)
        {
            var point = pose.Position(spine[i]);
            if (World.CastSegment(origin, point, levelOnly: true) is { } hit)
            {
                return hit.Position;
            }
            origin = point;
        }
        return null;
    }

    /// <summary>The level's ground under a place (from 10 m above it to 20 m below).</summary>
    private Vector3? GroundUnder(Vector3 at) => World.CastSegment(at + Vector3.UnitY * 10, at - Vector3.UnitY * 20, levelOnly: true)?.Position;

    /// <summary>
    /// The head strikes what it touches with its move's damage (each once a quarter second); a shield or solid barrier it
    /// rams loses three quarters of its most, and the ram costs the serpent 750 and staggers it. What the strike kills
    /// is torn apart.
    /// </summary>
    private void HeadContacts()
    {
        var damage = contact switch
        {
            Contact.Leap => LeapDamage,
            Contact.Emerge => EmergeDamage,
            Contact.Bite => BiteDamage,
            _ => null,
        };
        if (damage is null || !Head.IsPlaced || Manager is not { } manager)
        {
            return;
        }
        var at = Head.Position;
        foreach (var entity in manager.Query(at, HeadRadius))
        {
            if (!entity.IsDamageable || entity is Missile || IsOwn(entity) || headHits.Remembers(entity)
                || !entity.SegmentIntersect(new Segment(at, Vector3.Zero), HeadRadius, out var hit))
            {
                continue;
            }
            headHits.Remember(entity);
            switch (entity)
            {
                case Barrier { IsSolid: false }:
                    continue;
                case SpellShield or Barrier:
                    Sound(Sounds.HitObject);
                    var most = entity is Barrier barrier ? barrier.MaxHitPoints : SpellShield.MaxHitPoints;
                    entity.Damage(new DamageEntry(AttackProperties.Damage, Elements.Earth, (int)(most * 0.75f), 1), Head, hit);
                    Vitals.Hurt(RamDamage, Elements.None);
                    staggered = true;
                    continue;
            }
            entity.Damage(damage, Head, hit);
            if (entity is Character { IsDead: true } torn)
            {
                torn.OverKill();
            }
        }
    }

    /// <summary>Its body knocks back what touches it (each once a quarter second), and shields and barriers hard; dead, it only blocks.</summary>
    private void SpineContacts()
    {
        if (dead || !Spine.IsPlaced || Manager is not { } manager)
        {
            return;
        }
        var (centre, reach) = Spine.Bounds();
        foreach (var entity in manager.Query(centre, reach))
        {
            if (!entity.IsDamageable || entity is Missile || IsOwn(entity) || spineHits.Remembers(entity) || !Spine.Touches(entity, TouchSlack, out var hit))
            {
                continue;
            }
            spineHits.Remember(entity);
            switch (entity)
            {
                case Character character:
                    character.Damage(BodyKnock, Head, hit);
                    break;
                case SpellShield or Barrier:
                    entity.Damage(BodyRam, Head, hit);
                    break;
            }
        }
    }

    private bool IsOwn(Entity entity) => ReferenceEquals(entity, this) || Parts.Contains(entity);
}
