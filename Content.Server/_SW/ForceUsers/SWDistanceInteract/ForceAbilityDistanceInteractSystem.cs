using Content.Shared._SW;
using Content.Shared.ActionBlocker;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Interaction;
using Content.Shared.Item;
using Robust.Shared.Map;
using Robust.Shared.Physics.Systems;
using System.Numerics;
using Robust.Shared.Containers;
using Content.Shared.Verbs;
using Robust.Shared.Utility;
using Content.Shared.Stunnable;
using Content.Server.Actions;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Server._SW.Unpickuppable;
using Content.Server._SW.ModularSaber;
using Content.Shared.Item.ItemToggle;
using Content.Shared.Wieldable;
using Content.Shared.Wieldable.Components;
using Content.Shared._SW.ForceAbilities;

namespace Content.Server._SW;
public sealed partial class ForceAbilityDistanceInteractSystem : EntitySystem
{
    [Dependency] private readonly SharedHandsSystem _handsSystem = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private readonly SharedInteractionSystem _interactionSystem = default!;
    [Dependency] private readonly SharedPhysicsSystem _physicsSystem = default!;
    [Dependency] private readonly SharedTransformSystem _transformSystem = default!;
    [Dependency] private readonly ActionsSystem _actionsSystem = default!;
    [Dependency] private readonly SharedWieldableSystem _wieldable = default!;
    [Dependency] private readonly ItemToggleSystem _toggle = default!;
    [Dependency] private readonly SWDoubleSaberThrowVisualSystem _saberThrows = default!;
    [Dependency] private readonly SWSplitSaberStaffSystem _splitStaff = default!;
    public override void Initialize()
    {
        SubscribeLocalEvent<ForceAbilityDistanceInteractComponent, ComponentInit>(OnCompInit);
        SubscribeLocalEvent<ForceAbilityDistanceInteractComponent, ComponentShutdown>(OnCompShutdown);
        SubscribeLocalEvent<BeingAttractedByForceComponent, EntGotInsertedIntoContainerMessage>(OnAttractedObjectGetInserted);
        SubscribeLocalEvent<BeingMovedByForceComponent, EntGotInsertedIntoContainerMessage>(OnMovedObjectGetInserted);
        SubscribeLocalEvent<ForceAbilityDistanceInteractComponent, AttractByForceRequestedEvent>(OnAttractRequested);
    }

    private void OnCompInit(Entity<ForceAbilityDistanceInteractComponent> ent, ref ComponentInit args)
    {
        _actionsSystem.AddAction(ent.Owner, ent.Comp.ActionAttractPrototype);
    }

    private void OnCompShutdown(Entity<ForceAbilityDistanceInteractComponent> ent, ref ComponentShutdown args)
    {
        if (RequiresOwnedAttraction(ent.Comp))
            StopAttractingByForce(ent.Owner, ent.Comp);
    }

    private void OnAttractRequested(Entity<ForceAbilityDistanceInteractComponent> ent, ref AttractByForceRequestedEvent args)
    {
        StartAttractingByForce(args.Performer, args.Target, ent.Comp);
        args.Handled = true;
    }

    private void OnMovedObjectGetInserted(Entity<BeingMovedByForceComponent> ent, ref EntGotInsertedIntoContainerMessage args)
    {
        StopMovingObjectByForce(ent.Owner);
    }
    private void StopMovingObjectByForce(EntityUid objectToMove)
    {
        if (!TryComp<BeingMovedByForceComponent>(objectToMove, out var comp) || comp.ForceUserMover == null)
        {
            return;
        }

        RaiseLocalEvent(objectToMove, new StopMovingByForceEvent(comp.ForceUserMover.Value));
    }
    private void OnAttractedObjectGetInserted(Entity<BeingAttractedByForceComponent> ent, ref EntGotInsertedIntoContainerMessage args)
    {
        if (ent.Comp.User == null)
        {
            return;
        }
        StopAttractingByForce(ent.Comp.User.Value);
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);
        var query = EntityQueryEnumerator<ForceAbilityDistanceInteractComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (HasComp<KnockedDownComponent>(uid))
            {
                StopAttractingByForce(uid, comp);
                comp.PairedSaberReturns?.Clear();
                continue;
            }

            var requiresOwnedAttraction = RequiresOwnedAttraction(comp);
            if ((requiresOwnedAttraction || comp.PairedSaberReturns is { Count: > 0 }) &&
                !_actionBlocker.CanInteract(uid, null))
            {
                if (requiresOwnedAttraction)
                    StopAttractingByForce(uid, comp);
                comp.PairedSaberReturns?.Clear();
            }

