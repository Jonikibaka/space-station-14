using Content.Shared.Actions;

namespace Content.Shared.Silicons.Malfunction;

/// <summary>
/// Targets an APC and tries to hack it: turns off the breaker and drains its battery.
/// </summary>
public sealed partial class MalfHackApcEvent : EntityTargetActionEvent;

/// <summary>
/// Targets a location; the machine there is overloaded into a small explosion.
/// World-targeted so the remote station AI can use it (entity-target actions fail their access check).
/// </summary>
public sealed partial class MalfOverloadMachineEvent : WorldTargetActionEvent;

/// <summary>
/// Causes a station-wide blackout: shuts off APC breakers across the station for a short time.
/// </summary>
public sealed partial class MalfBlackoutEvent : InstantActionEvent;

/// <summary>
/// Bolts and electrifies every airlock on the AI's grid for a short time.
/// </summary>
public sealed partial class MalfLockdownEvent : InstantActionEvent;

/// <summary>
/// Targets a location; a cyborg there is subverted to the Malfunction AI's side.
/// World-targeted so the remote station AI can use it (entity-target actions fail their access check).
/// </summary>
public sealed partial class MalfHackCyborgEvent : WorldTargetActionEvent;

/// <summary>
/// Arms the Doomsday device. Handled by the Malfunction AI rule.
/// </summary>
public sealed partial class MalfDoomsdayEvent : InstantActionEvent;
