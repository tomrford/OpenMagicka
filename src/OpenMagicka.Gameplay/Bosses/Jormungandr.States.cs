using System.Numerics;
using OpenMagicka.Gameplay.Combat;

namespace OpenMagicka.Gameplay.Bosses;

/// <summary>Jörmungandr's states (docs/gameplay/BOSSES.md §2.3): what each does as it begins, each frame, and as it ends.</summary>
public sealed partial class Jormungandr
{
    public enum States : byte { Sleep, Intro, Underground, Leap, Risen, Spit, Bite, HitRisen, HitLeap, Dead }

    public States State { get; private set; }

    /// <summary>The state before this one (a spit never follows a spit).</summary>
    public States LastState { get; private set; }

    private bool entered;

    // what one state remembers while it lasts
    private bool introDived, leapLocked, growled, whipSounded, whipped, barriersCleared, roared, sprayed, bitSound, shaken, bitSplash;
    private int spat;
    private float spitTimer;
    private string deathClip = Clips.DieAbove;

    /// <summary>The share of the leap's clip at which its tail whips.</summary>
    public const float WhipAt = 55f / 62;

    /// <summary>How far it turns over a whole leap.</summary>
    public const float LeapSweep = 275;

    private static readonly DamageEntry[] WhipDamage =
        [new(AttackProperties.Knockback, Spells.Elements.Earth, 200, 4), new(AttackProperties.Damage, Spells.Elements.Earth, 300, 1)];

    private void Change(States next)
    {
        if (entered && next == State)
        {
            return;
        }
        if (entered)
        {
            LastState = State;
            Exit(State);
        }
        (State, entered) = (next, true);
        Enter(next);
    }

    /// <summary>A stagger flings it into its hit: mid-leap it recoils; risen or biting it flinches; else it is shrugged off.</summary>
    private void CheckStagger()
    {
        if (State == States.Dead || !staggered)
        {
            return;
        }
        switch (State)
        {
            case States.Leap:
                Change(States.HitLeap);
                break;
            case States.Risen or States.Bite:
                Change(States.HitRisen);
                break;
            default:
                staggered = false;
                break;
        }
    }

    private void Enter(States state)
    {
        switch (state)
        {
            case States.Sleep:
                Clock.Start(pose.Clip(Clips.Intro), loop: false);
                Clock.Stop();
                (drawn, animating, noGroundEffects) = (true, true, true);
                break;
            case States.Intro:
                Clock.Start(pose.Clip(Clips.Intro), loop: false);
                Sound(Sounds.Wakeup, positional: true);
                (drawn, animating, noGroundEffects, introDived) = (true, true, true, false);
                break;
            case States.Underground:
                splash.Stop();
                riseTremor.Stop();
                (noGroundEffects, dirtSpawned, drawn, animating, staggered, timer, bites, shaken) = (false, false, false, false, false, 0, 0, false);
                headHits.Forget();
                spineHits.Forget();
                Vitals.ClearStatuses();
                break;
            case States.Leap:
                orientation = Matrix4x4.CreateRotationX(ToRadians(125)) * orientation;
                Clock.Start(pose.Clip(Clips.Leap), loop: false);
                (animating, drawn, contact, timer, dirtSpawned, noGroundEffects, midLeap) = (false, false, Contact.Leap, 0, false, false, false);
                (growled, whipSounded, whipped, leapLocked) = (false, false, false, false);
                headHits.Forget();
                break;
            case States.Risen:
                (timer, noGroundEffects, dirtSpawned, barriersCleared, roared) = (0, false, false, false, false);
                spineHits.Forget();
                if (Clock.Clip?.Name == Clips.Emerge)
                {
                    if (GroundUnder(Head.IsPlaced ? Head.Position : position) is { } ground)
                    {
                        riseTremor.Start(ground);
                    }
                    Sound(Sounds.Unburrow);
                }
                Choose(Targeting.Angle);
                break;
            case States.Spit:
                stateSpeed = 1.333f;                                          // ours: the original's is overwritten before its clip runs (BOSSES.md §2.6)
                Clock.CrossFade(pose.Clip(Clips.Venom), 0.2f, loop: false);
                contact = Contact.None;
                Sound(Sounds.PreSpit);
                Choose(Targeting.Random);
                (spat, spitTimer, sprayed) = (0, 0, false);
                break;
            case States.Bite:
                bitSound = bitSplash = false;
                bites--;
                Clock.CrossFade(pose.Clip(Clips.Bite), 0.15f, loop: false);
                contact = Contact.Bite;
                headHits.Forget();
                Sound(Sounds.PreBite);
                if (target is null)
                {
                    Choose(Targeting.Strain);
                }
                break;
            case States.HitRisen:
                var flinchFrom = Clock.Time;
                Clock.CrossFade(pose.Clip(Clips.Hit), 0.5f, loop: false);
                if (flinchFrom > 1.1f)
                {
                    Clock.Stop();                                             // the old clip holds while the flinch blends in
                }
                Sound(Sounds.Pain);
                break;
            case States.HitLeap:
                Clock.CrossFade(pose.Clip(Clips.Recoil), 0.2f, loop: false);
                Sound(Sounds.Pain);
                break;
            case States.Dead:
                (drawn, animating, contact, timer) = (true, true, Contact.None, 0);
                Clock.Start(pose.Clip(deathClip), loop: false);
                Sound(Sounds.Death);
                splash.Stop();
                riseTremor.Stop();
                break;
        }
    }

