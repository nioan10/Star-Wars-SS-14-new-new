using Content.Shared.Actions;
using Content.Shared.Throwing;
using Robust.Shared.Physics.Systems;
using Content.Shared._SW;
using Content.Shared._SW.ForceAbilities;
using Content.Server.Hands.Systems;
using Content.Shared.Item;
using Content.Server._SW.ModularSaber;
using Content.Server._SW.Unpickuppable;
using Robust.Shared.Containers;

namespace Content.Server._SW;

public sealed partial class SWAbilityThrowSwordSystem : EntitySystem
{
    [Dependency] private readonly ForceAbilityDistanceInteractSystem _force = default!;
    [Dependency] private readonly SharedPhysicsSystem _physicsSystem = default!;
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SWAbilityThrowSwordComponent, BeforeThrowEvent>(BeforeThrow);
        SubscribeLocalEvent<SWSwordThrowableComponent, LandEvent>(OnLand);
        SubscribeLocalEvent<SWSplitSaberHalfComponent, ThrownEvent>(OnPairedSaberThrown);
        SubscribeLocalEvent<SWSplitSaberHalfComponent, EntGotInsertedIntoContainerMessage>(OnPairedSaberInserted);
    }

    private void BeforeThrow(Entity<SWAbilityThrowSwordComponent> ent, ref BeforeThrowEvent args)
    {
        if (HasComp<SWSwordThrowableComponent>(args.ItemUid))
        {
            //We do it to avoid
            //that someone can catch sword while it is throwed
            EnsureComp<SWUnpickuppableComponent>(args.ItemUid);
        }
    }

    private void OnPairedSaberThrown(Entity<SWSplitSaberHalfComponent> ent, ref ThrownEvent args) =>
        ent.Comp.ReturnGeneration++;

    private void OnPairedSaberInserted(Entity<SWSplitSaberHalfComponent> ent, ref EntGotInsertedIntoContainerMessage args) =>
        ent.Comp.ReturnGeneration++;

    private void OnLand(Entity<SWSwordThrowableComponent> ent, ref LandEvent args)
    {
        if (args.User == null)
        {
            return;
        }
        if (TryComp<SWSplitSaberHalfComponent>(ent.Owner, out var half) && half.Staff is { } staff &&
            !TerminatingOrDeleted(staff))
        {
            // Release the flight lock while queued. An ongoing attraction owns its own lock.
            if (!TryComp<BeingAttractedByForceComponent>(ent.Owner, out var attraction) || attraction.User == null)
                RemComp<SWUnpickuppableComponent>(ent);
            _force.EnqueuePairedSaberReturn(args.User.Value, ent.Owner, half.ReturnGeneration);
        }
        else
        {
            RemComp<SWUnpickuppableComponent>(ent);
            _force.StartAttractingByForce(args.User.Value, ent.Owner);
        }
        _physicsSystem.ApplyAngularImpulse(ent.Owner, 2f);
    }
}
