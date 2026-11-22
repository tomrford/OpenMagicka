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
/// An effect at a bone; an attached one follows it. Either ends with the window: an attached one stops, a loose one
/// (every one in the game's data) stays where it began and, if it loops or is infinite, stops (a gore spray from a
/// dying troll's mouth, a wizard's charge glow), else plays out.
/// </summary>
public sealed class PlayEffect(float start, float end, string effect, string bone, bool attach) : AnimationAction(start, end)
{
    public string Effect { get; } = effect ?? throw new ArgumentNullException(nameof(effect));

    public string Bone { get; } = bone ?? throw new ArgumentNullException(nameof(bone));

    public bool Attach { get; } = attach;

    public override void Run(Character owner, bool first)
    {
        if (first)
        {
            owner.RaiseCue(new EffectCue(Effect, Bone, Attach, this));
        }
    }

    /// <summary>A dying character holds its death clip's last pose: its effects end there all the same.</summary>
    public override bool EndsWithTheClip => true;

    public override void Stop(Character owner) => owner.RaiseCue(new EffectEndCue(this));
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
