using Content.Shared.Damage;
using Robust.Shared.Prototypes;

namespace Content.Server._SW.ModularSaber;

[RegisterComponent]
public sealed partial class SWSplitSaberStaffComponent : Component
{
    [DataField(required: true)] public EntProtoId HalfPrototype;
    [DataField] public DamageSpecifier JoinedDamageBonus = new();
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
