using OpenMagicka.Gameplay.Combat;
using OpenMagicka.Gameplay.Spells;

namespace OpenMagicka.Gameplay.Bosses;

/// <summary>What one entry of damage did to a boss: its result, the hit points it took (negative: healed), and whether it draws blood.</summary>
public readonly record struct BossHit(DamageResult Result, float Dealt, bool Bleeds);

/// <summary>
/// A named boss's hit points, resistances and statuses (docs/gameplay/BOSSES.md §1), by the bosses' own rules, not
/// the characters': an entry's amount is shifted by each of its elements' modifiers and its magnitude scaled by their
/// mean multiplier; a status attack sets the statuses of its elements the boss is not immune to; lightning hurts the
/// wet twice, life thins poison, and a physical blow on the frozen loses 200 and counts thrice (at least once). The
/// statuses the rules look at are those of the last <see cref="Update"/>, as the original's; Wet counts from a
/// magnitude of 1. A simulation object: it changes in place.
/// </summary>
public sealed class BossVitals
{
    /// <summary>How long new cold is kept off after a fire went out.</summary>
    public const float DryingTime = StatusSet.DryingTime;

    /// <summary>A frozen boss shrugs off this much of each physical blow, then takes the rest thrice.</summary>
    public const float FrozenArmour = 200, FrozenFactor = 3;

    public BossVitals(float maxHitPoints, ResistanceTable resistances)
    {
        MaxHitPoints = maxHitPoints > 0 && float.IsFinite(maxHitPoints) ? maxHitPoints : throw new ArgumentOutOfRangeException(nameof(maxHitPoints), maxHitPoints, "must be positive");
        Resistances = resistances ?? throw new ArgumentNullException(nameof(resistances));
        HitPoints = maxHitPoints;
    }

    public float MaxHitPoints { get; }

    public float HitPoints { get; private set; }

    public ResistanceTable Resistances { get; }

    public StatusSet Statuses { get; private set; } = StatusSet.Empty;

    /// <summary>The statuses as the last update saw them: the ones the rules test.</summary>
    public StatusEffects Active { get; private set; }

    /// <summary>A status at work as of the last update (Wet from a magnitude of 1).</summary>
    public bool Has(StatusEffects type) => (Active & type) == type && type != StatusEffects.None;

    /// <summary>A status's magnitude now (0 when it is not on).</summary>
    public float Magnitude(StatusEffects type) => Statuses[type].Magnitude;

    /// <summary>Whole again, with no status: a new fight.</summary>
    public void Reset()
    {
        HitPoints = MaxHitPoints;
        ClearStatuses();
        Statuses = Statuses.Drying(0);
    }

    /// <summary>Every status ends at once (the boss went under the ground).</summary>
    public void ClearStatuses() => (Statuses, Active) = (Statuses.Cleared(), StatusEffects.None);

    /// <summary>
    /// Hit points taken straight off (a status's burn, a boss's own ram): no resistance; fire burns the greased twice.
    /// The hit points never rise above the most. Returns what was taken.
    /// </summary>
    public float Hurt(float amount, Elements element)
    {
        if ((element & Elements.Fire) == Elements.Fire && Has(StatusEffects.Greased))
        {
            amount *= 2;
        }
        HitPoints = MathF.Min(HitPoints - amount, MaxHitPoints);
        return amount;
    }

