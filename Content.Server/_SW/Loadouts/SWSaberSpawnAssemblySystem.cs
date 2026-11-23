using System.Linq;
using Content.Server._SW.ModularSaber;
using Content.Shared._SW.CantEquipOnSlot;
using Content.Shared._SW.Loadouts;
using Content.Shared._SW.ModularSaber;
using Content.Shared.Inventory;
using Content.Shared.Hands.EntitySystems;
using Content.Shared.Station;
using Content.Shared.Roles;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Whitelist;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Server._SW.Loadouts;

/// <summary>Assembles the saber kit in a single starting-gear operation, including NPC kits.</summary>
public sealed class SWSaberSpawnAssemblySystem : EntitySystem
{
    [Dependency] private readonly EntityWhitelistSystem _whitelist = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly InventorySystem _inventory = default!;
    [Dependency] private readonly SharedHandsSystem _hands = default!;
    [Dependency] private readonly SharedContainerSystem _containers = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly SWSplitSaberStaffSystem _staff = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<InventoryComponent, PrepareStartingGearEvent>(OnPrepare);
        SubscribeLocalEvent<InventoryComponent, CompleteStartingSaberEvent>(OnComplete);
    }

    private void OnPrepare(Entity<InventoryComponent> entity, ref PrepareStartingGearEvent args)
    {
        // Only the designated first spawn of a force user may assemble a starting kit.
        if (!HasComp<SWSaberInitialAssemblyComponent>(entity))
            return;
        RemComp<SWSaberInitialAssemblyComponent>(entity);
        // Player gear records exactly which contributions came from kit groups. NPC kits are
        // dedicated starting-gear prototypes and therefore use the whole gear as their source.
        var kit = args.Gear is SWMergedGear merged ? merged.SaberKit : args.Gear;
        if (kit == null)
            return;
        string? hiltSlot = null;
        EntProtoId hilt = default;
        foreach (var (slot, prototype) in kit.Equipment)
        {
            if ((!HasPrototypeComponent<SWModularSaberComponent>(prototype) &&
                 !HasPrototypeComponent<SWSplitSaberStaffComponent>(prototype)) ||
                !args.Gear.Equipment.TryGetValue(slot, out var equipped) || equipped != prototype)
                continue;
            hiltSlot = slot;
            hilt = prototype;
            break;
        }
        if (hiltSlot == null)
            return;

        // Do not create anything in nullspace or on a map that is being deleted.
        if (Transform(entity).MapUid is not { } map || TerminatingOrDeleted(map))
            return;

        var gear = new SWMergedGear
        {
            Equipment = new(args.Gear.Equipment),
            Inhand = new(args.Gear.Inhand),
            Storage = new(),
        };
        foreach (var (slot, items) in args.Gear.Storage)
            gear.Storage[slot] = new(items);

        gear.Equipment.Remove(hiltSlot);
        var emitter = Take<SWSaberEmitterComponent>(kit, gear);
        var crystal = Take<SWSaberCrystalComponent>(kit, gear);
        var focus = Take<SWSaberFocusComponent>(kit, gear);
        // Never synthesize free parts when the configured kit is incomplete.
        if (emitter is not { } emitterId || crystal is not { } crystalId || focus is not { } focusId)
            return;

        // Build temporary entities on the map. On any failure the original snapshot remains intact.
        var coordinates = _transform.GetMapCoordinates(entity);
        var created = new List<EntityUid>();
        var assembled = false;
        try
        {
            var saber = SpawnForAssembly(hilt);
            if (TryComp<SWSplitSaberStaffComponent>(saber, out var staff))
            {
                var lead = SpawnForAssembly(staff.HalfPrototype);
                var tail = SpawnForAssembly(staff.HalfPrototype);
                // The selected kit determines both sets; the one-shot assembly creates each physical part once.
                if (!TryFill(lead) || !TryFill(tail) || !_staff.TryInitialize((saber, staff), lead, tail))
                    return;
            }
            else if (!TryFill(saber))
            {
                return;
            }

            // The snapshot is committed only after the entire assembly succeeds.
            args.Gear = gear;
            args.Saber = saber;
            assembled = true;
        }
        finally
        {
            if (!assembled)
            {
                // Delete inserted parts and halves before their containers.
                for (var i = created.Count - 1; i >= 0; i--)
                    Del(created[i]);
            }
        }

        EntityUid SpawnForAssembly(EntProtoId prototype)
        {
            var spawned = Spawn(prototype, coordinates);
            created.Add(spawned);
            return spawned;
        }

        bool TryFill(EntityUid saber)
        {
            if (!TryComp<SWModularSaberComponent>(saber, out var modular) ||
                modular.Crystal?.ContainerSlot == null || modular.Emitter?.ContainerSlot == null ||
                modular.Focus?.ContainerSlot == null)
                return false;

            return TryInsertPart(crystalId, modular.Crystal) &&
                   TryInsertPart(emitterId, modular.Emitter) &&
                   TryInsertPart(focusId, modular.Focus);
        }

        bool TryInsertPart(EntProtoId prototype, ItemSlot slot)
        {
            var part = SpawnForAssembly(prototype);
            return FitsSlot(part, slot) && _containers.Insert(part, slot.ContainerSlot!);
        }
    }

    private void OnComplete(Entity<InventoryComponent> entity, ref CompleteStartingSaberEvent args)
    {
        var saber = args.Saber;
        if (_inventory.TryGetSlots(entity, out var slots))
        {
            foreach (var slot in slots
                         .Where(slot => (slot.SlotFlags & SlotFlags.POCKET) != 0)
                         .OrderBy(slot => slot.Name switch { "pocket1" => 0, "pocket2" => 1, _ => 2 })
                         .ThenBy(slot => slot.Name, StringComparer.Ordinal))
            {
                if (TryComp<SWDisableSlotComponent>(entity, out var disabled) &&
                    (disabled.ProhibitedSlots & slot.SlotFlags) != 0)
                    continue;
                if (_inventory.TryEquip(entity, saber, slot.Name, silent: true))
                    return;
            }
        }

        if (_hands.TryPickupAnyHand(entity, saber, animate: false))
            return;

        Log.Warning("Could not equip initial lightsaber; it remains at its current location.");
    }

    private bool FitsSlot(EntityUid part, ItemSlot slot) =>
        !_whitelist.IsWhitelistFail(slot.Whitelist, part) && !_whitelist.IsWhitelistPass(slot.Blacklist, part);

    private bool HasPrototypeComponent<T>(EntProtoId id) where T : Component, new()
    {
        return _prototypes.TryIndex(id, out var prototype) &&
               prototype.TryGetComponent<T>(out _, EntityManager.ComponentFactory);
    }

    private EntProtoId? Take<T>(IEquipmentLoadout kit, SWMergedGear gear) where T : Component, new()
    {
        foreach (var (slot, items) in kit.Storage)
        {
            if (!gear.Storage.TryGetValue(slot, out var available))
                continue;
            foreach (var item in items)
            {
                if (HasPrototypeComponent<T>(item) && available.Remove(item))
                    return item;
            }
        }
        return null;
    }
}

/// <summary>One-shot authorization to assemble the initial force-user kit.</summary>
[RegisterComponent]
public sealed partial class SWSaberInitialAssemblyComponent : Component { }
