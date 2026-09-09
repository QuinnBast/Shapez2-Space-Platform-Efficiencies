using Game.Core.Coordinates;
using Game.Core.Map.Simulation;
using Game.Core.Simulation;

/// <summary>How a boundary port relates to the platform it is on.</summary>
public enum PortKind
{
    /// Not a boundary port at all.
    None,

    /// Carries goods off the platform.
    Output,

    /// Brings goods onto the platform.
    Input,

    /// A port building with no counterpart opposite it. The game still simulates what it
    /// would carry, so it exists and reports a rate - of nothing, forever.
    Unconnected
}

/// <summary>
/// Names every shape the game can leave a port in, and says which way it faces.
///
/// There is no shared interface over the port simulations, so this is a list rather than a
/// test - and it has to be the whole list, because a missing entry is a platform whose
/// output silently does not count. Two of them need more than their type: a transfer
/// simulation spans two docked platforms, and which end a platform owns is what decides
/// whether goods are leaving or arriving.
///
/// Taken from the port taxonomy in the modding notes, which is also where the two traps
/// below are written down.
/// </summary>
public static class PortClassifier
{
    /// <summary>
    /// What this simulation is to the island that owns <paramref name="chunk"/>.
    ///
    /// The chunk matters only for the transfer simulations. ConnectablePort orders their
    /// occupied chunks sending side first, so owning chunk 0 means goods leave here and
    /// owning chunk 1 means they arrive - and a port that has both ends on one platform is
    /// an internal connection that is nobody's boundary.
    /// </summary>
    public static PortKind Classify(ILocalizedSimulation localized, GlobalChunkCoordinate chunk)
    {
        if (localized == null)
        {
            return PortKind.None;
        }

        switch (localized.Simulation)
        {
            // Across space, by construction - there is no counterpart to check for.
            case SpaceBeltPortSenderSimulation _:
            case SpaceFluidPortSenderSimulation _:
                return PortKind.Output;

            case SpaceBeltPortReceiverSimulation _:
            case SpaceFluidPortReceiverSimulation _:
                return PortKind.Input;

            // A port building whose opposite number was never built. It is still simulated,
            // so it still turns up here, and counting it as capacity would hold a platform
            // to a port that cannot ever carry anything.
            case BeltPortSenderBlockedSimulation _:
            case BeltPortReceiverDisabledSimulation _:
            case FluidPortBlockedSimulation _:
            case FluidPortReceiverDisabledSimulation _:
                return PortKind.Unconnected;

            // Two platforms docked together. One simulation, one flow, two owners.
            case BeltPortTransferSimulation _:
            case FluidPortTransferSimulation _:
                return Direction(localized, chunk);

            default:
                return PortKind.None;
        }
    }

    private static PortKind Direction(ILocalizedSimulation localized, GlobalChunkCoordinate chunk)
    {
        if (localized.NumOccupiedChunks < 2)
        {
            // Both ends on one platform: an internal hop, not a boundary.
            return PortKind.None;
        }

        if (localized.GetOccupiedChunk(0).Equals(chunk))
        {
            return PortKind.Output;
        }

        return localized.GetOccupiedChunk(1).Equals(chunk) ? PortKind.Input : PortKind.None;
    }
}
