using System;
using System.Collections.Generic;
using System.Diagnostics;
using Game.Content.Features.SpacePaths;
using Game.Core.Belts.BeltPath;
using Game.Core.Coordinates;
using Game.Core.Map.Simulation;
using Game.Core.Simulation;
using Unity.Mathematics;

/// <summary>
/// Watches every item-moving simulation on the map and measures what actually flows
/// through it, so the overlay can show real rates instead of guesses.
///
/// Measurement is exact: each tracked simulation gets a <see cref="ThroughputMeter"/>
/// chained onto its output lane's PostAcceptHook, which fires once per item handed over.
/// Everything else (rate, status, per-island totals) is recomputed in one pass a few
/// times a second, and only while the overlay is switched on.
/// </summary>
public class EfficiencyTracker : IDisposable
{
    public sealed class Entry
    {
        public ILocalizedSimulation Localized;
        public readonly ThroughputMeter Meter = new ThroughputMeter();

        /// Lanes we count items on - the outputs, or the inputs for a terminal simulation.
        public IItemLane[] MeteredLanes = Array.Empty<IItemLane>();

        /// Lanes feeding this simulation, used to tell "starved" from "jammed".
        public IItemLane[] InputLanes = Array.Empty<IItemLane>();

        public IslandId Island;
        public GlobalChunkCoordinate Chunk;

        /// Theoretical ceiling from the building definition, or 0 when we don't know one.
        public float MaxItemsPerMinute;

        /// Where <see cref="MaxItemsPerMinute"/> came from. Reported by the inspect command,
        /// because a percentage is only ever as trustworthy as its denominator.
        public string MaxSource = "none";

        public float ItemsPerMinute;
        public float PeakItemsPerMinute;
        public EfficiencyStatus Status;

        /// <summary>
        /// How full the lanes feeding this are, smoothed - 0 nothing waiting, 1 backed up.
        ///
        /// This is what separates the two ways of running at 16%: fed and still not
        /// keeping up (the constraint is here or downstream) from simply not being given
        /// enough to do (the constraint is upstream). Throughput alone cannot tell them
        /// apart, and it is the difference that says which way to walk the factory.
        /// </summary>
        public float Saturation;

        /// Set for space pipe ports, whose backlog is a fluid level rather than a lane.
        public SpaceFluidPortSenderSimulation FluidSource;

        public bool IsSaturated => Saturation >= OverlayTuning.SaturationThreshold;

        /// Frame stamp so a simulation spanning several chunks only draws its label once.
        public int LastLabelFrame = -1;

        /// Created on demand when the side panel wants a gauge over all of these lanes.
        private AggregateLane Aggregate;

        /// Set for a platform's output ports, so all of them feed one platform-wide gauge.
        public AggregateLane Shared;

        /// This carries goods off the platform - a space belt port or a space pipe port.
        public bool IsOutputPort;

        /// <summary>Reports every metered lane as one flow, for the side panel gauge.</summary>
        public AggregateLane EnsureAggregate()
        {
            return Aggregate ?? (Aggregate = new AggregateLane());
        }

        /// <summary>
        /// A space pipe port has no lane to hook: it packages fluid straight into a
        /// buffer. Each package launch is reported here instead, which is the same event
        /// one step earlier.
        /// </summary>
        public void OnFluidPackageLaunched()
        {
            OnItemAccepted(null, null);
        }

        /// <summary>Hooked onto every metered lane; runs once per item handed over.</summary>
        public void OnItemAccepted(IItemReceiver receiver, IBeltItem item)
        {
            Meter.CountItem(receiver, item);

            // Ticks.Zero means "arrived now". The gauge averages gaps over a minute, so
            // quantising to the update it landed in makes no practical difference.
            Aggregate?.Report(item, Ticks.Zero);
            Shared?.Report(item, Ticks.Zero);
        }

        /// <summary>True when the percentage is measured against a known ceiling rather
        /// than against the best rate this happens to have managed so far.</summary>
        public bool HasKnownCeiling => MaxItemsPerMinute > 0f;

