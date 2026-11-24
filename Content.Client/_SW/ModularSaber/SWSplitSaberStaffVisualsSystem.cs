using Content.Client.Toggleable;
using Content.Shared._SW.ModularSaber;
using Content.Shared.Hands;
using Content.Shared.Hands.Components;
using Content.Shared.Toggleable;
using Content.Shared.Wieldable.Components;
using Robust.Client.GameObjects;

namespace Content.Client._SW.ModularSaber;

[RegisterComponent]
public sealed partial class SWSplitSaberStaffVisualsComponent : Component;

public sealed class SWSplitSaberStaffVisualsSystem : VisualizerSystem<SWSplitSaberStaffVisualsComponent>
{
    private const string TailLayer = "staff-tail-blade";
    private const string InhandRsi = "_SW/Objects/Weapons/Melee/split_saber_staff_inhands.rsi";

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SWSplitSaberStaffVisualsComponent, GetInhandVisualsEvent>(OnGetHeldVisuals,
            after: [typeof(ToggleableVisualsSystem)]);
    }

    protected override void OnAppearanceChange(EntityUid uid, SWSplitSaberStaffVisualsComponent component,
        ref AppearanceChangeEvent args)
    {
        if (args.Sprite == null ||
            !SpriteSystem.LayerMapTryGet((uid, args.Sprite), TailLayer, out var layer, false))
            return;

        var visible = TryGetTailColor(uid, args.Component, out var color);
        SpriteSystem.LayerSetVisible((uid, args.Sprite), layer, visible);
        if (visible)
            SpriteSystem.LayerSetColor((uid, args.Sprite), layer, color);
    }

    private void OnGetHeldVisuals(EntityUid uid, SWSplitSaberStaffVisualsComponent component, GetInhandVisualsEvent args)
    {
        if (!TryComp<AppearanceComponent>(uid, out var appearance) ||
            !TryGetTailColor(uid, appearance, out var tailColor) ||
            !AppearanceSystem.TryGetData<Color>(uid, ToggleableVisuals.Color, out var leadColor, appearance))
            return;

        var hand = args.Location switch
        {
            HandLocation.Left => "left",
            HandLocation.Right => "right",
            _ => null,
        };
        if (hand == null)
            return;

        var wielded = AppearanceSystem.TryGetData<bool>(uid, WieldableVisuals.Wielded, out var grip, appearance) && grip;
        var prefix = wielded ? "inhand" : "onehand";
        AddBlade(args, $"{prefix}-{hand}-lead", leadColor);
        AddBlade(args, $"{prefix}-{hand}-tail", tailColor);
    }

    private static void AddBlade(GetInhandVisualsEvent args, string state, Color color)
    {
        args.Layers.Add((state, new PrototypeLayerData
        {
            RsiPath = InhandRsi,
            State = state,
            Shader = "unshaded",
            Color = color,
        }));
    }

    private bool TryGetTailColor(EntityUid uid, AppearanceComponent appearance, out Color color)
    {
        color = default;
        return AppearanceSystem.TryGetData<bool>(uid, ToggleableVisuals.Enabled, out var enabled, appearance) &&
               enabled && AppearanceSystem.TryGetData(uid, SWSplitSaberStaffVisuals.TailColor, out color, appearance);
    }
}
