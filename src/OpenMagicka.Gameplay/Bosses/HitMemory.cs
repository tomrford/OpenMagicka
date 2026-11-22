using OpenMagicka.Gameplay.Entities;

namespace OpenMagicka.Gameplay.Bosses;

/// <summary>Who a boss's part hit lately: each is spared until its time runs out (a quarter second, as the original's).</summary>
public sealed class HitMemory(float time = HitMemory.DefaultTime)
{
    public const float DefaultTime = 0.25f;

    private readonly Dictionary<Entity, float> hit = new(ReferenceEqualityComparer.Instance);

    public float Time { get; } = time > 0 && float.IsFinite(time) ? time : throw new ArgumentOutOfRangeException(nameof(time), time, "must be positive");

    public int Count => hit.Count;

    public bool Remembers(Entity entity) => hit.ContainsKey(entity);

    /// <summary>The entity was hit now: spared for the memory's time.</summary>
    public void Remember(Entity entity) => hit[entity] = Time;

    public void Forget() => hit.Clear();

    /// <summary>Time passes: the ones whose time ran out are forgotten.</summary>
    public void Update(float dt)
    {
        if (hit.Count == 0)
        {
            return;
        }
        foreach (var entity in hit.Keys.ToList())
        {
            if ((hit[entity] -= dt) <= 0)
            {
                hit.Remove(entity);
            }
        }
    }
}