    /// <summary>One entry of damage by the bosses' rules; whether the boss can be hurt at all is its own to say first.</summary>
    public BossHit Damage(DamageEntry damage)
    {
        if (!float.IsFinite(damage.Amount) || !float.IsFinite(damage.Magnitude))
        {
            throw new ArgumentOutOfRangeException(nameof(damage), damage, "the amount and magnitude must be finite");
        }
        var (amount, magnitude, multipliers) = Resist(damage);
        if (MathF.Abs(magnitude) <= float.Epsilon)
        {
            return new BossHit(DamageResult.Deflected, 0, false);
        }
        var result = DamageResult.None;
        if (damage.Attack.HasFlag(AttackProperties.Status) && MathF.Abs(multipliers) > float.Epsilon)
        {
            result |= SetStatuses(damage.Element, amount, magnitude);
        }
        var (dealt, bleeds) = (0f, false);
        if (damage.Attack.HasFlag(AttackProperties.Damage))
        {
            if ((damage.Element & Elements.Lightning) != 0 && Has(StatusEffects.Wet))
            {
                amount *= 2;
            }
            if ((damage.Element & Elements.Life) != 0)
            {
                var poison = Statuses[StatusEffects.Poisoned];
                Statuses = Statuses.With(poison with { Type = StatusEffects.Poisoned, Magnitude = poison.Magnitude - magnitude });
            }
            if ((damage.Element & ElementSets.Physical) != 0)
            {
                if (Has(StatusEffects.Frozen))
                {
                    (amount, magnitude) = (MathF.Max(amount - FrozenArmour, 0) * FrozenFactor, MathF.Max(1, magnitude));
                }
                else
                {
                    bleeds = true;
                }
            }
            dealt = amount * magnitude;
            amount = dealt;
            HitPoints -= dealt;
            result |= DamageResult.Hit | (dealt > 0 ? DamageResult.Damaged : dealt < 0 ? DamageResult.Healed : DamageResult.Deflected);
            if (damage.Attack.HasFlag(AttackProperties.Piercing) && magnitude > 0 && dealt > 0)
            {
                result |= DamageResult.Pierced;
            }
        }
        HitPoints = MathF.Min(HitPoints, MaxHitPoints);
        if (amount == 0)
        {
            result |= DamageResult.Deflected;                                // as the original: a blow of no amount, a status's too
        }
        if (HitPoints <= 0)
        {
            result |= DamageResult.Killed;
        }
        return new BossHit(result, dealt, bleeds);
    }

    /// <summary>
    /// The statuses wear on and burn (straight off the hit points); a dead boss keeps none. The rules see what is left
    /// from now on.
    /// </summary>
    public void Update(float dt, bool dead)
    {
        if (dead)
        {
            ClearStatuses();
            Statuses = Statuses.Drying(Statuses.DryTimer - dt);
            return;
        }
        var tick = Statuses.Update(dt, HitPoints, MaxHitPoints);
        Statuses = tick.Set;
        foreach (var damage in tick.Damage)
        {
            Hurt(damage.Amount, damage.Element);
        }
        var active = StatusEffects.None;
        for (var type = StatusEffects.Burning; type <= StatusEffects.Bleeding; type = (StatusEffects)((int)type << 1))
        {
            var slot = Statuses[type];
            if (slot.IsActive && (type != StatusEffects.Wet || slot.Magnitude >= 1))
            {
                active |= type;
            }
        }
        Active = active;
    }

    /// <summary>
    /// The resistances of each element of the entry: its modifier shifts the amount (by whole points; earth's alone keeps
    /// it at or above 0), the mean of their multipliers scales the magnitude. Returns the sum of the multipliers too.
    /// </summary>
    private (float Amount, float Magnitude, float Multipliers) Resist(DamageEntry damage)
    {
        var (amount, sum, count) = (damage.Amount, 0f, 0);
        for (var i = 0; i < Spells.ElementIndex.Count; i++)
        {
            var element = (Elements)(1 << i);
            if ((damage.Element & element) != element)
            {
                continue;
            }
            var resistance = Resistances[element];
            amount = damage.Element == Elements.Earth && resistance.Modifier != 0
                ? MathF.Truncate(MathF.Max(amount + resistance.Modifier, 0))
                : amount + MathF.Truncate(resistance.Modifier);
            (sum, count) = (sum + resistance.Multiplier, count + 1);
        }
        return (amount, count > 0 ? damage.Magnitude * sum / count : damage.Magnitude, sum);
    }

