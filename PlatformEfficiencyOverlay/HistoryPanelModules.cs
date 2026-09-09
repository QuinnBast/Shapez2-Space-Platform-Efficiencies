using System.Collections.Generic;
using Core.Localization;

/// <summary>
/// The two side panel modules that make up the history view - a range selector and the
/// chart - built the same way whether the subject is one machine or a whole platform.
/// </summary>
internal static class HistoryPanelModules
{
    private static readonly IText[] RangeTexts = BuildRangeTexts();

    /// <summary>
    /// Yields nothing when there is no history to show, which is the normal case for a
    /// belt or for anything without a stated capacity to be a percentage of.
    /// </summary>
    public static IEnumerable<IHUDSidePanelModuleData> For(EfficiencyTracker tracker,
        MachineHistory history, float ceiling)
    {
        if (!OverlayTuning.HistoryEnabled || history == null || ceiling <= 0f)
        {
            yield break;
        }

        // The range is a mod-wide setting rather than per-panel state: the panel is
        // rebuilt whenever the selection changes, so anything held here would be forgotten
        // constantly. Choosing 6h once and having it stay chosen is what you want anyway.
        yield return new HUDSidePanelModuleDropdownSelector.Data(
            RangeTexts,
            OverlayTuning.HistoryRange,
            index => OverlayTuning.HistoryRange = index);

        // Read through a delegate, so the chart follows both the live data and a change of
        // range without the panel being rebuilt.
        yield return new EfficiencyGraphModule.Data(
            buffer => history.Read(OverlayTuning.HistoryRange, tracker.SimulationSeconds, ceiling, buffer));
    }

    private static IText[] BuildRangeTexts()
    {
        IText[] texts = new IText[MachineHistory.Ranges];

        for (int range = 0; range < texts.Length; range++)
        {
            // Raw rather than localised: these are the mod's own labels, and there is no
            // string table to add keys to.
            texts[range] = new RawText(MachineHistory.RangeNames[range]);
        }

        return texts;
    }
}
