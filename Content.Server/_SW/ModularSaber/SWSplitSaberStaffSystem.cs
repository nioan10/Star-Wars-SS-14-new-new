using System.Diagnostics.CodeAnalysis;
using Content.Shared._SW.ModularSaber;
using Content.Shared.ActionBlocker;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Inventory.VirtualItem;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Item.ItemToggle.Components;
using Content.Shared.Popups;
using Content.Shared.Verbs;
using Content.Shared.Wieldable;
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
    [Dependency] private readonly SharedWieldableSystem _wieldable = default!;
    [Dependency] private readonly SWModularSaberSystem _modular = default!;

    [Dependency] private readonly SharedAppearanceSystem _appearance = default!;

    private static readonly (string Slot, string Label)[] PartSlots =
    {
        ("saber-crystal", "sw-modular-saber-slot-crystal"),
        ("saber-emitter", "sw-modular-saber-slot-emitter"),
        ("saber-focus", "sw-modular-saber-slot-focus"),
    };

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
        SubscribeLocalEvent<SWSplitSaberStaffComponent, GetVerbsEvent<InteractionVerb>>(OnInsertVerbs);
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
        if (!args.CanAccess || !args.CanInteract)
            return;

        var user = args.User;
        if (_hands.IsHolding(user, staff.Owner))
        {
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

        if (TryGetServicePair(user, staff.Owner, out var pair))
        {
            AddEjectVerbs(user, pair, pair.Lead, leading: true, args.Verbs);
            AddEjectVerbs(user, pair, pair.Tail, leading: false, args.Verbs);
        }
    }

    private void AddEjectVerbs(EntityUid user, Pair pair, EntityUid hilt, bool leading, SortedSet<AlternativeVerb> verbs)
    {
        foreach (var (slotId, label) in PartSlots)
        {
            if (!_modular.TryGetPartSlot(hilt, slotId, out var slot) || slot.Item is not { } part)
                continue;

            verbs.Add(new AlternativeVerb
            {
                Text = PartVerbText("sw-split-saber-staff-eject-part", leading, label),
                Category = VerbCategory.Eject,
                IconEntity = GetNetEntity(part),
                Act = () => TryEjectPart(user, pair.Staff, leading, slotId, part),
            });
        }
    }

    private void OnInsertVerbs(Entity<SWSplitSaberStaffComponent> staff, ref GetVerbsEvent<InteractionVerb> args)
    {
        if (!args.CanAccess || !args.CanInteract || args.Using is not { } item ||
            !TryGetServicePair(args.User, staff.Owner, out var pair))
            return;

        AddInsertVerb(args.User, pair, pair.Lead, item, leading: true, args.Verbs);
        AddInsertVerb(args.User, pair, pair.Tail, item, leading: false, args.Verbs);
    }

    private void AddInsertVerb(EntityUid user, Pair pair, EntityUid hilt, EntityUid item, bool leading, SortedSet<InteractionVerb> verbs)
    {
        if (!_modular.TryGetInsertSlot(hilt, item, out var slot))
            return;

        foreach (var (slotId, label) in PartSlots)
        {
            if (slot.ID != slotId)
                continue;

            verbs.Add(new InteractionVerb
            {
                Text = PartVerbText("sw-split-saber-staff-insert-part", leading, label),
                Category = VerbCategory.Insert,
                IconEntity = GetNetEntity(item),
                Act = () => TryInsertPart(user, pair.Staff, item, leading),
            });
            return;
        }
    }

    private static string PartVerbText(string verb, bool leading, string part) =>
        Loc.GetString(verb,
            ("half", Loc.GetString(leading ? "sw-split-saber-staff-leading" : "sw-split-saber-staff-trailing")),
            ("part", Loc.GetString(part)));

    private bool TryGetServicePair(EntityUid user, EntityUid staff, out Pair pair)
    {
        pair = default;
        return TryComp<SWSplitSaberStaffComponent>(staff, out var assembly) && !assembly.Changing &&
               TryGetPair((staff, assembly), out pair) && pair.Joined &&
               !_toggle.IsActivated(pair.Lead) && !_toggle.IsActivated(pair.Tail) &&
               _modular.CanModify(staff, user);
    }

    public bool TryInsertPart(EntityUid user, EntityUid staff, EntityUid item, bool leading)
    {
        if (!TryGetServicePair(user, staff, out var pair) || !_actionBlocker.CanUseHeldEntity(user, item))
            return false;

        var half = leading ? pair.Lead : pair.Tail;
        return _modular.TryGetInsertSlot(half, item, out var slot) &&
               _modular.TryInsertPart(half, staff, user, item, slot);
    }

    public bool TryEjectPart(EntityUid user, EntityUid staff, bool leading, string slotId, EntityUid? expected = null)
    {
        if (!TryGetServicePair(user, staff, out var pair))
            return false;

        var half = leading ? pair.Lead : pair.Tail;
        return _modular.TryGetPartSlot(half, slotId, out var slot) &&
               _modular.TryEjectPart(half, staff, user, slot, expected);
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
            !TryComp<WieldableComponent>(staffUid, out var wieldable) ||
            !_hands.IsHolding(user, staffUid, out var staffHand) ||
            !TryGetSplitHand(user, staffUid, wieldable.Wielded, out var freeHand, out var grip) ||
            !_actionBlocker.CanInteract(user, staffUid) || !_hands.CanDrop(user, staffUid) ||
            !_actionBlocker.CanPickup(user, pair.Lead) || !_actionBlocker.CanPickup(user, pair.Tail))
            return false;

        var wasActive = _toggle.IsActivated(staffUid);
        var wasWielded = wieldable.Wielded;
        staff.Changing = true;
        var completed = false;
        try
        {
            completed = ReleaseGrip(user, staffUid, wieldable, grip) &&
                        SetActive(user, staffUid, false) &&
                        _hands.TryDrop(user, staffUid) && TakeApart(pair, restoring: false) &&
                        _hands.TryPickup(user, pair.Lead, staffHand, animate: false) &&
                        _hands.TryPickup(user, pair.Tail, freeHand, animate: false) &&
                        SetActive(user, pair.Lead, wasActive) && SetActive(user, pair.Tail, wasActive) &&
                        _hands.IsHolding(user, pair.Lead) && _hands.IsHolding(user, pair.Tail) && pair.Separated;
            return completed;
        }
        finally
        {
            if (!completed)
            {
                SetActive(user, pair.Lead, false);
                SetActive(user, pair.Tail, false);
                if (PutTogether(pair, restoring: true) &&
                    (_hands.IsHolding(user, staffUid) || _hands.TryPickup(user, staffUid, staffHand, animate: false)))
                {
                    if (wasWielded && !wieldable.Wielded)
                        TryWieldStaff(user, staffUid, wieldable);
                    SetActive(user, staffUid, wasActive);
                }
            }

            staff.Changing = false;
        }
    }

    public bool TryJoin(EntityUid user, EntityUid half)
    {
        if (!TryComp<SWSplitSaberHalfComponent>(half, out var link) || link.Staff is not { } staffUid ||
            !TryComp<SWSplitSaberStaffComponent>(staffUid, out var staff) || staff.Changing ||
            !TryGetPair((staffUid, staff), out var pair) || !pair.Separated || (half != pair.Lead && half != pair.Tail) ||
            !TryComp<WieldableComponent>(staffUid, out var wieldable) || wieldable.Wielded ||
            !_hands.IsHolding(user, pair.Lead, out var leadHand) || !_hands.IsHolding(user, pair.Tail, out var tailHand) ||
            !_modular.HasCompleteAssembly(pair.Lead) || !_modular.HasCompleteAssembly(pair.Tail) ||
            !_actionBlocker.CanInteract(user, half) || !_hands.CanDrop(user, pair.Lead) ||
            !_hands.CanDrop(user, pair.Tail) || !_actionBlocker.CanPickup(user, staffUid))
            return false;

        var leadActive = _toggle.IsActivated(pair.Lead);
        var tailActive = _toggle.IsActivated(pair.Tail);
        var staffHand = half == pair.Lead ? leadHand : tailHand;
        staff.Changing = true;
        var completed = false;
        try
        {
            completed = SetActive(user, pair.Lead, false) && SetActive(user, pair.Tail, false) &&
                        _hands.TryDrop(user, pair.Lead) && _hands.TryDrop(user, pair.Tail) &&
                        PutTogether(pair, restoring: false) &&
                        _hands.TryPickup(user, staffUid, staffHand, animate: false) &&
                        (!(leadActive || tailActive) ||
                         (TryWieldStaff(user, staffUid, wieldable) && _toggle.IsActivated(staffUid))) &&
                        _hands.IsHolding(user, staffUid) && pair.Joined;
            return completed;
        }
        finally
        {
            if (!completed)
            {
                if (wieldable.Wielded)
                    ReleaseGrip(user, staffUid, wieldable, FindGripPlaceholder(user, staffUid), force: true);
                SetActive(user, staffUid, false);
                if (TakeApart(pair, restoring: true))
                {
                    if (!_hands.IsHolding(user, pair.Lead))
                        _hands.TryPickup(user, pair.Lead, leadHand, animate: false);
                    if (!_hands.IsHolding(user, pair.Tail))
                        _hands.TryPickup(user, pair.Tail, tailHand, animate: false);
                    SetActive(user, pair.Lead, leadActive);
                    SetActive(user, pair.Tail, tailActive);
                }
            }

            staff.Changing = false;
        }
    }

    private bool TryGetSplitHand(EntityUid user, EntityUid staff, bool wielded,
        [NotNullWhen(true)] out string? hand, out EntityUid? placeholder)
    {
        placeholder = null;
        if (_hands.TryGetEmptyHand(user, out hand))
            return true;

        if (!wielded || FindGripPlaceholder(user, staff) is not { } grip || !_hands.IsHolding(user, grip, out hand))
            return false;

        placeholder = grip;
        return true;
    }

    private EntityUid? FindGripPlaceholder(EntityUid user, EntityUid staff)
    {
        foreach (var held in _hands.EnumerateHeld(user))
        {
            if (TryComp<VirtualItemComponent>(held, out var item) && item.BlockingEntity == staff)
                return held;
        }

        return null;
    }

    private bool ReleaseGrip(EntityUid user, EntityUid staff, WieldableComponent wieldable, EntityUid? placeholder,
        bool force = false)
    {
        if (wieldable.Wielded && !_wieldable.TryUnwield(staff, wieldable, user, force))
            return false;

        // Native unwield queues its virtual item for deletion. This transaction needs that same hand now.
        if (placeholder is { } grip && _hands.IsHolding(user, grip) &&
            TryComp<VirtualItemComponent>(grip, out var item) && item.BlockingEntity == staff)
            Del(grip);

        return true;
    }

    private bool TryWieldStaff(EntityUid user, EntityUid staff, WieldableComponent wieldable)
    {
        if (!_hands.TryGetEmptyHand(user, out _))
            return false;
        var wielded = _wieldable.TryWield(staff, wieldable, user, dropOthers: false, quiet: true,
            respectUseDelay: false, requireHeldGrip: true);
        var grip = FindGripPlaceholder(user, staff);
        if (wielded && wieldable.Wielded && grip != null)
            return true;

        // Native wield sets Wielded before creating the grip, and its callbacks can remove it.
        ReleaseGrip(user, staff, wieldable, grip, force: true);
        SetActive(user, staff, false);
        return false;
    }

    private bool SetActive(EntityUid user, EntityUid item, bool active) =>
        !TerminatingOrDeleted(item) && _toggle.TrySetActive(item, active, user, predicted: false, showPopup: false);

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
            !_modular.TryGetAssemblyColor(pair.Tail, out var tailColor) ||
            !_modular.TryApplyAssembly(pair.Lead, staff.Owner, staff.Comp.JoinedDamageBonus))
        {
            args.Cancelled = true;
            args.Popup = Loc.GetString("sw-split-saber-staff-incomplete");
            return;
        }

        _appearance.SetData(staff.Owner, SWSplitSaberStaffVisuals.TailColor, tailColor);
    }

    private void OnToggled(Entity<SWSplitSaberStaffComponent> staff, ref ItemToggledEvent args) =>
        _modular.UpdateAttackRate(staff.Owner, args.Activated ? staff.Comp.Lead : null);
}
