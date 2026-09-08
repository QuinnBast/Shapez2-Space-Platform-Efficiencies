using Game.Core.Simulation;

/// <summary>
/// A stand-in lane that reports the items accepted by a whole group of real lanes.
///
/// The game's efficiency gauge works out its numbers purely from the gaps between items
/// arriving at the one lane it is given, and it only ever installs a hook on that lane -
/// it never moves items through it. So handing it this instead of a real lane, and firing
/// it whenever any lane in a bundle accepts something, makes it measure the bundle as a
/// whole. That is what a space belt or space pipe needs: twelve parallel lanes are one
/// flow as far as the player is concerned.
/// </summary>
public class AggregateLane : IItemLane, IHookableItemReceiver
{
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

    /// <summary>Tells whoever is listening that one of the real lanes took an item.</summary>
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
