/// <summary>
/// What a single machine is doing right now, as far as the overlay can tell.
/// </summary>
public enum EfficiencyStatus
{
    /// Not an item-moving simulation, or it does not expose usable lanes.
    Unknown,

    /// Nothing on any input lane - this machine is waiting for supply from upstream.
    Starved,

    /// An output lane is holding a finished item that the next lane refuses to take.
    Blocked,

    /// Items are flowing through.
    Running
}
