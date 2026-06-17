using Robust.Shared.GameStates;

namespace Content.Shared.Silicons.Malfunction;

/// <summary>
/// Marks a cyborg that has already been hacked/subverted by a Malfunction AI, so it cannot be
/// hacked again.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class MalfHackedCyborgComponent : Component;