        /// <summary>0 = dead stop, 1 = running at the best rate we know this can do.</summary>
        public float Utilization
        {
            get
            {
                float reference = MaxItemsPerMinute > 0f ? MaxItemsPerMinute : PeakItemsPerMinute;
                if (reference <= 0f)
                {
                    return 0f;
                }

                float value = ItemsPerMinute / reference;
                return value > 1f ? 1f : value;
            }
        }
    }

    public sealed class IslandSummary
    {
        /// Items per minute leaving the platform through its space belt ports.
        public float OutputItemsPerMinute;

        /// Set once a space belt port has reported in, so we know the total above means something.
        public bool HasPorts;

        /// The busiest single flow on the platform - what a space belt island is carrying,
        /// where there are no ports to total up.
        public float BusiestItemsPerMinute;

        /// Every output port reporting as one flow, and what they could ship between them.
        /// This is what the platform's side panel gauge measures.
        public readonly AggregateLane OutputAggregate = new AggregateLane();
        public float OutputCeiling;
        public ILocalizedSimulation OutputSimulation;

        /// Average utilization of everything built on the platform.
        public float Utilization;

        /// Average of how backed up its machines' supply is, for the space view pip.
        public float Saturation;

        public bool IsSaturated => Saturation >= OverlayTuning.SaturationThreshold;

        public int MachineCount;
        public int BlockedCount;
        public int StarvedCount;

        /// <summary>The one number worth putting on the platform: what it ships, or what
        /// flows through it when it has no ports of its own.</summary>
        public float HeadlineItemsPerMinute => HasPorts ? OutputItemsPerMinute : BusiestItemsPerMinute;
    }

    private const float SaturationSmoothing = 0.35f;

    private static readonly Ticks AggregateInterval = Ticks.FromSeconds(0.5f);
    private static readonly Ticks KeepWarmInterval = Ticks.FromSeconds(1f);

    private readonly Dictionary<ILocalizedSimulation, Entry> Entries =
        new Dictionary<ILocalizedSimulation, Entry>();

    private readonly Dictionary<GlobalChunkCoordinate, List<Entry>> EntriesByChunk =
        new Dictionary<GlobalChunkCoordinate, List<Entry>>();

    private readonly Dictionary<IslandId, IslandSummary> Summaries =
        new Dictionary<IslandId, IslandSummary>();

    /// The output ports each platform ships through, for the platform side panel.
    private readonly Dictionary<IslandId, List<Entry>> IslandPorts =
        new Dictionary<IslandId, List<Entry>>();

    /// Space pipe ports are found by their launch simulation, which is what the detour
    /// hands back when a fluid package goes out.
    private readonly Dictionary<FluidPackageLaunchSimulation, Entry> FluidPorts =
        new Dictionary<FluidPackageLaunchSimulation, Entry>();

    private readonly LaneCollector Collector = new LaneCollector();

    /// Probing a simulation for lanes can throw when it does not implement an accessor.
    /// Exceptions are expensive, and a base has one simulation type many thousands of
    /// times over, so the answer is remembered per type instead of per instance.
    private static readonly Dictionary<Type, bool> ReceiversUsable = new Dictionary<Type, bool>();
    private static readonly Dictionary<Type, bool> ProvidersUsable = new Dictionary<Type, bool>();

    /// <summary>
    /// Registering a whole base means walking every simulation on the map and hooking its
    /// lanes. On a large save that is far too much for one frame - it stalls the window
    /// long enough for Windows to call the game unresponsive - so it is drained a slice
    /// at a time. Until it finishes the overlay simply shows what has been picked up.
    /// </summary>
    private List<ILocalizedSimulation> Pending;
    private int PendingIndex;
    private Stopwatch RegistrationTimer;

    /// A fixed count per frame is either slow on a big base or a stall on a small one, so
    /// the sweep is given a slice of frame time instead and adapts to the machine.
    private const long RegistrationBudgetMilliseconds = 3;
    private const int RegistrationBatch = 64;

    private readonly Core.Logging.ILogger Logger;

    private IMapModel Map;
    private ISimulator Simulator;
    private Ticks LastAggregate;
    private Ticks LastKeepWarm;

    public EfficiencyTracker(Core.Logging.ILogger logger = null)
    {
        Logger = logger;
    }

