using System.Collections.Generic;
using System.Linq;
using Game.Core.Research;
using ShapezShifter.Hijack;

/// <summary>
/// Puts the platform's measured numbers into its side panel, so selecting a platform
/// answers "how much is this actually shipping" without hunting for a belt reader.
///
/// The headline figure is the game's own building efficiency gauge - the same live widget
/// you get when selecting a machine, showing items per minute and a percentage against
/// capacity. No vanilla island panel uses it, so it is built here by hand.
///
/// The game builds each island's panel from one provider registered per island definition,
/// and it only registers them for islands that have something special to say - space
/// belts, converters, miners and the like. Ordinary building platforms have no provider at
/// all, so wrapping what already exists is not enough: those definitions need one added.
/// </summary>
public class PlatformPanelModules : IIslandModulesRewirer
{
    private readonly EfficiencyTracker Tracker;

    private IslandsModulesLookup Lookup;
    private IMapModel BoundMap;

    /// Needed to scale the gauge; supplied once the game session is up.
    private ISimulationSpeedsProvider Speeds;
    private ResearchSpeedId BeltSpeedId;

    public PlatformPanelModules(EfficiencyTracker tracker)
    {
        Tracker = tracker;
    }

    /// <summary>
    /// Hands over the session bits the gauge needs. Until this arrives the panel falls
    /// back to plain text, which needs nothing.
    /// </summary>
    public void BindSession(ISimulationSpeedsProvider speeds, ResearchSpeedId beltSpeedId)
    {
        Speeds = speeds;
        BeltSpeedId = beltSpeedId;
    }

    public void AddModules(IslandsModulesLookup modulesLookup)
    {
        Lookup = modulesLookup;

        // Wrap the providers vanilla registered, so its modules still show above ours.
        Dictionary<IslandDefinitionId, IIslandModuleDataProvider> providers = modulesLookup.IslandModulesMap;
        foreach (IslandDefinitionId definitionId in providers.Keys.ToArray())
        {
            providers[definitionId] = new Provider(providers[definitionId], this);
        }

        // A map may already be loaded by the time the HUD is rebuilt.
        if (BoundMap != null)
        {
            SyncProviders(BoundMap);
        }
    }

    /// <summary>
    /// Makes sure every island definition actually present on the map has a provider, so
    /// plain platforms get a panel entry too. Safe to call repeatedly.
    /// </summary>
    public void SyncProviders(IMapModel map)
    {
        if (!ReferenceEquals(BoundMap, map))
        {
            BoundMap?.OnIslandAdded.TryUnregister(OnIslandAdded);
            BoundMap = map;
            BoundMap?.OnIslandAdded.Register(OnIslandAdded);
        }

        if (Lookup == null || map == null)
        {
            return;
        }

        foreach (IslandModel island in map.Islands)
        {
            EnsureProvider(island.DefinitionId);
        }
    }

    private void OnIslandAdded(IslandModel island)
    {
        EnsureProvider(island.DefinitionId);
    }

    private void EnsureProvider(IslandDefinitionId definitionId)
    {
        Dictionary<IslandDefinitionId, IIslandModuleDataProvider> providers = Lookup?.IslandModulesMap;
        if (providers == null || providers.ContainsKey(definitionId))
        {
            return;
        }

        providers.Add(definitionId, new Provider(null, this));
    }

    /// <summary>
    /// Builds the live gauge for one measured flow.
    ///
    /// The module works out its own 100% mark as <c>baseDuration / (speedValue / 100)</c>,
    /// so handing it the ceiling we already measured multiplied by that same factor makes
    /// the two cancel: the gauge then reads full when the flow hits the rate this overlay
    /// considers its capacity, and the two never disagree.
    /// </summary>
    /// <summary>
    /// The game's gauge, for one flow carried on one real lane.
    ///
    /// It derives its rate from the gaps between arrivals on the lane it is handed, so it
    /// is exact for a single lane and unusable for anything aggregated: a stand-in fed by
    /// several lanes puts many arrivals in the same simulation tick, and the gaps it
    /// divides by collapse to nothing. So anything with more than one lane now gets the
    /// chart's own measured figures, and this is offered only where it is right.
    /// </summary>
    private IHUDSidePanelModuleData BuildGauge(EfficiencyTracker.Entry entry)
    {
        if (entry == null || Speeds == null || !entry.HasKnownCeiling || entry.MeteredLanes.Length != 1)
        {
            return null;
        }

        float speedFactor = Speeds.GetSpeedValue(BeltSpeedId) / 100f;
        if (speedFactor <= 0f)
        {
            return null;
        }

        // The module works out its own 100% mark as baseDuration / (speedValue / 100), so
        // handing it the ceiling we measured multiplied by that same factor makes the two
        // cancel: it then reads full at the rate this overlay calls capacity.
        return new HUDSidePanelModuleBuildingEfficiency.Data(
            default(BuildingModel),
            entry.Localized,
            entry.MeteredLanes[0],
            BeltSpeedId,
            60f / entry.MaxItemsPerMinute * speedFactor);
    }

    private sealed class Provider : IIslandModuleDataProvider
    {
        private readonly IIslandModuleDataProvider Inner;
        private readonly PlatformPanelModules Owner;

        public Provider(IIslandModuleDataProvider inner, PlatformPanelModules owner)
        {
            Inner = inner;
            Owner = owner;
        }

        public IEnumerable<IHUDSidePanelModuleData> GetStats()
        {
            return Inner != null ? Inner.GetStats() : Enumerable.Empty<IHUDSidePanelModuleData>();
        }

        public IEnumerable<IHUDSidePanelModuleData> GetModules(IslandModel island)
        {
            if (Inner != null)
            {
                foreach (IHUDSidePanelModuleData module in Inner.GetModules(island))
                {
                    yield return module;
                }
            }

            EfficiencyTracker tracker = Owner.Tracker;
            if (!tracker.TryGetSummary(island.Id, out EfficiencyTracker.IslandSummary summary)
                || summary.MachineCount == 0)
            {
                yield break;
            }

            // No gauge for a platform that has ports: its total comes from all of them at
            // once, which is the one case the game's gauge cannot measure. The chart's own
            // caption carries the rate instead.
            if (!summary.HasPorts)
            {
                IHUDSidePanelModuleData gauge = Owner.BuildGauge(Owner.Tracker.FindBusiest(island.Id));

                if (gauge != null)
                {
                    yield return gauge;
                }
            }

            foreach (IHUDSidePanelModuleData module in HistoryPanelModules.For(
                Owner.Tracker, summary.History, summary.HistoryCeiling))
            {
                yield return module;
            }
        }

    }
}