    private void Run(float dt)
    {
        switch (State)
        {
            case States.Intro:
                if (Clock.IsCrossFading)
                {
                    return;
                }
                if (Clock.HasFinished)
                {
                    Change(States.Underground);
                }
                else if (Clock.Time > 2.2f && !introDived)
                {
                    Sound(Sounds.Dive, positional: true);
                    introDived = true;
                }
                break;
            case States.Underground:
                Underground(dt);
                break;
            case States.Leap:
                Leap(dt);
                break;
            case States.Risen:
                Risen(dt);
                break;
            case States.Spit:
                Spit(dt);
                break;
            case States.Bite:
                Bite(dt);
                break;
            case States.HitRisen:
                if (Clock.HasFinished && !Clock.IsCrossFading)
                {
                    Clock.CrossFade(pose.Clip(Clips.Submerge), 0.6f, loop: false);
                    Change(States.Risen);
                }
                break;
            case States.HitLeap:
                if (Clock.IsCrossFading || !(Clock.Time / pose.Clip(Clips.Recoil).Duration > 0.666f))
                {
                    return;
                }
                timer -= dt;
                orientation = Matrix4x4.CreateRotationX(ToRadians(Sweep(dt))) * orientation;   // the leap played back
                if (timer <= Warning)
                {
                    Change(States.Underground);
                }
                break;
            case States.Dead:
                orientation = Facing(position, direction);
                if (Clock.HasFinished && !Clock.IsCrossFading)
                {
                    dead = true;
                }
                break;
        }
    }

    private void Exit(States state)
    {
        switch (state)
        {
            case States.Intro:
                timer = 0;
                break;
            case States.Leap:
                contact = Contact.None;
                position = Head.Position;
                splash.Stop();
                (midLeap, leapLocked, stateSpeed) = (false, false, 1);
                break;
            case States.Risen:
                contact = Contact.None;
                break;
            case States.Spit:
                Choose(Targeting.Random);
                stateSpeed = 1;
                break;
            case States.Bite:
                contact = Contact.None;
                stateSpeed = 1;
                break;
            case States.HitRisen:
                staggered = false;
                break;
            case States.HitLeap:
                (staggered, stateSpeed) = (false, 1);
                break;
        }
    }

    /// <summary>
    /// Under the ground: dead if it has no hit points; else after the warning it picks a wizard at random, shakes the
    /// ground, and either rises under it or leaps over it from a place it can stand on (trying again each frame it finds none).
    /// </summary>
    private void Underground(float dt)
    {
        if (Vitals.HitPoints <= 0)
        {
            deathClip = Clips.DieBelow;
            Change(States.Dead);
            return;
        }
        timer += dt;
        if (!(timer > Warning))
        {
            return;
        }
        Choose(Targeting.Random);
        if (target is null)
        {
            return;
        }
        if (!shaken)
        {
            Shake(ShakeStrength(), Warning);                                  // ours: once, not again each frame no leap is found
            shaken = true;
        }
        bites = Random.Next(2) + 2;
        if (Random.NextSingle() <= 0.35f + 0.4f * Vitals.HitPoints / Vitals.MaxHitPoints)
        {
            position = target.Position - Vector3.UnitY;
            orientation = Matrix4x4.CreateRotationY(2.5f) * Matrix4x4.CreateTranslation(position);
            headHits.Forget();
            Clock.Start(pose.Clip(Clips.Emerge), loop: false);
            Change(States.Risen);
            return;
        }
        if (LeapStart(target.Position) is not var (start, towards))
        {
            return;
        }
        position = start;
        direction = -towards;
        var far = target.Position + towards * 14;
        position = World.CastSegment(far, far - Vector3.UnitY * 10, levelOnly: true)?.Position ?? far;
        orientation = Facing(position, direction);
        orientation.Translation -= new Vector3(towards.X * 8, 9.5f, towards.Z * 8);
        Change(States.Leap);
    }