    public IMapModel TrackedMap => Map;

    /// <summary>True while the initial sweep of the map is still being drained.</summary>
    public bool RegistrationPending => Pending != null;

    /// <summary>Raised once a map has been picked up and its simulations registered.</summary>
    public event Action<IMapModel> MapAttached;

    public bool TryGetEntries(GlobalChunkCoordinate chunk, out List<Entry> entries)
    {
        return EntriesByChunk.TryGetValue(chunk, out entries);
    }

    public bool TryGetEntry(ILocalizedSimulation localized, out Entry entry)
    {
        return Entries.TryGetValue(localized, out entry);
    }

    public bool TryGetSummary(IslandId island, out IslandSummary summary)
    {
        return Summaries.TryGetValue(island, out summary);
    }

    public bool TryGetPorts(IslandId island, out List<Entry> ports)
    {
        return IslandPorts.TryGetValue(island, out ports);
    }

    /// <summary>
    /// The entry moving the most on a platform, preferring one with a known ceiling so the
    /// side panel gauge has something honest to measure against.
    /// </summary>
    public Entry FindBusiest(IslandId island)
    {
        Entry best = null;

        foreach (Entry entry in Entries.Values)
        {
            if (entry.Island != island || !entry.HasKnownCeiling)
            {
                continue;
            }

            if (best == null || entry.ItemsPerMinute > best.ItemsPerMinute)
            {
                best = entry;
            }
        }

        return best;
    }

    /// <summary>Starts tracking a map, replacing whatever was tracked before.</summary>
    public void Attach(IMapModel map)
    {
        if (ReferenceEquals(Map, map))
        {
            return;
        }

        Detach();

        Map = map;
        Simulator = map?.Simulator;
        if (Simulator == null)
        {
            return;
        }

        // Snapshot now, register over the next few frames. Anything built in the meantime
        // arrives through OnSimulationCreated, and Register ignores duplicates.
        Pending = new List<ILocalizedSimulation>(Simulator.Simulations);
        PendingIndex = 0;
        RegistrationTimer = Stopwatch.StartNew();

        Simulator.OnSimulationCreated.Register(Register);
        Simulator.OnBeforeSimulationDestroyed.Register(Unregister);

        MapAttached?.Invoke(map);
    }

    /// <summary>Counts one fluid package leaving a space pipe port.</summary>
    public void OnFluidPackageLaunched(FluidPackageLaunchSimulation launch)
    {
        if (launch != null && FluidPorts.TryGetValue(launch, out Entry entry))
        {
            entry.OnFluidPackageLaunched();
        }
    }

    /// <summary>Registers the next slice of the map. Cheap and safe to call every frame.</summary>
    public void PumpRegistration()
    {
        if (Pending == null)
        {
            return;
        }

        // Check the clock every batch rather than every item; the timer costs more than
        // the work does otherwise.
        Stopwatch slice = Stopwatch.StartNew();
        while (PendingIndex < Pending.Count)
        {
            int stopAt = PendingIndex + RegistrationBatch;
            if (stopAt > Pending.Count)
            {
                stopAt = Pending.Count;
            }

            for (; PendingIndex < stopAt; PendingIndex++)
            {
                Register(Pending[PendingIndex]);
            }

            if (slice.ElapsedMilliseconds >= RegistrationBudgetMilliseconds)
            {
                break;
            }
        }

        if (PendingIndex < Pending.Count)
        {
            return;
        }

        Logger?.Info?.Log("Tracking " + Entries.Count + " flows across " + Pending.Count
            + " simulations (" + RegistrationTimer.ElapsedMilliseconds + "ms)");

        Pending = null;
        RegistrationTimer = null;
    }

    public void Detach()
    {
        Pending = null;
        RegistrationTimer = null;

        if (Simulator != null)
        {
            Simulator.OnSimulationCreated.TryUnregister(Register);
            Simulator.OnBeforeSimulationDestroyed.TryUnregister(Unregister);
        }

        foreach (Entry entry in Entries.Values)
        {
            Unhook(entry);
        }

        Entries.Clear();
        EntriesByChunk.Clear();
        FluidPorts.Clear();
        Summaries.Clear();
        IslandPorts.Clear();
        Map = null;
        Simulator = null;
    }

