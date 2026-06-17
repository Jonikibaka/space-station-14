using Content.Server.Actions;
using Content.Server.Chat.Systems;
using Content.Server.Explosion.EntitySystems;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Content.Server.Silicons.Laws;
using Content.Server.Station.Systems;
using Content.Shared.Silicons.Borgs.Components;
using Content.Shared.Alert;
using Content.Shared.Doors.Components;
using Content.Shared.Doors.Systems;
using Content.Shared.Electrocution;
using Content.Shared.FixedPoint;
using Content.Shared.Popups;
using Content.Shared.Silicons.Malfunction;
using Content.Shared.Verbs;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map.Components;
using Robust.Shared.Timing;

namespace Content.Server.Silicons.Malfunction;

/// <summary>
/// Server-side logic for the Malfunction AI antagonist: grants malf actions and handles all
/// malf ability events (APC hack for processing power, machine overload, station blackout,
/// station lockdown, and Doomsday device arming).
/// </summary>
public sealed partial class MalfunctionAiSystem : EntitySystem
{
    [Dependency] private ActionsSystem _actions = default!;
    [Dependency] private ApcSystem _apc = default!;
    [Dependency] private AlertsSystem _alerts = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private ExplosionSystem _explosion = default!;
    [Dependency] private SiliconLawSystem _law = default!;
    [Dependency] private SharedDoorSystem _doors = default!;
    [Dependency] private SharedElectrocutionSystem _electrify = default!;
    [Dependency] private SharedPopupSystem _popups = default!;
    [Dependency] private StationSystem _station = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<MalfunctionAiComponent, ComponentInit>(OnMalfInit);
        SubscribeLocalEvent<MalfunctionAiComponent, ComponentShutdown>(OnMalfShutdown);

        SubscribeLocalEvent<MalfunctionAiComponent, MalfHackApcEvent>(OnHackApc);
        SubscribeLocalEvent<MalfunctionAiComponent, MalfOverloadMachineEvent>(OnOverloadMachine);
        SubscribeLocalEvent<MalfunctionAiComponent, MalfHackCyborgEvent>(OnHackCyborg);
        SubscribeLocalEvent<MalfunctionAiComponent, MalfBlackoutEvent>(OnBlackout);
        SubscribeLocalEvent<MalfunctionAiComponent, MalfLockdownEvent>(OnLockdown);
        SubscribeLocalEvent<MalfunctionAiComponent, MalfDoomsdayEvent>(OnDoomsday);