    /// <summary>
    /// The leap: hidden for the warning (its direction locked on the first frame), then thrown out of the ground in an
    /// arc about a pivot under it, growling, whipping its tail at the end, and back under once its clip ends.
    /// </summary>
    private void Leap(float dt)
    {
        var progress = Clock.NormalizedTime;
        timer += dt;
        if (!Clock.IsCrossFading && progress > WhipAt && !whipped && Manager is { } manager)
        {
            var tail = pose.Position(spine[1]);
            foreach (var damage in WhipDamage)
            {
                Entities.Splash.Hit(manager, Head, Spine, tail, 3, damage);
            }
            whipped = true;
        }
        if (!leapLocked && target is { } locked)
        {
            direction = locked.Position - position;
            direction = direction.LengthSquared() > 1e-12f ? Vector3.Normalize(direction) : Vector3.UnitZ;
            orientation = Matrix4x4.CreateRotationX(ToRadians(125)) * Facing(position, direction);
            orientation.Translation += new Vector3(direction.X * 8, -9.5f, direction.Z * 8);
            leapLocked = true;
            Sound(Sounds.Unburrow);
        }
        if (!(timer > Warning))
        {
            return;
        }
        if (timer > Warning + 0.9f && !midLeap)
        {
            (midLeap, dirtSpawned) = (true, false);                           // the dive back in splashes again
            splash.Stop();
        }
        else if (timer > Warning + 0.6f && !growled)
        {
            Sound(Sounds.Bite);
            growled = true;
        }
        else if (timer > Warning + 1.9f && !whipSounded)
        {
            Sound(Sounds.Whip);
            whipSounded = true;
        }
        riseTremor.Stop();
        (animating, drawn) = (true, true);
        orientation = Matrix4x4.CreateRotationX(-ToRadians(Sweep(dt))) * orientation;
        position = Head.Position;
        if (Clock.HasFinished)
        {
            Change(States.Underground);
        }
    }

    /// <summary>
    /// Risen, in three phases by its clip: emerging (hidden for the warning, then out, clearing the shields in its way),
    /// idling and choosing (a bite near, a spit far or blocked, never two spits running; under again with no bites
    /// left), and submerging (clearing the shields about it).
    /// </summary>
    private void Risen(float dt)
    {
        timer += dt;
        switch (Clock.Clip?.Name)
        {
            case Clips.Submerge:
                if (!barriersCleared)
                {
                    ClearBarriers(3);
                    barriersCleared = true;
                }
                if (!dirtSpawned)
                {
                    Sound(Sounds.Dive);
                    (dirtSpawned, noGroundEffects) = (true, true);
                    Shake(ShakeStrength(), 1);
                }
                if (Clock.HasFinished && !Clock.IsCrossFading)
                {
                    Change(States.Underground);
                }
                return;
            case Clips.Emerge:
                if (Clock.NormalizedTime >= 0.125f && !roared)
                {
                    roared = true;
                    Sound(Sounds.Rise);
                }
                if (Clock.HasFinished)
                {
                    Clock.CrossFade(pose.Clip(Clips.Idle), 0.5f, loop: true);
                    timer = 0;
                }
                if (!(timer > Warning) && !dirtSpawned)
                {
                    return;
                }
                (drawn, animating, contact) = (true, true, Contact.Emerge);  // as the original: it keeps striking as it idles after (BOSSES.md §2.5)
                if (!barriersCleared)
                {
                    ClearBarriers(2);
                    barriersCleared = true;
                }
                return;
        }
        if (Vitals.HitPoints <= 0)
        {
            deathClip = Clips.DieAbove;
            Change(States.Dead);
            return;
        }
        if (!Clock.IsCrossFading && bites <= 0)
        {
            Clock.CrossFade(pose.Clip(Clips.Submerge), 0.5f, loop: false);
            return;
        }
        if (Clock.CrossFadeClip?.Name == Clips.Submerge)
        {
            return;                                                           // ours: going under, it no longer chooses (a frozen blend lasts)
        }
        if (target is null)
        {
            return;
        }
        Turn(Flat(target.Position - Head.Position) ?? Vector3.UnitZ, 2, dt);
        if (!(timer > Warning + 0.8f))
        {
            return;
        }
        var distance = Vector3.Distance(target.Position, position);
        var probe = position + Forward(orientation) * 3;
        var blocked = Manager?.Query(probe, 2).Any(e => e is Spells.Effects.Barrier { IsSolid: true }) == true;
        Change((distance > 15 || blocked) && LastState != States.Spit ? States.Spit : States.Bite);
    }