            if (!comp.IsAttracting)
                StartNextPairedSaberReturn(uid, comp);

            if (comp.IsAttracting && comp.CurrentAttractingItem != null)
            {
                if (TerminatingOrDeleted(comp.CurrentAttractingItem.Value))
                {
                    StopAttractingByForce(uid, comp);
                    continue;
                }

                MapCoordinates usercords = _transformSystem.GetMapCoordinates(uid);
                MapCoordinates targetcords = _transformSystem.GetMapCoordinates(comp.CurrentAttractingItem.Value);
                if (RequiresOwnedAttraction(comp) &&
                    (usercords.MapId != targetcords.MapId || !OwnsAttraction(uid, comp.CurrentAttractingItem.Value)))
                {
                    StopAttractingByForce(uid, comp);
                    continue;
                }

                if (_interactionSystem.InRangeUnobstructed(usercords, comp.CurrentAttractingItem.Value, range: 100f) &&
                    (usercords.Position - targetcords.Position).Length() >= SharedInteractionSystem.InteractionRange)
                {
                    _physicsSystem.SetLinearVelocity(comp.CurrentAttractingItem.Value, (usercords.Position - targetcords.Position).Normalized() * comp.AttractByForceSpeed);
                }
                else
                {
                    var item = comp.CurrentAttractingItem.Value;
                    var returnPairedSaber = comp.ReturningPairedSaber;
                    var returnSaber = TryComp<SWDoubleSaberThrowVisualComponent>(item, out var visual) &&
                                      visual.ReturnUser == uid;
                    var wasActive = returnSaber && _toggle.IsActivated(item);
                    var inRange = (usercords.Position - targetcords.Position).Length() < SharedInteractionSystem.InteractionRange;
                    if (comp.ReturningPoweredStaff)
                    {
                        if (inRange && visual != null && _saberThrows.TryBeginPoweredStaffCatch((item, visual), uid))
                            CatchPoweredStaff(uid, (item, visual), comp);
                        else
                            StopAttractingByForce(uid, comp);
                        continue;
                    }

                    StopAttractingByForce(uid, comp);
                    if (!inRange)
                        continue;

                    if (returnPairedSaber)
                    {
                        _handsSystem.TryPickupAnyHand(uid, item);
                    }
                    else if (returnSaber)
                    {
                        if (_handsSystem.TryPickupAnyHand(uid, item))
                            WieldReturnedSaber(uid, item, wasActive);
                    }
                    else
                    {
                        _handsSystem.TryPickup(uid, item);
                    }
                }
            }
        }
    }

    private void CatchPoweredStaff(EntityUid user, Entity<SWDoubleSaberThrowVisualComponent> staff,
        ForceAbilityDistanceInteractComponent attraction)
    {
        var regripped = false;
        try
        {
            // Stop the attraction to unlock pickup. Keep power while pickup and regrip are in progress.
            StopAttractingByForce(user, attraction);
            regripped = _interactionSystem.InRangeUnobstructed(user, staff.Owner) &&
                        _splitStaff.TryCatchPoweredStaff(user, staff.Owner);
        }
        finally
        {
            _saberThrows.FinishPoweredStaffCatch(staff, regripped);
        }
    }

    private void WieldReturnedSaber(EntityUid user, EntityUid saber, bool wasActive)
    {
        if (TerminatingOrDeleted(user) || TerminatingOrDeleted(saber) ||
            !TryComp<WieldableComponent>(saber, out var wieldable) || wieldable.Wielded ||
            !_handsSystem.IsHolding(user, saber) || !_handsSystem.TryGetEmptyHand(user, out _))
            return;

        // Never drop an unrelated held item. Roll back a partial wield if spawning a virtual hand fails.
        if (!_wieldable.TryWield(saber, wieldable, user, dropOthers: false, quiet: true))
        {
            if (wieldable.Wielded)
                _wieldable.TryUnwield(saber, wieldable, user, force: true);
            return;
        }

        // Wielding may turn the blade on automatically; preserve an intentionally inactive blade.
        if (!wasActive && _toggle.IsActivated(saber))
            _toggle.TryDeactivate(saber, user, predicted: false);
    }

    internal void EnqueuePairedSaberReturn(EntityUid user, EntityUid item, uint generation)
    {
        if (!TryComp<ForceAbilityDistanceInteractComponent>(user, out var comp) ||
            TerminatingOrDeleted(user) || HasComp<KnockedDownComponent>(user))
            return;

        if (comp.IsAttracting && comp.CurrentAttractingItem == item)
        {
            if (OwnsAttraction(user, item))
                comp.ReturningPairedSaber = true;
            return;
        }

        var request = (item, generation);
        var pending = comp.PairedSaberReturns ??= new Queue<(EntityUid Item, uint Generation)>();
        if (!pending.Contains(request))
            pending.Enqueue(request);
    }

    private void StartNextPairedSaberReturn(EntityUid user, ForceAbilityDistanceInteractComponent comp)
    {
        if (comp.PairedSaberReturns is not { } pending)
            return;

        var map = Transform(user).MapUid;
        while (pending.TryDequeue(out var request))
        {
            var (item, generation) = request;
            // Picking up or throwing a half supersedes any queued return for its previous flight.
            if (map == null || TerminatingOrDeleted(item) || Transform(item).MapUid != map ||
                _containers.IsEntityInContainer(item) ||
                !TryComp<SWSplitSaberHalfComponent>(item, out var half) || half.ReturnGeneration != generation ||
                half.Staff is not { } staff || TerminatingOrDeleted(staff) ||
                (TryComp<BeingAttractedByForceComponent>(item, out var attraction) &&
                 attraction.User is { } owner && owner != user))
                continue;

            StartAttractingByForce(user, item, comp);
            if (comp.IsAttracting)
            {
                comp.ReturningPairedSaber = true;
                return;
            }
        }
    }

    private bool RequiresOwnedAttraction(ForceAbilityDistanceInteractComponent comp) =>
        comp.ReturningPairedSaber || comp.ReturningPoweredStaff ||
        HasComp<SWSplitSaberStaffComponent>(comp.CurrentAttractingItem);

    private bool OwnsAttraction(EntityUid user, EntityUid item) =>
        TryComp<BeingAttractedByForceComponent>(item, out var attraction) && attraction.User == user;

    internal void StartReturningStaffByForce(EntityUid user, EntityUid staff)
    {
        if (TryComp<BeingAttractedByForceComponent>(staff, out var attraction) &&
            attraction.User is { } owner && owner != user)
            return;

        StartAttractingByForce(user, staff);
    }

    public void StartAttractingByForce(EntityUid forceUser, EntityUid target, ForceAbilityDistanceInteractComponent? comp = null)
    {
        if (!TryComp(forceUser, out comp))
        {
            return;
        }

        if (HasComp<KnockedDownComponent>(forceUser)) {
            return;
        }

        if (!HasComp<ItemComponent>(target))
        {
            return;
        }

        if (!comp.IsAttracting || comp.CurrentAttractingItem != target)
        {
            if (comp.CurrentAttractingItem != null)
                StopAttractingByForce(forceUser, comp);

            comp.IsAttracting = true;
            comp.CurrentAttractingItem = target;
            EnsureComp<BeingAttractedByForceComponent>(target).User = forceUser;
            EnsureComp<SWUnpickuppableComponent>(target);
        }

        if (TryComp<SWDoubleSaberThrowVisualComponent>(target, out var visual) &&
            _saberThrows.IsPoweredStaffReturn((target, visual), forceUser) && OwnsAttraction(forceUser, target))
            comp.ReturningPoweredStaff = true;
    }

    public void StopAttractingByForce(EntityUid user, ForceAbilityDistanceInteractComponent? comp = null)
    {
        if (comp == null)
        {
            comp = CompOrNull<ForceAbilityDistanceInteractComponent>(user);
            if (comp == null)
            {
                return;
            }
        }

        if (comp.CurrentAttractingItem == null)
        {
            return;
        }

        var item = comp.CurrentAttractingItem.Value;
        var wasReturningPoweredStaff = comp.ReturningPoweredStaff;
        var requiresOwnedAttraction = RequiresOwnedAttraction(comp);
        comp.IsAttracting = false;
        comp.CurrentAttractingItem = null;
        comp.ReturningPairedSaber = false;
        comp.ReturningPoweredStaff = false;
        if (TerminatingOrDeleted(item))
            return;

        if (wasReturningPoweredStaff)
            _saberThrows.CancelPoweredStaffReturn(item, user);
        if (requiresOwnedAttraction && !OwnsAttraction(user, item))
            return;

        _physicsSystem.SetLinearVelocity(item, Vector2.Zero);
        RemComp<BeingAttractedByForceComponent>(item);
        RemComp<SWUnpickuppableComponent>(item);
    }
}