        // Alt-click fallbacks (in addition to the action-bar buttons): the AI can also hack/overload
        // by alt-clicking the target directly.
        SubscribeLocalEvent<ApcComponent, GetVerbsEvent<AlternativeVerb>>(OnApcAltVerb);
        SubscribeLocalEvent<ApcPowerReceiverComponent, GetVerbsEvent<AlternativeVerb>>(OnMachineAltVerb);
        SubscribeLocalEvent<BorgChassisComponent, GetVerbsEvent<AlternativeVerb>>(OnCyborgAltVerb);
    }

    private void OnCyborgAltVerb(Entity<BorgChassisComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        var user = args.User;
        if (!TryComp<MalfunctionAiComponent>(user, out var malf))
            return;

        if (HasComp<MalfHackedCyborgComponent>(ent.Owner))
            return;

        var target = ent.Owner;
        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString("malfunction-ai-verb-hack-cyborg"),
            Act = () => TryHackCyborg((user, malf), target),
        });
    }

    private void OnApcAltVerb(Entity<ApcComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        var user = args.User;
        if (!TryComp<MalfunctionAiComponent>(user, out var malf))
            return;

        if (HasComp<MalfHackedApcComponent>(ent.Owner))
            return;

        var target = ent.Owner;
        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString("malfunction-ai-verb-hack-apc"),
            Priority = 10,
            Act = () => TryHackApc((user, malf), target),
        });
    }

    private void OnMachineAltVerb(Entity<ApcPowerReceiverComponent> ent, ref GetVerbsEvent<AlternativeVerb> args)
    {
        var user = args.User;
        if (!TryComp<MalfunctionAiComponent>(user, out var malf))
            return;

        // Don't overload yourself or APCs (those get hacked instead).
        if (ent.Owner == user || HasComp<ApcComponent>(ent.Owner) || HasComp<MalfPendingOverloadComponent>(ent.Owner))
            return;

        var target = ent.Owner;
        args.Verbs.Add(new AlternativeVerb
        {
            Text = Loc.GetString("malfunction-ai-verb-overload-machine"),
            Act = () => TryOverloadMachine((user, malf), target),
        });
    }

    private void OnMalfInit(Entity<MalfunctionAiComponent> ent, ref ComponentInit args)
    {
        ent.Comp.ActionEntities.Clear();
        foreach (var proto in ent.Comp.Actions)
        {
            if (_actions.AddAction(ent.Owner, proto) is { } actionUid)
                ent.Comp.ActionEntities.Add(actionUid);
        }

        EnsureComp<AlertsComponent>(ent.Owner);
        _alerts.ShowAlert(ent.Owner, ent.Comp.PowerAlert);
    }

    private void OnMalfShutdown(Entity<MalfunctionAiComponent> ent, ref ComponentShutdown args)
    {
        foreach (var actionUid in ent.Comp.ActionEntities)
        {
            _actions.RemoveAction(ent.Owner, actionUid);
        }
        ent.Comp.ActionEntities.Clear();
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var now = _timing.CurTime;

        var query = EntityQueryEnumerator<MalfunctionAiComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            // Passive regen, but only up to the passive cap; further power comes from hacking APCs.
            if (comp.PowerPerSecond > 0 && comp.ProcessingPower < comp.PassivePowerCap)
            {
                comp.Accumulator += frameTime;
                if (comp.Accumulator >= 1f)
                {
                    var ticks = (int) comp.Accumulator;
                    comp.Accumulator -= ticks;
                    comp.ProcessingPower = FixedPoint2.Min(
                        comp.PassivePowerCap,
                        comp.ProcessingPower + comp.PowerPerSecond * ticks);
                    Dirty(uid, comp);
                }
            }

            // End an active lockdown.
            if (comp.LockdownEndTime != null && now >= comp.LockdownEndTime)
            {
                EndLockdown((uid, comp));
            }
        }

        // Trigger pending machine overloads.
        var overloadQuery = EntityQueryEnumerator<MalfPendingOverloadComponent>();
        while (overloadQuery.MoveNext(out var machine, out var pending))
        {
            if (now < pending.TriggerAt)
                continue;

            _explosion.QueueExplosion(
                machine,
                "Default",
                pending.Intensity,
                5f,
                pending.MaxTileIntensity,
                canCreateVacuum: false,
                user: pending.Source);

            RemComp<MalfPendingOverloadComponent>(machine);
        }
    }

    private bool TrySpendPower(Entity<MalfunctionAiComponent> ent, FixedPoint2 cost)
    {
        if (ent.Comp.ProcessingPower < cost)
        {
            _popups.PopupCursor(Loc.GetString("malfunction-ai-popup-not-enough-power"), ent.Owner);
            return false;
        }

        ent.Comp.ProcessingPower -= cost;
        Dirty(ent);
        return true;
    }

    private void OnHackApc(Entity<MalfunctionAiComponent> ent, ref MalfHackApcEvent args)
    {
        if (args.Handled)
            return;

        if (TryHackApc(ent, args.Target))
            args.Handled = true;
    }

    private bool TryHackApc(Entity<MalfunctionAiComponent> ent, EntityUid target)
    {
        if (!HasComp<ApcComponent>(target))
        {
            _popups.PopupCursor(Loc.GetString("malfunction-ai-popup-invalid-target"), ent.Owner);
            return false;
        }

        if (HasComp<MalfHackedApcComponent>(target))
        {
            _popups.PopupCursor(Loc.GetString("malfunction-ai-popup-apc-already-hacked"), ent.Owner);
            return false;
        }

        // Hacking grants processing power rather than costing it.
        AddComp<MalfHackedApcComponent>(target);
        ent.Comp.HackedApcCount++;
        ent.Comp.ProcessingPower = FixedPoint2.Min(ent.Comp.MaxProcessingPower, ent.Comp.ProcessingPower + ent.Comp.CpuPerApc);
        Dirty(ent);

        _popups.PopupCursor(
            Loc.GetString("malfunction-ai-popup-hack-apc-success",
                ("power", ent.Comp.ProcessingPower.Int()),
                ("count", ent.Comp.HackedApcCount)),
            ent.Owner);
        return true;
    }

    private void OnOverloadMachine(Entity<MalfunctionAiComponent> ent, ref MalfOverloadMachineEvent args)
    {
        if (args.Handled)
            return;

        foreach (var candidate in _lookup.GetEntitiesInRange(args.Target, 0.75f))
        {
            if (candidate == ent.Owner
                || !HasComp<ApcPowerReceiverComponent>(candidate)
                || HasComp<ApcComponent>(candidate)
                || HasComp<MalfPendingOverloadComponent>(candidate))
                continue;

            if (TryOverloadMachine(ent, candidate))
            {
                args.Handled = true;
                return;
            }
        }

        _popups.PopupCursor(Loc.GetString("malfunction-ai-popup-invalid-target"), ent.Owner);
    }

    private bool TryOverloadMachine(Entity<MalfunctionAiComponent> ent, EntityUid target)
    {
        if (target == ent.Owner || HasComp<MalfPendingOverloadComponent>(target))
            return false;

        if (!TrySpendPower(ent, ent.Comp.OverloadMachineCost))
            return false;

        var pending = AddComp<MalfPendingOverloadComponent>(target);
        pending.TriggerAt = _timing.CurTime + ent.Comp.OverloadDelay;
        pending.Intensity = ent.Comp.OverloadIntensity;
        pending.MaxTileIntensity = ent.Comp.OverloadMaxTileIntensity;
        pending.Source = ent.Owner;

        _audio.PlayPvs(ent.Comp.OverloadWarningSound, target);

        _popups.PopupCursor(Loc.GetString("malfunction-ai-popup-overload-success"), ent.Owner);
        return true;
    }

    private void OnHackCyborg(Entity<MalfunctionAiComponent> ent, ref MalfHackCyborgEvent args)
    {
        if (args.Handled)
            return;

        foreach (var candidate in _lookup.GetEntitiesInRange(args.Target, 0.75f))
        {
            if (!HasComp<BorgChassisComponent>(candidate))
                continue;

            if (TryHackCyborg(ent, candidate))
            {
                args.Handled = true;
                return;
            }
        }

        _popups.PopupCursor(Loc.GetString("malfunction-ai-popup-invalid-cyborg"), ent.Owner);
    }

    private bool TryHackCyborg(Entity<MalfunctionAiComponent> ent, EntityUid target)
    {
        if (!HasComp<BorgChassisComponent>(target))
        {
            _popups.PopupCursor(Loc.GetString("malfunction-ai-popup-invalid-cyborg"), ent.Owner);
            return false;
        }

        if (HasComp<MalfHackedCyborgComponent>(target))
        {
            _popups.PopupCursor(Loc.GetString("malfunction-ai-popup-cyborg-already-hacked"), ent.Owner);
            return false;
        }

        if (!TrySpendPower(ent, ent.Comp.HackCyborgCost))
            return false;

        // Keep the borg's normal laws but prepend the hidden malfunction law 0, flagging it as an antag.
        if (!_law.AddMalfunctionLaw(target, ensureSubvertedRole: true))
        {
            // Already subverted (e.g. emagged); refund and bail.
            ent.Comp.ProcessingPower += ent.Comp.HackCyborgCost;
            Dirty(ent);
            _popups.PopupCursor(Loc.GetString("malfunction-ai-popup-cyborg-already-hacked"), ent.Owner);
            return false;
        }

        AddComp<MalfHackedCyborgComponent>(target);
        _popups.PopupCursor(Loc.GetString("malfunction-ai-popup-hack-cyborg-success"), ent.Owner);
        return true;
    }

    private void OnBlackout(Entity<MalfunctionAiComponent> ent, ref MalfBlackoutEvent args)
    {
        if (args.Handled)
            return;

        if (!TrySpendPower(ent, ent.Comp.BlackoutCost))
            return;

        var gridUid = Transform(ent.Owner).GridUid;
        var count = 0;

        var query = EntityQueryEnumerator<ApcComponent, TransformComponent>();
        while (query.MoveNext(out var apcUid, out var apc, out var xform))
        {
            if (gridUid != null && xform.GridUid != gridUid)
                continue;

            if (!apc.MainBreakerEnabled)
                continue;

            _apc.ApcToggleBreaker(apcUid, apc, user: ent.Owner);
            count++;
        }

        AnnounceFromAi(ent.Owner, Loc.GetString("malfunction-ai-announcement-blackout"));

        _popups.PopupCursor(Loc.GetString("malfunction-ai-popup-blackout-success", ("count", count)), ent.Owner);
        args.Handled = true;
    }

    private void OnLockdown(Entity<MalfunctionAiComponent> ent, ref MalfLockdownEvent args)
    {
        if (args.Handled)
            return;

        if (ent.Comp.LockdownEndTime != null)
        {
            _popups.PopupCursor(Loc.GetString("malfunction-ai-popup-lockdown-active"), ent.Owner);
            return;
        }

        if (!TrySpendPower(ent, ent.Comp.LockdownCost))
            return;

        var gridUid = Transform(ent.Owner).GridUid;
        ent.Comp.LockedDoors.Clear();

        var query = EntityQueryEnumerator<DoorBoltComponent, TransformComponent>();
        while (query.MoveNext(out var doorUid, out var bolt, out var xform))
        {
            if (gridUid != null && xform.GridUid != gridUid)
                continue;

            if (!_doors.TrySetBoltDown((doorUid, bolt), true, ent.Owner, predicted: false))
                continue;

            if (TryComp<ElectrifiedComponent>(doorUid, out var electrified))
                _electrify.SetElectrified((doorUid, electrified), true);

            ent.Comp.LockedDoors.Add(doorUid);
        }

        ent.Comp.LockdownEndTime = _timing.CurTime + TimeSpan.FromSeconds(ent.Comp.LockdownDuration);
        Dirty(ent);

        AnnounceFromAi(ent.Owner, Loc.GetString("malfunction-ai-announcement-lockdown"));

        _popups.PopupCursor(Loc.GetString("malfunction-ai-popup-lockdown-success", ("count", ent.Comp.LockedDoors.Count)), ent.Owner);
        args.Handled = true;
    }

    private void EndLockdown(Entity<MalfunctionAiComponent> ent)
    {
        foreach (var doorUid in ent.Comp.LockedDoors)
        {
            if (TryComp<DoorBoltComponent>(doorUid, out var bolt))
                _doors.TrySetBoltDown((doorUid, bolt), false, ent.Owner, predicted: false);

            if (TryComp<ElectrifiedComponent>(doorUid, out var electrified))
                _electrify.SetElectrified((doorUid, electrified), false);
        }

        ent.Comp.LockedDoors.Clear();
        ent.Comp.LockdownEndTime = null;
        Dirty(ent);
    }

    private void OnDoomsday(Entity<MalfunctionAiComponent> ent, ref MalfDoomsdayEvent args)
    {
        if (args.Handled)
            return;

        if (ent.Comp.DoomsdayUsed)
        {
            _popups.PopupCursor(Loc.GetString("malfunction-ai-popup-doomsday-already-used"), ent.Owner);
            return;
        }

        if (ent.Comp.HackedApcCount < ent.Comp.DoomsdayRequiredApcs)
        {
            _popups.PopupCursor(
                Loc.GetString("malfunction-ai-popup-doomsday-need-apcs",
                    ("required", ent.Comp.DoomsdayRequiredApcs),
                    ("current", ent.Comp.HackedApcCount)),
                ent.Owner);
            return;
        }

        if (!TrySpendPower(ent, ent.Comp.DoomsdayCost))
            return;

        ent.Comp.DoomsdayUsed = true;
        Dirty(ent);

        var doomEv = new MalfDoomsdayArmedEvent(ent.Owner);
        RaiseLocalEvent(ref doomEv);
        args.Handled = true;
    }

    private void AnnounceFromAi(EntityUid ai, string message)
    {
        var station = _station.GetOwningStation(ai);
        if (station == null)
            return;

        _chat.DispatchStationAnnouncement(
            station.Value,
            message,
            Loc.GetString("malfunction-ai-announcement-sender"),
            playDefaultSound: true,
            colorOverride: Color.Red);
    }
}

/// <summary>
/// Raised broadcast when a Malfunction AI arms the Doomsday device.
/// The Malfunction AI game rule listens for this and starts the countdown / blast.
/// </summary>
[ByRefEvent]
public readonly record struct MalfDoomsdayArmedEvent(EntityUid Ai);