    public void Dispose()
    {
        Detach();
    }

    /// <summary>
    /// Keeps the measurement windows rolling while the overlay is hidden, so switching it
    /// on shows real numbers immediately instead of counting up from zero. This is a
    /// timestamp comparison per simulation once a second - it does not touch the lanes.
    /// </summary>
    public void KeepWarm()
    {
        if (Simulator == null)
        {
            return;
        }

        Ticks now = Simulator.SimulationTime;
        if (now.Value - LastKeepWarm.Value < KeepWarmInterval.Value)
        {
            return;
        }

        LastKeepWarm = now;

        foreach (Entry entry in Entries.Values)
        {
            entry.Meter.Advance(now);
        }
    }

    /// <summary>
    /// Rolls every meter forward and recomputes rates, statuses and platform totals.
    /// Called from the draw hook while the overlay is visible; throttled internally.
    /// </summary>
    public void Update()
    {
        if (Simulator == null)
        {
            return;
        }

        Ticks now = Simulator.SimulationTime;
        if (now.Value - LastAggregate.Value < AggregateInterval.Value)
        {
            return;
        }

        LastAggregate = now;

        foreach (IslandSummary summary in Summaries.Values)
        {
            summary.OutputItemsPerMinute = 0f;
            summary.BusiestItemsPerMinute = 0f;
            summary.OutputCeiling = 0f;
            summary.HasPorts = false;
            summary.Utilization = 0f;
            summary.MachineCount = 0;
            summary.BlockedCount = 0;
            summary.StarvedCount = 0;
        }

        foreach (Entry entry in Entries.Values)
        {
            entry.ItemsPerMinute = entry.Meter.ItemsPerMinute(now);
            if (entry.ItemsPerMinute > entry.PeakItemsPerMinute)
            {
                entry.PeakItemsPerMinute = entry.ItemsPerMinute;
            }

            entry.Saturation += (MeasureSaturation(entry) - entry.Saturation) * SaturationSmoothing;
            entry.Status = Classify(entry);
            Accumulate(entry);
        }

        foreach (IslandSummary summary in Summaries.Values)
        {
            if (summary.MachineCount > 0)
            {
                summary.Utilization /= summary.MachineCount;
                summary.Saturation /= summary.MachineCount;
            }
        }
    }

    private void Accumulate(Entry entry)
    {
        if (entry.Island == IslandId.Invalid)
        {
            return;
        }

        IslandSummary summary = GetOrCreateSummary(entry.Island);
        summary.MachineCount++;
        summary.Utilization += entry.Utilization;
        summary.Saturation += entry.Saturation;

        switch (entry.Status)
        {
            case EfficiencyStatus.Blocked:
                summary.BlockedCount++;
                break;
            case EfficiencyStatus.Starved:
                summary.StarvedCount++;
                break;
        }

        if (entry.ItemsPerMinute > summary.BusiestItemsPerMinute)
        {
            summary.BusiestItemsPerMinute = entry.ItemsPerMinute;
        }

        if (entry.IsOutputPort)
        {
            summary.HasPorts = true;
            summary.OutputItemsPerMinute += entry.ItemsPerMinute;
            summary.OutputCeiling += entry.MaxItemsPerMinute;
            summary.OutputSimulation = summary.OutputSimulation ?? entry.Localized;
        }
    }

    /// <summary>
    /// Does this carry goods off the platform?
    ///
    /// There is more than one way out and they are separate simulation types: a port
    /// feeding a space belt, a port packaging fluid into a space pipe, and - easy to miss -
    /// a port jumping items straight to a platform docked next to it, which needs no space
    /// belt at all. Counting only the space belt kind makes a platform that is directly
    /// docked to its neighbour look like it ships nothing.
    ///
    /// Receiving ports are deliberately not included: they are the platform's input.
    /// </summary>
    private static bool IsOutputPortSimulation(ISimulation simulation)
    {
        return simulation is SpaceBeltPortSenderSimulation
            || simulation is BeltPortTransferSimulation;
    }

