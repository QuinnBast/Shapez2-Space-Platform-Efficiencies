using System.Collections.Generic;
using System.Linq;
using Game.Core.Map.Simulation;
using ShapezShifter.Hijack;

/// <summary>
/// Adds the history chart to a machine's own side panel, under whatever the game already
/// shows there - including its efficiency gauge, which answers "right now" while the chart
/// answers "and for the last six hours".
///
/// Every building definition already has a modules provider registered, so unlike the
/// island panels this only has to wrap what exists rather than fill in the gaps.
/// </summary>
public class BuildingPanelModules : IBuildingModulesRewirer
{
    private readonly EfficiencyTracker Tracker;

    public BuildingPanelModules(EfficiencyTracker tracker)
    {
        Tracker = tracker;
    }

    public void AddModules(BuildingsModulesLookup modulesLookup)
    {
        Dictionary<BuildingDefinitionId, IBuildingModules> providers = modulesLookup?.BuildingModulesMap;
        if (providers == null)
        {
            return;
        }

        foreach (BuildingDefinitionId definitionId in providers.Keys.ToArray())
        {
            if (!(providers[definitionId] is Provider))
            {
                providers[definitionId] = new Provider(providers[definitionId], Tracker);
            }
        }
    }

    private sealed class Provider : IBuildingModules
    {
        private readonly IBuildingModules Inner;
        private readonly EfficiencyTracker Tracker;

        public Provider(IBuildingModules inner, EfficiencyTracker tracker)
        {
            Inner = inner;
            Tracker = tracker;
        }

        /// <summary>
        /// The definition-only overload describes a building type rather than a placed
        /// one - the build menu's preview - where there is no history to show.
        /// </summary>
        public IEnumerable<IHUDSidePanelModuleData> GetInfoModules(IBuildingDefinition definition)
        {
            return Inner != null
                ? Inner.GetInfoModules(definition)
                : Enumerable.Empty<IHUDSidePanelModuleData>();
        }

        public IEnumerable<IHUDSidePanelModuleData> GetInfoModules(IMapModel map, BuildingModel building)
        {
            if (Inner != null)
            {
                foreach (IHUDSidePanelModuleData module in Inner.GetInfoModules(map, building))
                {
                    yield return module;
                }
            }

            EfficiencyTracker.Entry entry = Find(map, building);
            if (entry == null)
            {
                yield break;
            }

            foreach (IHUDSidePanelModuleData module in
                HistoryPanelModules.For(Tracker, entry.History, entry.Ceiling))
            {
                yield return module;
            }
        }

        private EfficiencyTracker.Entry Find(IMapModel map, BuildingModel building)
        {
            if (map?.Simulator == null)
            {
                return null;
            }

            return map.Simulator.TryFindTileSimulation(building.Tile_G, out ILocalizedTileSimulation localized)
                && Tracker.TryGetEntry(localized, out EfficiencyTracker.Entry entry)
                ? entry
                : null;
        }
    }
}