    /// <summary>The statuses an attack's elements set, each only where the boss is not immune to that element.</summary>
    private DamageResult SetStatuses(Elements elements, float amount, float magnitude)
    {
        var result = DamageResult.None;
        foreach (var (element, type) in StatusSources)
        {
            if ((elements & element) == element && Resistances[element].Multiplier > float.Epsilon)
            {
                result |= AddStatus(StatusEffect.Create(type, amount, magnitude));
            }
        }
        return result;
    }

    private static readonly (Elements Element, StatusEffects Status)[] StatusSources =
    [
        (Elements.Fire, StatusEffects.Burning), (Elements.Cold, StatusEffects.Cold), (Elements.Water, StatusEffects.Wet),
        (Elements.Poison, StatusEffects.Poisoned), (Elements.Life, StatusEffects.Healing), (Elements.Steam, StatusEffects.Wet),
    ];

    /// <summary>
    /// A status meets the others, by the bosses' rules: fire puts out cold, wet and frozen (or is put out while drying);
    /// cold puts out fire (drying starts), is scaled by its resistance, freezes the wet and feeds the frozen; wet puts out
    /// fire and washes grease off; grease dries the wet; poison is scaled by its resistance; healing cures poison. What
    /// was put out instead of taking is reported as removed.
    /// </summary>
    public DamageResult AddStatus(StatusEffect effect)
    {
        if (!effect.IsActive)
        {
            return DamageResult.None;
        }
        var removed = false;
        switch (effect.Type)
        {
            case StatusEffects.Burning:
                if (Alive(StatusEffects.Cold) || Alive(StatusEffects.Wet) || Alive(StatusEffects.Frozen) || Statuses.IsDrying)
                {
                    End(StatusEffects.Cold);
                    End(StatusEffects.Wet);
                    End(StatusEffects.Frozen);
                    removed = true;
                }
                break;
            case StatusEffects.Cold:
                var magnitude = effect.Magnitude;
                if (Alive(StatusEffects.Burning) || Statuses.IsDrying)
                {
                    End(StatusEffects.Burning);
                    Statuses = Statuses.Drying(DryingTime);
                    removed = true;
                }
                magnitude *= Resistances[Elements.Cold].Multiplier;
                if (Has(StatusEffects.Wet))
                {
                    Statuses = Statuses.With(new StatusEffect(StatusEffects.Frozen, 0, magnitude));
                    End(StatusEffects.Wet);
                }
                if (Has(StatusEffects.Frozen))
                {
                    var frozen = Statuses[StatusEffects.Frozen];
                    Statuses = Statuses.With(frozen with { Type = StatusEffects.Frozen, Magnitude = frozen.Magnitude + magnitude });
                    magnitude = 0;
                }
                effect = effect with { Magnitude = magnitude };
                break;
            case StatusEffects.Wet:
                if (Has(StatusEffects.Burning) || Statuses.IsDrying)
                {
                    End(StatusEffects.Burning);
                    removed = true;
                }
                if (Has(StatusEffects.Greased))
                {
                    End(StatusEffects.Greased);
                }
                break;
            case StatusEffects.Greased:
                if (Has(StatusEffects.Wet))
                {
                    End(StatusEffects.Wet);
                }
                break;
            case StatusEffects.Poisoned:
                effect = effect with { Magnitude = effect.Magnitude * Resistances[Elements.Poison].Multiplier };
                break;
            case StatusEffects.Healing:
                if (Has(StatusEffects.Poisoned))
                {
                    End(StatusEffects.Poisoned);
                }
                break;
        }
        if (removed)
        {
            return DamageResult.StatusRemoved;
        }
        Statuses = Statuses.With(Statuses[effect.Type] + effect);
        return DamageResult.StatusAdded;
    }

    private bool Alive(StatusEffects type) => Statuses[type].IsActive;

    private void End(StatusEffects type) => Statuses = Statuses.With(default(StatusEffect) with { Type = type });
}
