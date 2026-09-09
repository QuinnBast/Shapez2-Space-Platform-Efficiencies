using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
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

        /// Everything this has ever handed over, which is what the history samples.
        public long TotalItems;

        /// Throughput over time. Only processing machines get one - see MachineHistory
        /// for why the belts are left out.
        public MachineHistory History;

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
        /// Driven from the measured rate rather than from the arrivals themselves - see
        /// AggregateLane for why relaying them cannot work.
        public AggregateLane Aggregate;

        /// Set for a platform's output ports, so all of them feed one platform-wide gauge.
        public AggregateLane Shared;

        /// This carries goods off the platform - a space belt port or a space pipe port.
        public bool IsOutputPort;

        /// Items pass straight through rather than being transformed: belts, space belts,
        /// space pipes, ports. Almost all of a large save is this, so it is the axis that
        /// decides whether a per-flow feature is affordable.
        public bool IsTransport;

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
            TotalItems++;

            // Deliberately not relayed to Aggregate. Several lanes hand over in the same
            // tick, so every one of those arrivals would carry the same timestamp and the
            // gaps the gauge averages would collapse. It is driven from the measured rate
            // instead, once a second, in KeepWarm.
            Shared?.Report(item, Ticks.Zero);
        }

        /// <summary>True when the percentage is measured against a known ceiling rather
        /// than against the best rate this happens to have managed so far.</summary>
        public bool HasKnownCeiling => MaxItemsPerMinute > 0f;

        /// <summary>
        /// Whether this port is part of the factory at all.
        ///
        /// A platform can carry more port buildings than it uses - a bank of them built
        /// out and only some connected - and an unused one has no business in the
        /// platform's ceiling: twelve idle ports among thirty-six turned a platform
        /// shipping everything it could into a platform at 69%. Anything that has moved
        /// an item counts, and so does anything with goods waiting on it, so a port that
        /// is merely blocked still counts against the total, which is the point of it.
        /// </summary>
        public bool InUse
        {
            get
            {
                if (TotalItems > 0)
                {
                    return true;
                }

                for (int i = 0; i < MeteredLanes.Length; i++)
                {
                    if (MeteredLanes[i].HasItem)
                    {
                        return true;
                    }
                }

                return FluidSource != null && FluidSource.FluidContainer.Level > 0f;
            }
        }

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

        /// Whether the platform cannot ship: its ports have goods waiting and nothing
        /// leaving. Averaging every flow's value instead - which is what this used to be -
        /// put a pip on any platform where a few belts happened to be holding stock.
        public float Saturation;

        /// Ports actually in use, and how many of those are stuck.
        public int PortCount;
        public int StuckPortCount;

        public bool IsSaturated => Saturation >= OverlayTuning.SaturationThreshold;

        public int MachineCount;
        public int BlockedCount;
        public int StarvedCount;

        /// <summary>The one number worth putting on the platform: what it ships, or what
        /// flows through it when it has no ports of its own.</summary>
        public float HeadlineItemsPerMinute => HasPorts ? OutputItemsPerMinute : BusiestItemsPerMinute;

        /// How much of what this platform could ship it has been shipping, over time.
        /// Only platforms with output ports get one: without ports there is no ceiling to
        /// be a percentage of, which is the same reason they get no gauge either.
        public MachineHistory History;

        /// The ceiling the history was recorded against, so a reader scales the live tip
        /// the same way the closed buckets were scaled.
        public float HistoryCeiling;
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
    /// Roughly what one MachineHistory costs: 300 one-byte buckets plus its bookkeeping
    /// and the headers of the three arrays it is made of. Only used to report the total.
    private const int HistoryBytes = 420;

    private const long RegistrationBudgetMilliseconds = 3;
    private const int RegistrationBatch = 64;

    private readonly Core.Logging.ILogger Logger;

    private IMapModel Map;
    private ISimulator Simulator;

    /// The research speeds, which scale every ceiling that comes from a definition.
    private ISimulationSpeedsProvider Speeds;
    private ResearchSpeedId BuildingSpeedId;
    private int LastSpeedValue = -1;

    /// How many times a map has been picked up, and how many times every history has been
    /// rolled forward. Both are only here to be reported: an empty graph is either nothing
    /// happening or nothing being recorded, and these separate the two.
    private int AttachCount;
    private int AdvanceCount;
    private float LastAdvanceSeconds;
    private Ticks LastAggregate;
    private Ticks LastKeepWarm;

    public EfficiencyTracker(Core.Logging.ILogger logger = null)
    {
        Logger = logger;
    }

    public IMapModel TrackedMap => Map;

    /// <summary>Simulated seconds, which is the clock every history is kept on.</summary>
    public float SimulationSeconds => Simulator == null ? 0f : Simulator.SimulationTime.FloatSeconds;

    /// <summary>The same clock, for the meters, which count in ticks.</summary>
    public Ticks SimulationTicks => Simulator == null ? Ticks.Zero : Simulator.SimulationTime;

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

    /// <summary>
    /// How far the first pass over the map has got, or null once it is done.
    ///
    /// Worth saying out loud in the diagnostics: registration is spread over frames, so
    /// everything reads as empty for the first minute or so of a session - and again for a
    /// minute after a hot reload, which starts a fresh tracker on an already-running map.
    /// An empty report is otherwise indistinguishable from a broken one.
    /// </summary>
    public string DescribeProgress()
    {
        List<ILocalizedSimulation> pending = Pending;

        return pending == null || PendingIndex >= pending.Count
            ? null
            : "still registering: " + PendingIndex + " of " + pending.Count
                + " simulations - the numbers below are incomplete";
    }

    /// <summary>Starts tracking a map, replacing whatever was tracked before.</summary>
    public void Attach(IMapModel map)
    {
        if (ReferenceEquals(Map, map))
        {
            return;
        }

        Detach();

        AttachCount++;
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

    /// <summary>
    /// How the tracked flows split between things that move items and things that change
    /// them. Anything costed per flow - history for graphs, for instance - is really costed
    /// against the transport count, because that is nearly all of it.
    /// </summary>
    public string DescribeComposition()
    {
        int transport = 0;
        int machines = 0;
        int ports = 0;

        foreach (Entry entry in Entries.Values)
        {
            if (entry.IsOutputPort)
            {
                ports++;
            }
            else if (entry.IsTransport)
            {
                transport++;
            }
            else
            {
                machines++;
            }
        }

        string progress = DescribeProgress();

        int histories = 0;

        foreach (Entry entry in Entries.Values)
        {
            if (entry.History != null)
            {
                histories++;
            }
        }

        foreach (IslandSummary summary in Summaries.Values)
        {
            if (summary.History != null)
            {
                histories++;
            }
        }

        return (progress == null ? "" : "  " + progress + "\n")
            + "  of those: " + machines + " processing machines, " + transport
            + " belts and pass-throughs, " + ports + " platform ports\n"
            + "  history on " + histories + " of them, about "
            + (histories * HistoryBytes / 1048576f).ToString("0.0") + " MB";
    }

    /// <summary>
    /// Every input to a platform's percentage: what the chart is drawn from, port by port.
    ///
    /// A platform's number is not any one flow's - it is everything leaving through its
    /// ports against everything those ports could carry - so this reports that sum and
    /// the terms of it. Which is the only way to see whether a total that reads low is a
    /// port measuring low or a port contributing a ceiling and no goods.
    /// </summary>
    public string ExplainIsland(IslandId island)
    {
        StringBuilder text = new StringBuilder();
        text.Append("platform ").Append(island);

        if (!Summaries.TryGetValue(island, out IslandSummary summary))
        {
            return text.Append(": nothing tracked on it yet").ToString();
        }

        if (!IslandPorts.TryGetValue(island, out List<Entry> ports))
        {
            return text.Append(": no output ports, so no platform total - the panel shows"
                + " its busiest flow instead").ToString();
        }

        long items = 0;
        float rate = 0f;
        float ceiling = 0f;

        for (int i = 0; i < ports.Count; i++)
        {
            items += ports[i].TotalItems;
            rate += ports[i].ItemsPerMinute;
            ceiling += ports[i].MaxItemsPerMinute;
        }

        text.Append(": ").Append(rate.ToString("0.#")).Append(" of ").Append(ceiling.ToString("0.#"))
            .Append("/min = ").Append(ceiling > 0f ? (rate / ceiling * 100f).ToString("0.#") : "0")
            .Append('%');

        text.Append('\n').Append("  history ceiling ").Append(summary.HistoryCeiling.ToString("0.#"))
            .Append("/min, latest ")
            .Append(summary.History == null ? "none" : (summary.History.Latest(0) * 100f).ToString("0.#") + "%")
            .Append(", recorded ")
            .Append(summary.History == null ? 0 : summary.History.CoveredSeconds(0)).Append('s');

        text.Append('\n').Append("  ").Append(ports.Count).Append(" port(s), ")
            .Append(summary.PortCount).Append(" in use, ").Append(summary.StuckPortCount)
            .Append(" stuck -> saturation ").Append(summary.Saturation.ToString("0.##"))
            .Append(summary.IsSaturated ? " (pip shown)" : " (no pip)");

        text.Append('\n').Append("  ").Append(items)
            .Append(" item(s) through them since tracking began");

        for (int i = 0; i < ports.Count && i < 12; i++)
        {
            Entry port = ports[i];

            text.Append('\n').Append("    ").Append(port.Localized.Simulation.GetType().Name)
                .Append(": ").Append(port.ItemsPerMinute.ToString("0.#"))
                .Append(" of ").Append(port.MaxItemsPerMinute.ToString("0.#"))
                .Append(" (").Append(port.MaxSource).Append(", ")
                .Append(port.MeteredLanes.Length).Append(" lane(s)) = ")
                .Append((port.Utilization * 100f).ToString("0.#")).Append('%')
                .Append(port.InUse ? "" : ", unused - not counted");
        }

        if (ports.Count > 12)
        {
            text.Append('\n').Append("    ...and ").Append(ports.Count - 12).Append(" more");
        }

        return text.ToString();
    }

    /// <summary>
    /// Every input to one flow's percentage and its pip, in the order the arithmetic uses
    /// them. For when a number is wrong and the question is which part of it.
    /// </summary>
    public string Explain(Entry entry)
    {
        if (entry == null)
        {
            return "not tracked";
        }

        StringBuilder text = new StringBuilder();

        text.Append(entry.Localized.Simulation.GetType().Name)
            .Append(": measured ").Append(entry.ItemsPerMinute.ToString("0.##")).Append("/min")
            .Append(", ceiling ").Append(entry.MaxItemsPerMinute.ToString("0.##"))
            .Append(" from ").Append(entry.MaxSource)
            .Append(" -> ").Append((entry.Utilization * 100f).ToString("0.#")).Append('%');

        text.Append('\n').Append("  research speed ")
            .Append(Speeds == null ? "unbound" : Speeds.GetSpeedValue(BuildingSpeedId).ToString())
            .Append(", factor ").Append(SpeedFactor.ToString("0.###"));

        if (Map != null
            && entry.Localized is ILocalizedTileSimulation tiles
            && tiles.NumOccupiedTiles > 0
            && Map.TryGetBuilding(tiles.GetOccupiedTile(0), out BuildingModel building))
        {
            text.Append('\n').Append("  building ").Append(building.Definition.Id);

            if (building.Definition.CustomData.TryGet(out IBuildingEfficiencyData efficiency))
            {
                text.Append(", original duration ").Append(efficiency.OriginalProcessingDuration.ToString("0.###"))
                    .Append("s over ").Append(efficiency.ProcessingLaneCount).Append(" lane(s)");
            }
            else
            {
                text.Append(", no efficiency data");
            }
        }

        text.Append('\n').Append("  saturation ").Append(entry.Saturation.ToString("0.##"))
            .Append(entry.IsSaturated ? " (pip shown)" : " (no pip)")
            .Append(", threshold ").Append(OverlayTuning.SaturationThreshold);

        if (entry.FluidSource != null)
        {
            text.Append('\n').Append("  fluid level ")
                .Append(entry.FluidSource.FluidContainer.Level.ToString("0.###"));
        }

        Describe(text, "metered", entry.MeteredLanes);
        Describe(text, "input", entry.InputLanes);

        return text.ToString();
    }

    private static void Describe(StringBuilder text, string role, IItemLane[] lanes)
    {
        for (int i = 0; i < lanes.Length && i < 6; i++)
        {
            IItemLane lane = lanes[i];

            text.Append('\n').Append("  ").Append(role).Append(' ').Append(i).Append(": ")
                .Append(lane.GetType().Name)
                .Append(", holds ").Append(Capacity(lane))
                .Append(" in ").Append(Traversal(lane).ToString("0.###")).Append('s')
                .Append(" -> ").Append(MaxRateFromLaneSpeed(lane).ToString("0.#")).Append("/min")
                .Append(", items ").Append(lane.ItemCount);
        }
    }

    private static int Capacity(IItemLane lane)
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

    private static float Traversal(IItemLane lane)
    {
        switch (lane)
        {
            case BeltPathLane path:
                return path.Duration_T.FloatSeconds;
            case FastBeltPathLane fast:
                return fast.Duration_T.FloatSeconds;
            case SingleItemLane single:
                return single.Duration_T.FloatSeconds;
            default:
                return 0f;
        }
    }

    /// <summary>
    /// Whether recording is actually happening, and on what clock.
    ///
    /// A graph that stays empty has two quite different causes - nothing is passing through
    /// the machine, or nothing is being rolled forward - and they look identical from the
    /// panel. Buckets close on simulation time, so a clock that is not moving means no
    /// bucket ever closes however busy the factory is.
    /// </summary>
    public string DescribeClock()
    {
        int histories = 0;
        int recorded = 0;
        int busiest = 0;

        foreach (Entry entry in Entries.Values)
        {
            if (entry.History == null)
            {
                continue;
            }

            histories++;
            int covered = entry.History.CoveredSeconds(0);

            if (covered > 0)
            {
                recorded++;
            }

            if (covered > busiest)
            {
                busiest = covered;
            }
        }

        return "clock " + SimulationSeconds.ToString("0.0") + "s simulated"
            + ", rolled " + AdvanceCount + " time(s), last at " + LastAdvanceSeconds.ToString("0.0") + "s"
            + "\n  map picked up " + AttachCount + " time(s)"
            + ", " + Entries.Count + " flow(s), " + histories + " recording"
            + "\n  " + recorded + " of them have closed a bucket, deepest "
            + busiest + "s of the 1m range";
    }

    /// <summary>
    /// Everything tracked on one platform, by simulation type, with what each kind is
    /// measured against.
    ///
    /// The answer to "why is there no graph on this one": either nothing on it is tracked,
    /// or nothing on it ships anything off-platform, or what does has no ceiling to be a
    /// percentage of. Which one it is, is only visible from here.
    /// </summary>
    public string DescribeIsland(IslandId island, int top)
    {
        Dictionary<string, int> counts = new Dictionary<string, int>();
        Dictionary<string, string> ceilings = new Dictionary<string, string>();
        int tracked = 0;

        foreach (Entry entry in Entries.Values)
        {
            if (entry.Island != island)
            {
                continue;
            }

            tracked++;
            string name = entry.Localized.Simulation.GetType().Name;

            counts.TryGetValue(name, out int seen);
            counts[name] = seen + 1;

            ceilings[name] = entry.MaxItemsPerMinute.ToString("0.#") + "/min from " + entry.MaxSource
                + (entry.IsOutputPort ? ", port" : entry.IsTransport ? ", transport" : ", machine")
                + (entry.History != null ? ", history" : "");
        }

        StringBuilder text = new StringBuilder();
        text.Append("platform ").Append(island).Append(": ").Append(tracked).Append(" tracked flow(s)");

        if (Summaries.TryGetValue(island, out IslandSummary summary))
        {
            text.Append('\n').Append("  ports ")
                .Append(IslandPorts.TryGetValue(island, out List<Entry> ports) ? ports.Count : 0)
                .Append(", output ceiling ").Append(summary.HistoryCeiling.ToString("0.#"))
                .Append("/min, history ").Append(summary.History != null ? "on" : "off");
        }
        else
        {
            text.Append('\n').Append("  no summary - nothing on it has reported yet");
        }

        foreach (KeyValuePair<string, int> pair in counts.OrderByDescending(p => p.Value).Take(top))
        {
            text.Append('\n').Append("  ").Append(pair.Value.ToString().PadLeft(5))
                .Append("  ").Append(pair.Key.PadRight(40)).Append(ceilings[pair.Key]);
        }

        return text.ToString();
    }

    /// <summary>
    /// The tracked flows by simulation type, commonest first, with how each was classified.
    ///
    /// The transport/machine split alone is coarse: "transport" means "held to lane speed",
    /// which covers mergers, splitters and lifts along with the belts. This says which
    /// simulations those actually are.
    /// </summary>
    public string DescribeTypes(int top)
    {
        Dictionary<string, int> counts = new Dictionary<string, int>();
        Dictionary<string, string> kinds = new Dictionary<string, string>();

        foreach (Entry entry in Entries.Values)
        {
            string name = entry.Localized.Simulation.GetType().Name;

            counts.TryGetValue(name, out int seen);
            counts[name] = seen + 1;

            kinds[name] = entry.IsOutputPort ? "port" : entry.IsTransport ? "transport" : "machine";
        }

        StringBuilder text = new StringBuilder();
        string progress = DescribeProgress();
        if (progress != null)
        {
            text.Append(progress).Append('\n');
        }

        text.Append(counts.Count).Append(" distinct simulation types tracked");

        foreach (KeyValuePair<string, int> pair in counts.OrderByDescending(p => p.Value).Take(top))
        {
            text.Append('\n').Append("  ").Append(pair.Value.ToString().PadLeft(7))
                .Append("  ").Append(pair.Key.PadRight(46))
                .Append(kinds[pair.Key]);
        }

        return text.ToString();
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
        Logger?.Info?.Log(DescribeComposition());

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

        // Both throttles below are stamps on simulation time, and simulation time starts
        // over with every save. Leaving them set means the next save inherits a stamp from
        // a clock that no longer exists.
        LastKeepWarm = Ticks.Zero;
        LastAggregate = Ticks.Zero;
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

        // A backwards clock has to roll rather than wait. The stamp is meant to be in the
        // past; if it is in the future the save has changed underneath us, and waiting for
        // simulation time to catch up would mean waiting hours.
        long sinceWarm = now.Value - LastKeepWarm.Value;
        if (sinceWarm >= 0 && sinceWarm < KeepWarmInterval.Value)
        {
            return;
        }

        LastKeepWarm = now;

        // Deliberately here rather than in Update: this runs whether or not the overlay is
        // showing, so a graph opened for the first time has real history behind it instead
        // of starting from the moment someone looked. Simulation time, so a paused game
        // records nothing rather than recording a stall.
        float seconds = now.FloatSeconds;
        AdvanceCount++;
        LastAdvanceSeconds = seconds;

        foreach (Entry entry in Entries.Values)
        {
            entry.Meter.Advance(now);
            entry.History?.Advance(seconds, entry.TotalItems, entry.MaxItemsPerMinute);

            // Only ever a handful: an aggregate exists once a panel has asked for a gauge
            // over one, and it stops driving as soon as that panel closes.
            entry.Aggregate?.Drive(Simulator, entry.Localized,
                entry.Meter.ItemsPerMinute(now), now);
        }

        AdvancePlatformHistories(seconds);
    }

    /// <summary>
    /// Rolls each platform's history forward from its output ports.
    ///
    /// Totalled here rather than read off the summary because the summary is only
    /// recomputed while the overlay is on screen, and history has to accrue whether
    /// anyone is looking or not.
    /// </summary>
    private void AdvancePlatformHistories(float seconds)
    {
        if (!OverlayTuning.HistoryEnabled)
        {
            return;
        }

        foreach (KeyValuePair<IslandId, List<Entry>> pair in IslandPorts)
        {
            List<Entry> ports = pair.Value;
            long total = 0;
            float ceiling = 0f;

            for (int i = 0; i < ports.Count; i++)
            {
                if (!ports[i].InUse)
                {
                    continue;
                }

                total += ports[i].TotalItems;
                ceiling += ports[i].MaxItemsPerMinute;
            }

            if (ceiling <= 0f)
            {
                continue;
            }

            IslandSummary summary = GetOrCreateSummary(pair.Key);
            summary.HistoryCeiling = ceiling;

            if (summary.History == null)
            {
                summary.History = new MachineHistory();
            }

            summary.History.Advance(seconds, total, ceiling);
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

        long sinceAggregate = now.Value - LastAggregate.Value;
        if (sinceAggregate >= 0 && sinceAggregate < AggregateInterval.Value)
        {
            return;
        }

        LastAggregate = now;

        // An upgrade researched mid-session raises what every machine can do, and a
        // ceiling from before it would read as a machine that had slowed down.
        if (Speeds != null && Speeds.GetSpeedValue(BuildingSpeedId) != LastSpeedValue)
        {
            RefreshDefinitionCeilings();
        }

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
            summary.PortCount = 0;
            summary.StuckPortCount = 0;
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
            }

            // A platform is holding things up when the way out is blocked, which is what
            // its ports say. With no ports of its own - a space belt, a pipe - the flows
            // on it are all there is to go on.
            summary.Saturation = summary.PortCount > 0
                ? summary.StuckPortCount / (float)summary.PortCount
                : (summary.MachineCount > 0 ? summary.BlockedCount / (float)summary.MachineCount : 0f);
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

        if (entry.IsOutputPort && entry.InUse)
        {
            summary.HasPorts = true;
            summary.OutputItemsPerMinute += entry.ItemsPerMinute;
            summary.OutputCeiling += entry.MaxItemsPerMinute;
            summary.OutputSimulation = summary.OutputSimulation ?? entry.Localized;
            summary.PortCount++;

            if (entry.Saturation >= OverlayTuning.SaturationThreshold)
            {
                summary.StuckPortCount++;
            }
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
    /// <summary>
    /// What ships goods off a platform.
    ///
    /// Both types, because they belong to two different port systems rather than to two
    /// halves of one. SpaceBeltPortSenderSimulation launches onto a space belt;
    /// BeltPortTransferSimulation is the jump between two platforms sitting side by side,
    /// and a base wired up mostly that way has almost no senders at all - so counting only
    /// senders leaves most platforms with no measurable output and no history.
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
        // A fluid port holds a tank rather than a lane. Its level is not the answer: a
        // buffer drains and refills as packages launch, so half full is what healthy
        // looks like. Only a tank with no room left is actually holding anything up.
        if (entry.FluidSource != null)
        {
            return entry.FluidSource.FluidContainer.Level >= 0.98f ? 1f : 0f;
        }

        return Stuck(entry);
    }

    /// <summary>
    /// Whether goods are stuck here: something on the way out, and nothing going out.
    ///
    /// Three attempts at asking the lane itself all failed, and the reason is worth
    /// recording. Occupancy made a belt running flat out look backed up, because a
    /// flat-out belt is nearly full. FreeStepsAtTheEnd is small for most of the time on
    /// any moving belt, because the last item is near the entrance. And MaxStep_S - which
    /// is what CanAcceptItem actually tests - is an internal maintained during the
    /// simulation's own update and chained through NextLane; measured from outside it
    /// reads deeply negative on a perfectly healthy belt carrying ten items at full rate.
    /// None of the three can be read from here.
    ///
    /// What can be read is our own measurement, which is the thing we trust everywhere
    /// else: if items are sitting on the output and the rate is zero, they are not
    /// leaving. If the rate is above zero, by definition nothing is stuck.
    /// </summary>
    private static float Stuck(Entry entry)
    {
        if (entry.ItemsPerMinute > 0.01f)
        {
            return 0f;
        }

        for (int i = 0; i < entry.MeteredLanes.Length; i++)
        {
            if (entry.MeteredLanes[i].HasItem)
            {
                return 1f;
            }
        }

        return 0f;
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

        // Only the things measured against their own stated rate: a processing machine,
        // or a port, whose ceiling is its launch rate. Both are worth a graph, and
        // together they are a fifth of what the tracker sees.
        if (OverlayTuning.HistoryEnabled && entry.MaxSource == "definition")
        {
            entry.History = new MachineHistory();
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
        float fromLanes = 0f;
        for (int i = 0; i < entry.MeteredLanes.Length; i++)
        {
            fromLanes += MaxRateFromLaneSpeed(entry.MeteredLanes[i]);
        }

        float fromDefinition = LookupDefinitionRate(localized);
        float fromJump = JumpLaneRate(localized.Simulation);

        // A belt port states nothing, and its lane lies: the jump runs at several times
        // conveyor speed, so dividing by item spacing says it could carry several times a
        // belt. What actually limits it is how many items may be in the air at once.
        if (fromJump > 0f)
        {
            source = "jump-lane";
            return fromJump;
        }

        // Having no inputs does not make something transport. An extractor is fed by the
        // patch under it rather than by a lane, and its ceiling is how fast it can pull -
        // which only the definition knows. Measuring one against the belt it feeds rates a
        // flat-out extractor at a fraction of a belt it was never going to fill. So a
        // no-input flow only falls back to lane speed when nothing states a rate for it:
        // space belt port senders, trash, the hub.
        bool preferLanes = IsPassThrough(localized.Simulation, entry.MeteredLanes, entry.InputLanes)
            || fromDefinition <= 0f;

        if (preferLanes && fromLanes > 0f)
        {
            source = "lane-speed";
        }
        else if (fromDefinition > 0f)
        {
            source = "definition";
        }
        else if (fromLanes > 0f)
        {
            source = "lane-speed";
        }
        else
        {
            source = "none";
        }

        // What it is measured against is the honest classification. Mergers, splitters and
        // lifts state no rate of their own and are held to lane speed exactly as a belt is,
        // so counting them as machines only inflated the machine count.
        entry.IsTransport = source != "definition";

        return source == "definition" ? fromDefinition : source == "lane-speed" ? fromLanes : 0f;
    }

    /// <summary>
    /// True when items pass straight through rather than being transformed: belts and
    /// space paths hand the same lane out as both input and output, and a space belt
    /// carries a whole bundle of them.
    /// </summary>
    private static bool IsPassThrough(ISimulation simulation, IItemLane[] metered, IItemLane[] inputs)
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

        return false;
    }

    /// <summary>
    /// What a belt port between two platforms can really pass: the belt that feeds it.
    ///
    /// Its jump lane runs at a multiple of conveyor speed, so measuring it by the usual
    /// spacing arithmetic makes it several belts' worth - and no port can carry more than
    /// the single belt handing items to it, whatever its own lane could manage. Taking the
    /// base speed back out of the multiple gives that belt's rate exactly.
    ///
    /// Dividing by the crossing time instead, as this used to, was nearly right but not
    /// quite: the jump is longer for a port reaching further, while the multiplier is
    /// fixed at the longest reach, so a short hop came out above a belt and a port running
    /// flat out read in the high eighties.
    /// </summary>
    private static float JumpLaneRate(ISimulation simulation)
    {
        if (!(simulation is BeltPortTransferSimulation port)
            || !(port.JumpLane.BeltSpeed is MultipleBeltSpeed jump))
        {
            return 0f;
        }

        Ticks perItem = LaneConstants.ItemSpacing / jump.BaseSpeed.StepsPerTick;

        return perItem.Value > 0 ? 60f / perItem.FloatSeconds : 0f;
    }

    /// <summary>
    /// Items per minute the building definition says this can process, at the research the
    /// player actually has.
    ///
    /// The duration on the definition is the *original* one, before upgrades. The game
    /// never uses it raw: every stat and the efficiency gauge itself run it through
    /// StructureStatProcessingTime.ComputeEffectiveDuration, which divides by the research
    /// speed as a percentage. Dividing measured throughput by the unscaled rate instead
    /// meant comparing what a machine is doing against what it could do at a research
    /// level the player does not have - which is why a machine running flat out read in
    /// the eighties, and why the number moved when nothing about the factory had.
    /// </summary>
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

        return 60f / efficiency.OriginalProcessingDuration * lanes * SpeedFactor;
    }

    /// <summary>
    /// The research multiplier the game applies to a building's processing duration, or 1
    /// until the session hands the provider over.
    /// </summary>
    private float SpeedFactor
    {
        get
        {
            if (Speeds == null)
            {
                return 1f;
            }

            float factor = Speeds.GetSpeedValue(BuildingSpeedId) / 100f;

            return factor > 0f ? factor : 1f;
        }
    }

    /// <summary>
    /// Hands over the research speeds, which the definition-based ceilings scale by.
    /// Anything already registered is recomputed, since registration usually runs first.
    /// </summary>
    public void BindSpeeds(ISimulationSpeedsProvider speeds, ResearchSpeedId buildingSpeedId)
    {
        Speeds = speeds;
        BuildingSpeedId = buildingSpeedId;
        RefreshDefinitionCeilings();
    }

    /// <summary>
    /// Recomputes the ceilings that come from a definition. Called when the speeds arrive
    /// and whenever the research changes - an upgrade mid-session would otherwise leave
    /// every machine measured against the rate it had before.
    /// </summary>
    private void RefreshDefinitionCeilings()
    {
        LastSpeedValue = Speeds == null ? 0 : Speeds.GetSpeedValue(BuildingSpeedId);

        foreach (Entry entry in Entries.Values)
        {
            if (entry.MaxSource != "definition")
            {
                continue;
            }

            float rate = LookupDefinitionRate(entry.Localized);

            if (rate > 0f)
            {
                entry.MaxItemsPerMinute = rate;
            }
        }
    }

    /// <summary>
    /// Items per minute a lane can carry: how many fit on it, over how long a traversal
    /// takes.
    ///
    /// For an ordinary belt this is the same answer as dividing item spacing by lane
    /// speed, since a belt's capacity is its length over that spacing. It differs - and is
    /// right where the other was wrong - for the fast lanes the space islands use, which
    /// are enormously long and move enormously fast, and whose items are not spaced at
    /// LaneConstants.ItemSpacing at all. Measuring those by spacing gave a space merger a
    /// ceiling of 86,400 a minute and a reading of 1.8% while it worked perfectly.
    /// </summary>
    public static float MaxRateFromLaneSpeed(IItemLane lane)
    {
        switch (lane)
        {
            case BeltPathLane path:
                return Carrying(path.Slots.Count, path.Duration_T);
            case FastBeltPathLane fast:
                return Carrying(fast.ItemCapacity, fast.Duration_T);
            case SingleItemLane single:
                // One item at a time, so its whole traversal is one item.
                return Carrying(1, single.Duration_T);
            default:
                return 0f;
        }
    }

    private static float Carrying(int capacity, Ticks traversal)
    {
        float seconds = traversal.FloatSeconds;

        return capacity > 0 && seconds > 0f ? capacity * 60f / seconds : 0f;
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
