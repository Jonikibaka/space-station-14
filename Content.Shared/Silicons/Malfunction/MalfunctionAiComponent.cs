using Content.Shared.Alert;
using Content.Shared.FixedPoint;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared.Silicons.Malfunction;

/// <summary>
/// Added to a station AI once it has been turned into a Malfunction AI antagonist.
/// Tracks malf-only state such as processing power (currency for abilities), the number
/// of hacked APCs, and the Doomsday device state.
/// </summary>
[RegisterComponent, NetworkedComponent]
[AutoGenerateComponentState]
public sealed partial class MalfunctionAiComponent : Component
{
    /// <summary>
    /// Currency spent on malf abilities. Gained by hacking APCs.
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public FixedPoint2 ProcessingPower = 50;

    /// <summary>
    /// Cap for <see cref="ProcessingPower"/>.
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public FixedPoint2 MaxProcessingPower = 1000;

    /// <summary>
    /// Processing power regenerated per second. Canonically the AI gains power from APCs, not passively,
    /// so this defaults to zero.
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public FixedPoint2 PowerPerSecond = 0;

    /// <summary>
    /// Server-side accumulator for passive power regen.
    /// </summary>
    public float Accumulator;

    /// <summary>
    /// How many unique APCs this AI has hacked. Drives income and gates the Doomsday device.
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public int HackedApcCount;

    /// <summary>
    /// Processing power granted per APC hacked.
    /// </summary>
    [DataField]
    public FixedPoint2 CpuPerApc = 10;

    // --- Ability costs ---

    [DataField]
    public FixedPoint2 OverloadMachineCost = 20;

    [DataField]
    public FixedPoint2 BlackoutCost = 75;

    [DataField]
    public FixedPoint2 LockdownCost = 30;

    /// <summary>
    /// Cost to arm the Doomsday device.
    /// </summary>
    [DataField]
    public FixedPoint2 DoomsdayCost = 130;

    /// <summary>
    /// Number of hacked APCs required before the Doomsday device can be armed.
    /// </summary>
    [DataField]
    public int DoomsdayRequiredApcs = 8;

    // --- Tuning ---

    /// <summary>
    /// Intensity for the overload-machine explosion.
    /// </summary>
    [DataField]
    public float OverloadIntensity = 20f;

    /// <summary>
    /// Tile intensity cap for the overload-machine explosion.
    /// </summary>
    [DataField]
    public float OverloadMaxTileIntensity = 5f;

    /// <summary>
    /// Delay between triggering an overload and the explosion, giving a warning window.
    /// </summary>
    [DataField]
    public TimeSpan OverloadDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Warning sound played at the targeted machine when an overload starts.
    /// </summary>
    [DataField]
    public SoundSpecifier OverloadWarningSound = new SoundPathSpecifier("/Audio/Machines/vessel_warning.ogg");

    /// <summary>
    /// How long a station lockdown keeps doors bolted and electrified, in seconds.
    /// </summary>
    [DataField]
    public float LockdownDuration = 90f;

    /// <summary>
    /// When the current lockdown ends. Null if no lockdown is active.
    /// </summary>
    [DataField]
    public TimeSpan? LockdownEndTime;

    /// <summary>
    /// Doors affected by the current lockdown, to be reverted when it ends.
    /// </summary>
    [DataField]
    public List<EntityUid> LockedDoors = new();

    /// <summary>
    /// Whether the Doomsday device has already been used this round.
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite), AutoNetworkedField]
    public bool DoomsdayUsed;

    /// <summary>
    /// Actions granted on activation; tracked here so they can be removed later.
    /// </summary>
    [DataField]
    public List<EntityUid> ActionEntities = new();

    [DataField]
    public List<EntProtoId> Actions = new()
    {
        "ActionMalfHackApc",
        "ActionMalfOverloadMachine",
        "ActionMalfBlackout",
        "ActionMalfLockdown",
        "ActionMalfDoomsday",
    };

    /// <summary>
    /// HUD alert showing the current processing power.
    /// </summary>
    [DataField]
    public ProtoId<AlertPrototype> PowerAlert = "MalfunctionProcessingPower";
}