    private IslandSummary GetOrCreateSummary(IslandId island)
    {
        if (!Summaries.TryGetValue(island, out IslandSummary summary))
        {
            summary = new IslandSummary();
            Summaries.Add(island, summary);
        }

        return summary;
    }

    /// <summary>
    /// How backed up the supply side is right now, 0 to 1.
    /// </summary>
    private static float MeasureSaturation(Entry entry)
    {
        // A fluid port holds a tank rather than a lane, and its level is the answer.
        if (entry.FluidSource != null)
        {
            return math.saturate(entry.FluidSource.FluidContainer.Level);
        }

        // Belts hand the same lane out as input and output; ports keep theirs on the
        // metered side. Either way, the lanes feeding this thing are what matter.
        IItemLane[] lanes = entry.InputLanes.Length > 0 ? entry.InputLanes : entry.MeteredLanes;
        if (lanes.Length == 0)
        {
            return 0f;
        }

        float total = 0f;
        for (int i = 0; i < lanes.Length; i++)
        {
            total += Occupancy(lanes[i]);
        }

        return total / lanes.Length;
    }

    private static float Occupancy(IItemLane lane)
    {
        int capacity = LaneCapacity(lane);
        if (capacity <= 0)
        {
            return lane.HasItem ? 1f : 0f;
        }

        return math.saturate(lane.ItemCount / (float)capacity);
    }

    private static int LaneCapacity(IItemLane lane)
    {
        switch (lane)
        {
            case BeltPathLane path:
                return path.Slots.Count;
            case FastBeltPathLane fast:
                return fast.ItemCapacity;
            case SingleItemLane _:
                return 1;
            default:
                return 0;
        }
    }

    private static EfficiencyStatus Classify(Entry entry)
    {
        if (entry.ItemsPerMinute > 0.01f)
        {
            return EfficiencyStatus.Running;
        }

        for (int i = 0; i < entry.MeteredLanes.Length; i++)
        {
            if (entry.MeteredLanes[i].HasItem)
            {
                // Finished goods sitting still: whatever is downstream is not taking them.
                return EfficiencyStatus.Blocked;
            }
        }

        return entry.IsSaturated ? EfficiencyStatus.Blocked : EfficiencyStatus.Starved;
    }

    private void Register(ILocalizedSimulation localized)
    {
        if (localized == null || localized.NumOccupiedChunks <= 0 || Entries.ContainsKey(localized))
        {
            return;
        }

        Collector.Reset();
        CollectLanes(localized.Simulation, Collector);

        SpaceFluidPortSenderSimulation fluidPort = localized.Simulation as SpaceFluidPortSenderSimulation;
        if (Collector.Metered.Count == 0 && fluidPort == null)
        {
            return;
        }

        Entry entry = new Entry
        {
            Localized = localized,
            MeteredLanes = Collector.Metered.ToArray(),
            InputLanes = Collector.Inputs.ToArray(),
            Chunk = localized.GetOccupiedChunk(0)
        };

        entry.Island = Map != null && Map.TryGetIsland(entry.Chunk, out IslandModel island)
            ? island.Id
            : IslandId.Invalid;
        if (entry.MeteredLanes.Length > 0)
        {
            entry.MaxItemsPerMinute = LookupMaxRate(localized, entry, out entry.MaxSource);
        }

        entry.IsOutputPort = fluidPort != null || IsOutputPortSimulation(localized.Simulation);

        if (fluidPort != null)
        {
            entry.FluidSource = fluidPort;

            // One package per launch duration is all a port can manage.
            float launchSeconds = fluidPort.LaunchDuration_T.FloatSeconds;
            entry.MaxItemsPerMinute = launchSeconds > 0f ? 60f / launchSeconds : 0f;
            entry.MaxSource = "launch-rate";
            FluidPorts[fluidPort.LaunchSimulation] = entry;
        }

        if (entry.Island != IslandId.Invalid && entry.IsOutputPort)
        {
            if (!IslandPorts.TryGetValue(entry.Island, out List<Entry> ports))
            {
                ports = new List<Entry>(4);
                IslandPorts.Add(entry.Island, ports);
            }

            ports.Add(entry);
            entry.Shared = GetOrCreateSummary(entry.Island).OutputAggregate;
        }

        for (int i = 0; i < entry.MeteredLanes.Length; i++)
        {
            if (entry.MeteredLanes[i] is IHookableItemReceiver hookable)
            {
                hookable.PostAcceptHook = (PostAcceptHookDelegate)Delegate.Combine(
                    hookable.PostAcceptHook,
                    new PostAcceptHookDelegate(entry.OnItemAccepted));
            }
        }

        Entries.Add(localized, entry);

        for (int i = 0; i < localized.NumOccupiedChunks; i++)
        {
            GlobalChunkCoordinate chunk = localized.GetOccupiedChunk(i);
            if (!EntriesByChunk.TryGetValue(chunk, out List<Entry> list))
            {
                list = new List<Entry>(4);
                EntriesByChunk.Add(chunk, list);
            }

            list.Add(entry);
        }
    }

