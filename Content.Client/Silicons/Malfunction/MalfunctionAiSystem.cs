using Content.Shared.Alert.Components;
using Content.Shared.Silicons.Malfunction;

namespace Content.Client.Silicons.Malfunction;

/// <summary>
/// Feeds the current processing power to the Malfunction AI's HUD counter alert.
/// </summary>
public sealed class MalfunctionAiSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MalfunctionAiComponent, GetGenericAlertCounterAmountEvent>(OnGetCounterAmount);
    }

    private void OnGetCounterAmount(Entity<MalfunctionAiComponent> ent, ref GetGenericAlertCounterAmountEvent args)
    {
        if (args.Handled)
            return;

        if (ent.Comp.PowerAlert != args.Alert)
            return;

        args.Amount = ent.Comp.ProcessingPower.Int();
    }
}
