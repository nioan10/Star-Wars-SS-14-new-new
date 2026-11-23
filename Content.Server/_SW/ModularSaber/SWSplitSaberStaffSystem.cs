using Content.Shared.ActionBlocker;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Item.ItemToggle.Components;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Content.Shared.Wieldable.Components;
using Robust.Shared.Containers;

namespace Content.Server._SW.ModularSaber;

public sealed class SWSplitSaberStaffSystem : EntitySystem
{
    public const string LeadContainer = "staff-lead";
    public const string TailContainer = "staff-tail";
    public const string FrameContainer = "staff-frame";

    [Dependency] private readonly ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly ItemToggleSystem _toggle = default!;
    [Dependency] private readonly SWModularSaberSystem _modular = default!;

    private readonly record struct Pair(
        EntityUid Staff,
        EntityUid Lead,
        EntityUid Tail,
        ContainerSlot LeadSlot,
        ContainerSlot TailSlot,
        ContainerSlot FrameSlot)
    {
        public bool Joined => LeadSlot.ContainedEntity == Lead && TailSlot.ContainedEntity == Tail;
        public bool Separated => FrameSlot.ContainedEntity == Staff && !LeadSlot.ContainedEntity.HasValue &&
                                 !TailSlot.ContainedEntity.HasValue;
    }

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SWSplitSaberStaffComponent, GetVerbsEvent<AlternativeVerb>>(OnStaffVerbs);
        SubscribeLocalEvent<SWSplitSaberHalfComponent, GetVerbsEvent<AlternativeVerb>>(OnHalfVerbs);
        SubscribeLocalEvent<SWSplitSaberStaffComponent, ItemToggleActivateAttemptEvent>(OnActivate);
        SubscribeLocalEvent<SWSplitSaberStaffComponent, ItemToggledEvent>(OnToggled);
    }

    internal bool TryInitialize(Entity<SWSplitSaberStaffComponent> staff, EntityUid lead, EntityUid tail)
    {
        if (lead == tail || staff.Comp.Lead != null || staff.Comp.Tail != null ||
            !TryComp<SWSplitSaberHalfComponent>(lead, out var leadHalf) || leadHalf.Staff != null ||
            !TryComp<SWSplitSaberHalfComponent>(tail, out var tailHalf) || tailHalf.Staff != null ||
            !_modular.HasCompleteAssembly(lead) || !_modular.HasCompleteAssembly(tail))
            return false;

        staff.Comp.Lead = lead;
        staff.Comp.Tail = tail;
        leadHalf.Staff = staff.Owner;
        tailHalf.Staff = staff.Owner;
        return TryGetPair(staff, out var pair) && PutTogether(pair, restoring: false);
    }

    private bool TryGetPair(Entity<SWSplitSaberStaffComponent> staff, out Pair pair)
    {
        pair = default;
        if (staff.Comp.Lead is not { } lead || staff.Comp.Tail is not { } tail ||
            lead == tail || TerminatingOrDeleted(staff.Owner) || TerminatingOrDeleted(lead) || TerminatingOrDeleted(tail) ||
            !TryComp<SWSplitSaberHalfComponent>(lead, out var leadHalf) || leadHalf.Staff != staff.Owner ||
            !TryComp<SWSplitSaberHalfComponent>(tail, out var tailHalf) || tailHalf.Staff != staff.Owner ||
            !_containers.TryGetContainer(staff.Owner, LeadContainer, out var leadContainer) || leadContainer is not ContainerSlot leadSlot ||
            !_containers.TryGetContainer(staff.Owner, TailContainer, out var tailContainer) || tailContainer is not ContainerSlot tailSlot ||
            !_containers.TryGetContainer(lead, FrameContainer, out var frameContainer) || frameContainer is not ContainerSlot frameSlot)
            return false;

        pair = new Pair(staff.Owner, lead, tail, leadSlot, tailSlot, frameSlot);
        return true;
    }

    private void OnStaffVerbs(Entity<SWSplitSaberStaffComponent> staff, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || !_hands.IsHolding(args.User, staff.Owner))
            return;

        var user = args.User;
        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString("sw-split-saber-staff-separate"),
            Act = () =>
            {
                if (!TrySplit(user, staff.Owner))
                    _popup.PopupEntity(Loc.GetString("sw-split-saber-staff-separate-failed"), user, user);
            },
        });
    }

    private void OnHalfVerbs(Entity<SWSplitSaberHalfComponent> half, ref GetVerbsEvent<AlternativeVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || half.Comp.Staff == null || !_hands.IsHolding(args.User, half.Owner))
            return;

        var user = args.User;
        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString("sw-split-saber-staff-join"),
            Act = () =>
            {
                if (!TryJoin(user, half.Owner))
                    _popup.PopupEntity(Loc.GetString("sw-split-saber-staff-join-failed"), user, user);
            },
        });
    }

    public bool TrySplit(EntityUid user, EntityUid staffUid)
    {
        if (!TryComp<SWSplitSaberStaffComponent>(staffUid, out var staff) || staff.Changing ||
            !TryGetPair((staffUid, staff), out var pair) || !pair.Joined ||
            _toggle.IsActivated(staffUid) ||
            (TryComp<WieldableComponent>(staffUid, out var wieldable) && wieldable.Wielded) ||
            !_hands.IsHolding(user, staffUid, out var staffHand) || !_hands.TryGetEmptyHand(user, out var freeHand) ||
            !_actionBlocker.CanInteract(user, staffUid) || !_hands.CanDrop(user, staffUid) ||
            !_actionBlocker.CanPickup(user, pair.Lead) || !_actionBlocker.CanPickup(user, pair.Tail))
            return false;

        staff.Changing = true;
        var completed = false;
        try
        {
            completed = _hands.TryDrop(user, staffUid) && TakeApart(pair, restoring: false) &&
                        _hands.TryPickup(user, pair.Lead, staffHand, animate: false) &&
                        _hands.TryPickup(user, pair.Tail, freeHand, animate: false) &&
                        _hands.IsHolding(user, pair.Lead) && _hands.IsHolding(user, pair.Tail) && pair.Separated;
            return completed;
        }
        finally
        {
            if (!completed && PutTogether(pair, restoring: true) && !_hands.IsHolding(user, staffUid))
                _hands.TryPickup(user, staffUid, staffHand, animate: false);

            staff.Changing = false;
        }
    }

    public bool TryJoin(EntityUid user, EntityUid half)
    {
        if (!TryComp<SWSplitSaberHalfComponent>(half, out var link) || link.Staff is not { } staffUid ||
            !TryComp<SWSplitSaberStaffComponent>(staffUid, out var staff) || staff.Changing ||
            !TryGetPair((staffUid, staff), out var pair) || !pair.Separated || (half != pair.Lead && half != pair.Tail) ||
            !_hands.IsHolding(user, pair.Lead, out var leadHand) || !_hands.IsHolding(user, pair.Tail, out var tailHand) ||
            _toggle.IsActivated(pair.Lead) || _toggle.IsActivated(pair.Tail) ||
            !_modular.HasCompleteAssembly(pair.Lead) || !_modular.HasCompleteAssembly(pair.Tail) ||
            !_actionBlocker.CanInteract(user, half) || !_hands.CanDrop(user, pair.Lead) ||
            !_hands.CanDrop(user, pair.Tail) || !_actionBlocker.CanPickup(user, staffUid))
            return false;

        staff.Changing = true;
        var completed = false;
        try
        {
            completed = _hands.TryDrop(user, pair.Lead) && _hands.TryDrop(user, pair.Tail) &&
                        PutTogether(pair, restoring: false) &&
                        _hands.TryPickup(user, staffUid, half == pair.Lead ? leadHand : tailHand, animate: false) &&
                        _hands.IsHolding(user, staffUid) && pair.Joined;
            return completed;
        }
        finally
        {
            if (!completed && TakeApart(pair, restoring: true))
            {
                if (!_hands.IsHolding(user, pair.Lead))
                    _hands.TryPickup(user, pair.Lead, leadHand, animate: false);
                if (!_hands.IsHolding(user, pair.Tail))
                    _hands.TryPickup(user, pair.Tail, tailHand, animate: false);
            }

            staff.Changing = false;
        }
    }

    private bool PutTogether(Pair pair, bool restoring)
    {
        if (TerminatingOrDeleted(pair.Staff) || TerminatingOrDeleted(pair.Lead) || TerminatingOrDeleted(pair.Tail))
            return false;

        // Put the frame on the map first so the leading half can enter it without a container cycle.
        if (pair.FrameSlot.ContainedEntity == pair.Staff && !MoveOut(pair.Staff, pair.FrameSlot, restoring))
            return false;

        return (pair.LeadSlot.ContainedEntity == pair.Lead || _containers.Insert(pair.Lead, pair.LeadSlot, force: restoring)) &&
               (pair.TailSlot.ContainedEntity == pair.Tail || _containers.Insert(pair.Tail, pair.TailSlot, force: restoring));
    }

    private bool TakeApart(Pair pair, bool restoring)
    {
        if (TerminatingOrDeleted(pair.Staff) || TerminatingOrDeleted(pair.Lead) || TerminatingOrDeleted(pair.Tail))
            return false;

        if (pair.LeadSlot.ContainedEntity == pair.Lead && !MoveOut(pair.Lead, pair.LeadSlot, restoring))
            return false;
        if (pair.TailSlot.ContainedEntity == pair.Tail && !MoveOut(pair.Tail, pair.TailSlot, restoring))
            return false;

        return pair.FrameSlot.ContainedEntity == pair.Staff || _containers.Insert(pair.Staff, pair.FrameSlot, force: restoring);
    }

    private bool MoveOut(EntityUid item, ContainerSlot slot, bool force)
    {
        var coordinates = _transform.ToCoordinates(_transform.GetMapCoordinates(item));
        return _containers.Remove(item, slot, force: force, destination: coordinates);
    }

    private void OnActivate(Entity<SWSplitSaberStaffComponent> staff, ref ItemToggleActivateAttemptEvent args)
    {
        if (args.Cancelled)
            return;

        if (!TryComp<WieldableComponent>(staff.Owner, out var wieldable) || !wieldable.Wielded ||
            !TryGetPair(staff, out var pair) || !pair.Joined ||
            !_modular.HasCompleteAssembly(pair.Tail) ||
            !_modular.TryApplyAssembly(pair.Lead, staff.Owner, staff.Comp.JoinedDamageBonus))
        {
            args.Cancelled = true;
            args.Popup = Loc.GetString("sw-split-saber-staff-incomplete");
        }
    }

    private void OnToggled(Entity<SWSplitSaberStaffComponent> staff, ref ItemToggledEvent args) =>
        _modular.UpdateAttackRate(staff.Owner, args.Activated ? staff.Comp.Lead : null);
}
