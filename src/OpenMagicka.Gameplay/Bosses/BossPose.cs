using System.Numerics;
using OpenMagicka.Gameplay.Animation;

namespace OpenMagicka.Gameplay.Bosses;

/// <summary>
/// A boss's skinned model as its logic sees it: its skeleton and clips, the clock that plays them, and each frame the
/// bones placed in the world from a root, for its zones, its bodies and its picture. A simulation object.
/// </summary>
public sealed class BossPose
{
    private readonly IReadOnlyDictionary<string, AnimationClip> clips;
    private readonly BonePose[] poses;
    private readonly Matrix4x4[] placed;
    private readonly Matrix4x4[] skinned;
    private readonly Vector3[] bind;

    public BossPose(Skeleton skeleton, IReadOnlyDictionary<string, AnimationClip> clips)
    {
        Skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
        this.clips = clips ?? throw new ArgumentNullException(nameof(clips));
        var count = skeleton.Bones.Count;
        (poses, placed, skinned, bind) = (new BonePose[count], new Matrix4x4[count], new Matrix4x4[count], new Vector3[count]);
        for (var i = 0; i < count; i++)
        {
            bind[i] = Matrix4x4.Invert(skeleton.Bones[i].InverseBindPose, out var bone) ? bone.Translation : Vector3.Zero;
            placed[i] = Matrix4x4.Identity;
            skinned[i] = Matrix4x4.Identity;
        }
    }

    public Skeleton Skeleton { get; }

    public AnimationClock Clock { get; } = new();

    /// <summary>The root the bones were last placed from.</summary>
    public Matrix4x4 Root { get; private set; } = Matrix4x4.Identity;

    /// <summary>Each bone's skinning transform, as last placed.</summary>
    public ReadOnlySpan<Matrix4x4> Skinned => skinned;

    /// <summary>A clip by its exact name; a model without it is not the boss's.</summary>
    public AnimationClip Clip(string name) =>
        clips.TryGetValue(name, out var clip) ? clip : throw new InvalidDataException($"the boss's model has no clip {name}");

    /// <summary>A bone by name (any case); a model without it is not the boss's.</summary>
    public int Bone(string name) => Skeleton.IndexOf(name) is >= 0 and var index ? index : throw new InvalidDataException($"the boss's model has no bone {name}");

    /// <summary>A bone's place in the world, as last placed.</summary>
    public Vector3 Position(int bone) => placed[bone].Translation;

    /// <summary>A bone's frame in the world, as last placed.</summary>
    public Matrix4x4 Frame(int bone) => placed[bone];

    /// <summary>A bone's place in the model's bind pose.</summary>
    public Vector3 BindPosition(int bone) => bind[bone];

    /// <summary>The bones posed for the clock's time and placed from the root.</summary>
    public void Place(Matrix4x4 root)
    {
        Root = root;
        Skeleton.Sample(Clock, poses);
        Skeleton.Place(poses, root, placed, skinned);
    }
}
