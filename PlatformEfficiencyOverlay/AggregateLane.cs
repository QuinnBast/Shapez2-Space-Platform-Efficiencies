using Game.Core.Map.Simulation;
using Game.Core.Simulation;

/// <summary>
/// A stand-in lane that drives the game's efficiency gauge from a rate we measured.
///
/// The gauge works out its numbers purely from the gaps between items arriving at the one
/// lane it is given, and it only ever installs a hook on that lane - it never moves items
/// through it. So handing it this instead of a real lane lets it measure something it
/// otherwise cannot: a space belt is twelve parallel lanes and one flow as far as anyone
/// looking at it is concerned.
///
/// Forwarding the real arrivals does not work, though, and that is the whole reason this
/// drives the gauge instead of relaying to it. Several lanes hand over in the same
/// simulation tick, all of those arrivals carry the same timestamp, and the gaps the gauge
/// averages collapse towards zero - so a busy belt reads pinned or absurd. What goes in
/// instead is a perfectly regular stream whose spacing *is* the measured rate: one arrival
/// every 60/rate seconds. The gauge divides that out and arrives back at the rate we
/// measured, and at our percentage of capacity, because that is what its arithmetic does
/// to an even stream. Feeding it the answer is more honest than feeding it noise.
/// </summary>
public class AggregateLane : IItemLane, IHookableItemReceiver
{
    /// A cap on one catch-up, so a rate that jumps cannot cost a frame.
    private const int MaxArrivalsPerUpdate = 256;

    /// When the next synthetic arrival is due, on the simulation clock.
    private Ticks NextArrival;
    private bool Driving;

    public PreAcceptHookDelegate PreAcceptHook { get; set; }

    public AcceptHookDelegate AcceptHook { get; set; }

    public PostAcceptHookDelegate PostAcceptHook { get; set; }

    public IItemReceiver NextLane { get; set; }

    // Nothing is ever routed through this lane; it exists to be listened to.
    public Steps MaxStep_S => Steps.Zero;

    public Steps FreeStepsAtTheEnd => Steps.Zero;

    public int ItemCount => 0;

    public bool HasItem => false;

    public IBeltItem GetItem(int index)
    {
        return null;
    }

    public void Clear()
    {
    }

    public bool CanAcceptItem(IBeltItem itemToTransfer)
    {
        return false;
    }

    public void HandOverItem(IBeltItem itemToTransfer, Ticks remainingTicks)
    {
        Report(itemToTransfer, remainingTicks);
    }

    /// <summary>
    /// Hands the gauge the arrivals a flow running at this rate would have produced since
    /// the last call.
    ///
    /// The timestamp the gauge records is its reference simulation's clock minus the
    /// remaining ticks it is passed, so passing the difference between that clock and the
    /// instant we want places an arrival exactly where we intend.
    /// </summary>
    public void Drive(ISimulator simulator, ILocalizedSimulation reference, float itemsPerMinute, Ticks now)
    {
        // Nobody is looking: the gauge installs its hook when its panel opens and takes it
        // away again when the panel closes.
        if (AcceptHook == null || simulator == null || reference == null || itemsPerMinute <= 0f)
        {
            Driving = false;
            return;
        }

        Ticks spacing = Ticks.FromSeconds(60f / itemsPerMinute);
        if (spacing.Value <= 0)
        {
            return;
        }

        if (!Driving)
        {
            Driving = true;
            NextArrival = now;
        }

        // Behind by more than a second's worth means the game was paused, or the panel was
        // closed for a while, or the rate has just changed sharply. Start again from here
        // rather than paying off a debt that would misreport the present.
        long slack = Ticks.OneSecond.Value;
        if (now.Value - NextArrival.Value > slack + spacing.Value * MaxArrivalsPerUpdate)
        {
            NextArrival = now;
        }

        Ticks basis = simulator.GetSimulationTimeFor(reference)
            + simulator.GetSimulationUpdateDeltaTimeFor(reference);

        for (int i = 0; i < MaxArrivalsPerUpdate && NextArrival.Value <= now.Value; i++)
        {
            Report(null, new Ticks(basis.Value - NextArrival.Value));
            NextArrival = new Ticks(NextArrival.Value + spacing.Value);
        }
    }

    /// <summary>Tells whoever is listening that an item arrived.</summary>
    public void Report(IBeltItem item, Ticks remainingTicks)
    {
        AcceptHookDelegate hook = AcceptHook;
        if (hook == null)
        {
            return;
        }

        // The hook takes these by reference and is allowed to rewrite them; nothing
        // downstream of us reads them back, so a local copy is enough.
        IBeltItem forwarded = item;
        Ticks remaining = remainingTicks;
        hook(this, ref forwarded, ref remaining);
    }
}
