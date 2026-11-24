using System.Linq;
using Content.Shared.Damage;
using Robust.Shared.GameStates;

namespace Content.Shared._SW.ModularSaber;

/// <summary>Identifies the assembled staff and its two matching hilts without resolving a hidden entity.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState, UnsavedComponent]
public sealed partial class SWSplitSaberFamilyComponent : Component
{
    [AutoNetworkedField] public NetEntity Family = NetEntity.Invalid;
}

/// <summary>Attack recovery belongs to the wielder and this particular staff family.</summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState, UnsavedComponent]
public sealed partial class SWSplitSaberCooldownComponent : Component
{
    [AutoNetworkedField] public Dictionary<NetEntity, TimeSpan> NextAttacks = new();
}

public sealed class SWSplitSaberCombatSystem : EntitySystem
{
    public void LinkMember(EntityUid weapon, EntityUid staff)
    {
        var family = GetNetEntity(staff);
        var member = EnsureComp<SWSplitSaberFamilyComponent>(weapon);
        if (member.Family == family)
            return;

        member.Family = family;
        Dirty(weapon, member);
    }

    public bool TryGetFamily(EntityUid weapon, out NetEntity family)
    {
        family = NetEntity.Invalid;
        if (TryComp<SWSplitSaberFamilyComponent>(weapon, out var member))
            family = member.Family;
        return family != NetEntity.Invalid;
    }

    public TimeSpan GetNextAttack(EntityUid user, NetEntity family) =>
        TryComp<SWSplitSaberCooldownComponent>(user, out var cooldown) &&
        cooldown.NextAttacks.TryGetValue(family, out var next) ? next : default;

    public void RecordAttack(EntityUid user, NetEntity family, TimeSpan now, TimeSpan next)
    {
        var cooldown = EnsureComp<SWSplitSaberCooldownComponent>(user);
        var expired = cooldown.NextAttacks.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray();
        foreach (var key in expired)
            cooldown.NextAttacks.Remove(key);
        cooldown.NextAttacks[family] = next;
        Dirty(user, cooldown);
    }

    public SWSplitSaberCounterDamageEvent GetCounterDamage(EntityUid weapon, EntityUid user)
    {
        var counter = new SWSplitSaberCounterDamageEvent(user);
        if (HasComp<SWSplitSaberFamilyComponent>(weapon))
            RaiseLocalEvent(weapon, ref counter);
        return counter;
    }
}

/// <summary>Queried for one eligible melee damage target, after a successful defense can cancel the swing.</summary>
[ByRefEvent]
public record struct SWSplitSaberCounterDamageEvent(EntityUid User)
{
    public DamageSpecifier? Bonus;
    public Action? OnDamageApplied;
}
