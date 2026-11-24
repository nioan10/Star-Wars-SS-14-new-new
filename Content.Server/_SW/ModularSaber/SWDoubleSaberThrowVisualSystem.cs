using System.Linq;
using Content.Server._SW.Unpickuppable;
using Content.Shared._SW.ForceAbilities;
using Content.Shared._SW.ModularSaber;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Item.ItemToggle.Components;
using Content.Shared.Throwing;
using Content.Shared.Wieldable;
using Content.Shared.Wieldable.Components;
using Robust.Shared.Containers;

namespace Content.Server._SW.ModularSaber;

[RegisterComponent]
public sealed partial class SWDoubleSaberThrowVisualComponent : Component
{
    public EntityUid? ThrowUser;
    public bool ThrowScope;
    public bool WasActive;
    public bool WasWielded;
    public bool RestoreWieldOnReturn;
    public bool UnwieldedForThrow;
    public bool SuppressUnwieldDeactivation;
    public bool ThrowSucceeded;
    public bool RestoredForFlight;
    public EntityUid? ReturnUser;
    public bool CatchingPoweredStaff;
}

public sealed class SWDoubleSaberThrowVisualSystem : EntitySystem
{
    [Dependency] private readonly ItemToggleSystem _toggle = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SWDoubleSaberThrowVisualComponent, SWSaberThrowAttemptStartedEvent>(OnThrowStarted);
        SubscribeLocalEvent<SWDoubleSaberThrowVisualComponent, SWSaberThrowAttemptFinishedEvent>(OnThrowFinished);
        SubscribeLocalEvent<SWDoubleSaberThrowVisualComponent, ItemUnwieldedEvent>(OnUnwielded, before: [typeof(ItemToggleSystem)]);
        SubscribeLocalEvent<SWDoubleSaberThrowVisualComponent, ItemToggleDeactivateAttemptEvent>(OnDeactivateAttempt);
        SubscribeLocalEvent<SWDoubleSaberThrowVisualComponent, ThrownEvent>(OnThrown);
        SubscribeLocalEvent<SWDoubleSaberThrowVisualComponent, LandEvent>(OnLand, after: [typeof(SWAbilityThrowSwordSystem)]);
        SubscribeLocalEvent<BeingAttractedByForceComponent, ComponentShutdown>(OnAttractionShutdown);
        SubscribeLocalEvent<SWDoubleSaberThrowVisualComponent, StopThrowEvent>(OnStopThrow);
        SubscribeLocalEvent<SWDoubleSaberThrowVisualComponent, EntGotInsertedIntoContainerMessage>(OnInserted);
    }

    private void OnThrowStarted(Entity<SWDoubleSaberThrowVisualComponent> ent, ref SWSaberThrowAttemptStartedEvent args)
    {
        ent.Comp.ThrowScope = true;
        ent.Comp.ThrowUser = args.User;
        var splitStaff = HasComp<SWSplitSaberStaffComponent>(ent.Owner);
        ent.Comp.WasWielded = TryComp<WieldableComponent>(ent.Owner, out var wieldable) && wieldable.Wielded &&
                            (!splitStaff || _hands.EnumerateHeld(args.User).Any(held =>
                                TryComp<VirtualItemComponent>(held, out var grip) && grip.BlockingEntity == ent.Owner));
        ent.Comp.WasActive = _toggle.IsActivated(ent.Owner) && (ent.Comp.WasWielded || splitStaff);
        ent.Comp.UnwieldedForThrow = false;
        ent.Comp.SuppressUnwieldDeactivation = false;
        ent.Comp.ThrowSucceeded = false;
    }

    private void OnUnwielded(Entity<SWDoubleSaberThrowVisualComponent> ent, ref ItemUnwieldedEvent args)
    {
        if (!ent.Comp.ThrowScope || !ent.Comp.WasActive || ent.Comp.ThrowUser != args.User ||
            !args.Force || !_toggle.IsActivated(ent.Owner) || HasComp<SWSplitSaberStaffComponent>(ent.Owner))
            return;

        ent.Comp.UnwieldedForThrow = true;
        ent.Comp.SuppressUnwieldDeactivation = true;
    }

    private void OnDeactivateAttempt(Entity<SWDoubleSaberThrowVisualComponent> ent, ref ItemToggleDeactivateAttemptEvent args)
    {
        if (!ent.Comp.ThrowScope || !ent.Comp.SuppressUnwieldDeactivation || ent.Comp.ThrowUser != args.User)
            return;

        ent.Comp.SuppressUnwieldDeactivation = false;
        args.Cancelled = true;
        args.Silent = true;
    }

    private void OnThrown(Entity<SWDoubleSaberThrowVisualComponent> ent, ref ThrownEvent args)
    {
        if (!ent.Comp.ThrowScope || ent.Comp.ThrowUser != args.User)
            return;

        ent.Comp.ThrowSucceeded = true;
        var splitStaff = HasComp<SWSplitSaberStaffComponent>(ent.Owner);
        ent.Comp.RestoredForFlight = ent.Comp.WasActive && (splitStaff || ent.Comp.UnwieldedForThrow) &&
                                     _toggle.IsActivated(ent.Owner);
        ent.Comp.RestoreWieldOnReturn = splitStaff && ent.Comp.RestoredForFlight && ent.Comp.WasWielded;
        ent.Comp.ReturnUser = ent.Comp.RestoredForFlight || !splitStaff ? args.User : null;
        if (splitStaff && HasComp<SWAbilityThrowSwordComponent>(args.User))
            EnsureComp<SWUnpickuppableComponent>(ent.Owner);

        _appearance.SetData(ent.Owner, SWDoubleSaberThrowVisuals.Flying, false);
    }

    private void OnThrowFinished(Entity<SWDoubleSaberThrowVisualComponent> ent, ref SWSaberThrowAttemptFinishedEvent args)
    {
        if (!ent.Comp.ThrowScope || ent.Comp.ThrowUser != args.User)
            return;

        var rollback = ent.Comp.WasActive && !ent.Comp.ThrowSucceeded &&
                       (HasComp<SWSplitSaberStaffComponent>(ent.Owner)
                           ? !_hands.IsHolding(args.User, ent.Owner)
                           : ent.Comp.UnwieldedForThrow);
        ent.Comp.ThrowScope = false;
        ent.Comp.ThrowUser = null;
        ent.Comp.WasActive = false;
        ent.Comp.WasWielded = false;
        ent.Comp.UnwieldedForThrow = false;
        ent.Comp.SuppressUnwieldDeactivation = false;
        ent.Comp.ThrowSucceeded = false;
        if (rollback)
            _toggle.TryDeactivate(ent.Owner, predicted: false);
    }

    private void OnLand(Entity<SWDoubleSaberThrowVisualComponent> ent, ref LandEvent args)
    {
        var splitStaff = HasComp<SWSplitSaberStaffComponent>(ent.Owner);
        if (TryComp<BeingAttractedByForceComponent>(ent.Owner, out var attraction))
        {
            if (!splitStaff || attraction.User == ent.Comp.ReturnUser)
                return;
        }
        else if (splitStaff)
        {
            RemComp<SWUnpickuppableComponent>(ent.Owner);
        }

        ent.Comp.ReturnUser = null;
        EndFlight(ent);
    }

    private void OnStopThrow(Entity<SWDoubleSaberThrowVisualComponent> ent, ref StopThrowEvent args)
    {
        if (HasComp<BeingAttractedByForceComponent>(ent.Owner))
            return;

        if (HasComp<SWSplitSaberStaffComponent>(ent.Owner))
            RemComp<SWUnpickuppableComponent>(ent.Owner);
        ent.Comp.ReturnUser = null;
        EndFlight(ent);
    }

    private void OnInserted(Entity<SWDoubleSaberThrowVisualComponent> ent, ref EntGotInsertedIntoContainerMessage args)
    {
        EndFlight(ent);
    }

    private void OnAttractionShutdown(Entity<BeingAttractedByForceComponent> ent, ref ComponentShutdown args)
    {
        if (!TryComp<SWDoubleSaberThrowVisualComponent>(ent.Owner, out var visual))
            return;
        visual.ReturnUser = null;
        if (TryComp<ThrownItemComponent>(ent.Owner, out var thrown) && !thrown.Landed)
            return;
        EndFlight((ent.Owner, visual));
    }

    internal bool KeepsStaffPowerOnRelease(EntityUid staff, EntityUid user) =>
        TryComp<SWDoubleSaberThrowVisualComponent>(staff, out var visual) && visual.ThrowScope &&
        visual.ThrowUser == user && visual.WasActive;

    internal bool IsPoweredStaffReturn(Entity<SWDoubleSaberThrowVisualComponent> ent, EntityUid user) =>
        ent.Comp.ReturnUser == user && ent.Comp.RestoredForFlight && HasComp<SWSplitSaberStaffComponent>(ent.Owner);

    internal bool TryBeginPoweredStaffCatch(Entity<SWDoubleSaberThrowVisualComponent> ent, EntityUid user)
    {
        if (!IsPoweredStaffReturn(ent, user) || ent.Comp.CatchingPoweredStaff || !_toggle.IsActivated(ent.Owner))
            return false;

        ent.Comp.CatchingPoweredStaff = true;
        return true;
    }

    internal void FinishPoweredStaffCatch(Entity<SWDoubleSaberThrowVisualComponent> ent, bool caughtLit)
    {
        ent.Comp.CatchingPoweredStaff = false;
        ent.Comp.ReturnUser = null;
        ent.Comp.RestoreWieldOnReturn = false;
        if (caughtLit)
            ent.Comp.RestoredForFlight = false;
        else if (!TerminatingOrDeleted(ent.Owner))
            EndFlight(ent);
    }

    internal void CancelPoweredStaffReturn(EntityUid staff, EntityUid user)
    {
        if (!TryComp<SWDoubleSaberThrowVisualComponent>(staff, out var visual) || visual.ReturnUser != user)
            return;

        visual.ReturnUser = null;
        EndFlight((staff, visual));
    }

    private void EndFlight(Entity<SWDoubleSaberThrowVisualComponent> ent)
    {
        if (ent.Comp.CatchingPoweredStaff)
            return;

        _appearance.SetData(ent.Owner, SWDoubleSaberThrowVisuals.Flying, false);
        ent.Comp.RestoreWieldOnReturn = false;
        if (!ent.Comp.RestoredForFlight)
            return;
        ent.Comp.RestoredForFlight = false;
        _toggle.TryDeactivate(ent.Owner, predicted: false);
    }
}