    private void Unregister(ILocalizedSimulation localized)
    {
        if (localized == null || !Entries.TryGetValue(localized, out Entry entry))
        {
            return;
        }

        Unhook(entry);
        Entries.Remove(localized);

        if (IslandPorts.TryGetValue(entry.Island, out List<Entry> ports))
        {
            ports.Remove(entry);
        }

        for (int i = 0; i < localized.NumOccupiedChunks; i++)
        {
            if (EntriesByChunk.TryGetValue(localized.GetOccupiedChunk(i), out List<Entry> list))
            {
                list.Remove(entry);
            }
        }
    }

    private static void Unhook(Entry entry)
    {
        for (int i = 0; i < entry.MeteredLanes.Length; i++)
        {
            if (entry.MeteredLanes[i] is IHookableItemReceiver hookable)
            {
                hookable.PostAcceptHook = (PostAcceptHookDelegate)Delegate.Remove(
                    hookable.PostAcceptHook,
                    new PostAcceptHookDelegate(entry.OnItemAccepted));
            }
        }
    }

    /// <summary>
    /// The rate this would hit if nothing ever held it up. Buildings state it outright;
    /// belts and space paths derive it from how fast their lane moves items along.
    /// Speed research makes the real ceiling higher than the stated one, so a fully fed
    /// upgraded machine simply reads as pinned at 100%.
    /// </summary>
    private float LookupMaxRate(ILocalizedSimulation localized, Entry entry, out string source)
    {
        // Which ceiling is right depends on what the thing does. A belt is limited by how
        // fast its lane can carry items, and reading that from the live lane also picks up
        // any speed research. A machine is limited by how long it takes to process one
        // item, which only its definition knows - its output lane is just a belt, so
        // measuring a machine against lane speed would rate a perfectly busy machine at a
        // few percent of a belt it was never going to fill.
        bool transport = IsTransport(localized.Simulation, entry.MeteredLanes, entry.InputLanes);

        float fromLanes = 0f;
        for (int i = 0; i < entry.MeteredLanes.Length; i++)
        {
            fromLanes += MaxRateFromLaneSpeed(entry.MeteredLanes[i]);
        }

        float fromDefinition = LookupDefinitionRate(localized);

        if (transport && fromLanes > 0f)
        {
            source = "lane-speed";
            return fromLanes;
        }

        if (fromDefinition > 0f)
        {
            source = "definition";
            return fromDefinition;
        }

        if (fromLanes > 0f)
        {
            source = "lane-speed";
            return fromLanes;
        }

        source = "none";
        return 0f;
    }

    /// <summary>
    /// True when items pass straight through rather than being transformed: belts and
    /// space paths hand the same lane out as both input and output, and a terminal like a
    /// space belt port or the hub has no output lane at all.
    /// </summary>
    private static bool IsTransport(ISimulation simulation, IItemLane[] metered, IItemLane[] inputs)
    {
        if (simulation is IItemBundleSimulation)
        {
            return true;
        }

        for (int i = 0; i < metered.Length; i++)
        {
            for (int j = 0; j < inputs.Length; j++)
            {
                if (ReferenceEquals(metered[i], inputs[j]))
                {
                    return true;
                }
            }
        }

        return inputs.Length == 0;
    }

