using Content.Shared.Damage;
using Robust.Shared.Prototypes;

namespace Content.Server._SW.ModularSaber;

[RegisterComponent]
public sealed partial class SWSplitSaberStaffComponent : Component
{
    [DataField(required: true)] public EntProtoId HalfPrototype;
    [DataField] public DamageSpecifier JoinedDamageBonus = new();
    [DataField] public float WieldedArcBonusDegrees;
    [DataField] public float WieldedBlockBonusSeconds;
    [DataField] public DamageSpecifier PairedCounterDamage = new();
    [DataField] public float PairedCounterSeconds;

    [DataField] public Angle? BaseAttackAngle;
    public EntityUid? CounterUser;
    public TimeSpan CounterUntil;
    public uint CounterGeneration;
    [DataField] public EntityUid? Lead;
    [DataField] public EntityUid? Tail;

    public bool Changing;
}

[RegisterComponent]
public sealed partial class SWSplitSaberHalfComponent : Component
{
    [DataField] public EntityUid? Staff;

    public uint ReturnGeneration;
}
