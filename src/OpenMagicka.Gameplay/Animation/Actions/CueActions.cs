using OpenMagicka.Gameplay.Characters;

namespace OpenMagicka.Gameplay.Animation.Actions;

/// <summary>A sound cue at the character.</summary>
public sealed class PlaySound(float start, float end, string cue, int banks) : AnimationAction(start, end)
{
    public string Cue { get; } = cue ?? throw new ArgumentNullException(nameof(cue));

    public int Banks { get; } = banks;

    public override void Run(Character owner, bool first)
    {
        if (first)
        {
            owner.RaiseCue(new SoundCue(Cue, Banks, owner.Position, owner.Body.Facing));
        }
    }
}

/// <summary>
/// An effect at a bone, as the original: its window's length decides, not its attach flag (read, never used). A window
/// with a length follows the bone and stops the effect when it ends, whatever its kind (a dying troll's gore spray, a
/// wizard's charge glow); an empty one starts it once at the bone and leaves it to play out.
/// </summary>
public sealed class PlayEffect(float start, float end, string effect, string bone, bool attach) : AnimationAction(start, end)
{
    public string Effect { get; } = effect ?? throw new ArgumentNullException(nameof(effect));

    public string Bone { get; } = bone ?? throw new ArgumentNullException(nameof(bone));

    /// <summary>The data's flag; as the original, it changes nothing.</summary>
    public bool Attach { get; } = attach;

    /// <summary>The window has a length: the effect follows the bone and is stopped when it ends.</summary>
    public bool Follows => End > Start;

    public override void Run(Character owner, bool first)
    {
        if (first)
        {
            owner.RaiseCue(new EffectCue(Effect, Bone, Follows, this));
        }
    }

    /// <summary>
    /// Ours: a clip holding its last pose (a corpse's death clip) ends a window that reaches its end; the original's
    /// would run on till the corpse is removed.
    /// </summary>
    public override bool EndsWithTheClip => true;

    public override void Stop(Character owner)
    {
        if (Follows)
        {
            owner.RaiseCue(new EffectEndCue(this));
        }
    }
}

/// <summary>A step, heard and seen only on the ground and not levitating.</summary>
public sealed class Footstep(float start, float end) : AnimationAction(start, end)
{
    public override void Run(Character owner, bool first)
    {
        var body = owner.Body;
        if (first && body.IsTouchingGround && !body.Conditions.IsLevitating)
        {
            var feet = owner.Position with { Y = owner.Position.Y + body.HeightOffset };
            var depth = body.IsInWater ? body.WaterDepth : 0;
            owner.RaiseCue(new FootstepCue(body.GroundMaterial ?? 0, body.Body.Mass, feet with { Y = feet.Y + depth }, body.Facing, depth));   // a splash on the surface
        }
    }
}

/// <summary>Shakes the camera from the character's position.</summary>
public sealed class CameraShake(float start, float end, float duration, float magnitude) : AnimationAction(start, end)
{
    public float Duration { get; } = Finite(duration, nameof(duration));

    public float Magnitude { get; } = Finite(magnitude, nameof(magnitude));

    public override void Run(Character owner, bool first)
    {
        if (first)
        {
            owner.RaiseCue(new CameraShakeCue(owner.Position, Magnitude, Duration));
        }
    }
}