    /// <summary>Items per minute the building definition says this can process.</summary>
    private float LookupDefinitionRate(ILocalizedSimulation localized)
    {
        if (Map == null
            || !(localized is ILocalizedTileSimulation tileSimulation)
            || tileSimulation.NumOccupiedTiles <= 0
            || !Map.TryGetBuilding(tileSimulation.GetOccupiedTile(0), out BuildingModel building)
            || !building.Definition.CustomData.TryGet(out IBuildingEfficiencyData efficiency)
            || efficiency.OriginalProcessingDuration <= 0f)
        {
            return 0f;
        }

        int lanes = efficiency.ProcessingLaneCount > 0 ? efficiency.ProcessingLaneCount : 1;
        return 60f / efficiency.OriginalProcessingDuration * lanes;
    }

    /// <summary>Items per minute a belt-style lane can carry, from its speed and item spacing.</summary>
    public static float MaxRateFromLaneSpeed(IItemLane lane)
    {
        Ticks perItem;
        switch (lane)
        {
            case BeltPathLane path:
                perItem = LaneConstants.ItemSpacing / path.StepsPerTick_S;
                break;
            case FastBeltPathLane fast:
                perItem = LaneConstants.ItemSpacing / fast.StepsPerTick_;
                break;
            case SingleItemLane single:
                // A single item lane is exactly one item spacing long.
                perItem = single.Duration_T;
                break;
            default:
                return 0f;
        }

        return perItem.Value > 0 ? 60f / perItem.FloatSeconds : 0f;
    }

    /// <summary>
    /// Picks the lanes worth metering: a simulation's outputs, or its inputs when it has
    /// no outputs at all (space belt port senders, trash, the hub).
    /// </summary>
    private static void CollectLanes(ISimulation simulation, LaneCollector collector)
    {
        if (simulation is IItemSimulation item)
        {
            for (int i = 0; i < item.NumItemReceivers; i++)
            {
                if (TryGetReceiver(item, i) is IItemLane lane)
                {
                    collector.Inputs.Add(lane);
                }
            }

            for (int i = 0; i < item.NumItemProviders; i++)
            {
                if (TryGetProvider(item, i) is IItemLane lane)
                {
                    collector.Metered.Add(lane);
                }
            }

            if (collector.Metered.Count == 0)
            {
                collector.Metered.AddRange(collector.Inputs);
                collector.Inputs.Clear();
            }

            return;
        }

        if (simulation is IItemBundleSimulation bundle)
        {
            // Space belts carry several parallel lanes; count them all as one flow.
            bundle.TraverseLanes(collector);
        }
    }

    private static IItemReceiver TryGetReceiver(IItemSimulation simulation, int index)
    {
        Type type = simulation.GetType();
        if (ReceiversUsable.TryGetValue(type, out bool usable) && !usable)
        {
            return null;
        }

        try
        {
            IItemReceiver receiver = simulation.GetItemReceiver(index);
            ReceiversUsable[type] = true;
            return receiver;
        }
        catch (NotImplementedException)
        {
            // Not implemented on this type, and it never will be - stop asking.
            ReceiversUsable[type] = false;
            return null;
        }
    }

    private static IItemProvider TryGetProvider(IItemSimulation simulation, int index)
    {
        Type type = simulation.GetType();
        if (ProvidersUsable.TryGetValue(type, out bool usable) && !usable)
        {
            return null;
        }

        try
        {
            IItemProvider provider = simulation.GetItemProvider(index);
            ProvidersUsable[type] = true;
            return provider;
        }
        catch (NotImplementedException)
        {
            ProvidersUsable[type] = false;
            return null;
        }
    }

    /// Collects lanes from TraverseLanes and doubles as scratch space for CollectLanes.
    private sealed class LaneCollector : IItemLaneTraverser
    {
        public readonly List<IItemLane> Metered = new List<IItemLane>(4);
        public readonly List<IItemLane> Inputs = new List<IItemLane>(4);

        public void Reset()
        {
            Metered.Clear();
            Inputs.Clear();
        }

        public void Traverse(IItemLane lane)
        {
            Metered.Add(lane);
        }
    }
}