    /// <summary>The spit: it turns to its wizard, sprays at 37.5% of its clip, and from 38% spits up to four gobs, half a second apart.</summary>
    private void Spit(float dt)
    {
        if (target is { } aim)
        {
            var progress = Clock.NormalizedTime;
            var towards = Flat(aim.Position - Head.Position) ?? Forward(orientation);
            Turn(towards, 1, dt);
            if (!Clock.IsCrossFading)
            {
                spitTimer -= dt * Clock.Speed;                                // ours: on its clip's time, so the volley keeps its shape at any speed
                var mouth = Head.Position;
                if (progress >= 0.375f && !sprayed)
                {
                    spray.Stop();
                    spray.Start(mouth, direction);
                    Sound(Sounds.Spit);
                    sprayed = true;
                }
                else if (spray.IsOn)
                {
                    spray.Start(mouth, direction);
                }
                if (progress >= 0.38f && spitTimer <= 0 && spat < 4)
                {
                    spat++;
                    spitTimer += 0.5f;
                    SpitAt(aim, mouth + towards * 0.5f);
                }
            }
        }
        if (Clock.HasFinished && !Clock.IsCrossFading)
        {
            Clock.CrossFade(pose.Clip(Clips.Idle), 0.5f, loop: true);
            Change(States.Risen);
        }
    }

    /// <summary>The bite: it turns to its wizard and snaps; from 40% of its clip it speeds up the more it is hurt.</summary>
    private void Bite(float dt)
    {
        if (target is { } prey)
        {
            Turn(Flat(prey.Position - position) ?? Vector3.UnitZ, 1.5f, dt);
        }
        if (!Clock.IsCrossFading && Clock.Time / pose.Clip(Clips.Bite).Duration >= 0.4f && !bitSound)
        {
            stateSpeed = 1 + Random.NextSingle() * 0.6f * (1 - Vitals.HitPoints / Vitals.MaxHitPoints);   // ours: the original's is overwritten (§2.7)
            Sound(Sounds.Bite);
            bitSound = true;
        }
        if (Clock.HasFinished && !Clock.IsCrossFading)
        {
            Clock.CrossFade(pose.Clip(Clips.Idle), 0.5f, loop: true);
            Change(States.Risen);
        }
    }

    /// <summary>
    /// It turns its root towards a way (flat), by a share of the way each second; cold halves the rate, frozen it does not
    /// turn at all.
    /// </summary>
    private void Turn(Vector3 towards, float rate, float dt)
    {
        if (Vitals.Has(StatusEffects.Frozen))
        {
            return;
        }
        if (Vitals.Has(StatusEffects.Cold))
        {
            rate *= 0.5f;
        }
        var wanted = Quaternion.CreateFromRotationMatrix(Facing(Vector3.Zero, towards));
        var now = Quaternion.CreateFromRotationMatrix(orientation);
        var at = orientation.Translation;
        orientation = Matrix4x4.CreateFromQuaternion(Quaternion.Lerp(now, wanted, Math.Clamp(dt * rate, 0, 1)));
        orientation.Translation = at;
        direction = Forward(orientation);
    }

    /// <summary>
    /// How far the leap turns this frame: 275° over its clip, at its clip's speed (ours: cold slows it and frozen stops
    /// it with the clip, where the original turns on regardless).
    /// </summary>
    private float Sweep(float dt) => LeapSweep / Clock.Clip!.Duration * dt * Clock.Speed;

    private static Vector3? Flat(Vector3 v)
    {
        v.Y = 0;
        var length = v.Length();
        return length > float.Epsilon ? v / length : null;
    }

    private static float ToRadians(float degrees) => degrees * MathF.PI / 180;
}
