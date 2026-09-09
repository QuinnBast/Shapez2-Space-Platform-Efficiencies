/// <summary>
/// The handful of numbers that can only really be judged with the game in front of you.
/// They live here rather than as constants so the debug console can change them at runtime -
/// a new mod DLL cannot be loaded without restarting the game, so anything that needs
/// eyeballing is worth making adjustable in place.
/// </summary>
public static class OverlayTuning
{
    /// Bumped whenever something changes that was baked into a cached mesh or position.
    public static int Version { get; private set; }

    /// <summary>Height above the platform floor for the colour wash.</summary>
    public static float TintHeight = 1.1f;

    /// <summary>Opacity of the colour wash at full fade-in.</summary>
    public static float TintAlpha = 0.55f;

    /// <summary>
    /// Show a pip on anything whose supply is backed up, marking it as fed. A dark tile
    /// with a pip is the constraint; a dark tile without one is starved from upstream.
    /// </summary>
    public static bool ShowSaturationPips = true;

    /// <summary>How full the feeding lanes must be to count as backed up, 0 to 1.</summary>
    public static float SaturationThreshold = 0.5f;

    /// <summary>Brightness of the 0% end of the gradient. Lower is darker.</summary>
    public static float BlockedShade = 0.42f;

    /// <summary>
    /// Measure at all. Turning this off unhooks every lane and forgets every flow, which
    /// is the escape hatch if the mod is ever costing more than it is worth on a big save.
    /// </summary>
    public static bool TrackingEnabled = true;

    /// <summary>
    /// Keep throughput history per machine. About 45 MB on a completed save, and it has to
    /// be decided before a machine is registered, so turning it off only takes effect for
    /// flows registered after that - reload the save to reclaim what is already held.
    /// </summary>
    public static bool HistoryEnabled = true;

    /// <summary>
    /// Zoom beyond which belts stop being washed and only machines and ports are.
    ///
    /// Belts are four fifths of everything on a big map and each one covers many tiles, so
    /// they are nearly all of the drawing cost - and zoomed out they are a few pixels wide
    /// and tell you nothing you cannot read from the machines they feed. Raise it to see
    /// them further out, lower it if panning stutters.
    /// </summary>
    public static float BeltZoom = 260f;

    /// <summary>Which window the history readout shows - an index into MachineHistory.RangeNames.</summary>
    public static int HistoryRange;

    /// <summary>
    /// Cap the side panel's content and let it scroll instead of running off the bottom of
    /// the screen. Off means the vanilla behaviour: the panel grows without limit.
    /// </summary>
    public static bool PanelScrolling = true;

    /// <summary>How much of the screen height the panel's content may take up.</summary>
    public static float PanelHeightFraction = 0.8f;

    /// <summary>
    /// Draw the measured number on each machine. Off by default: the labels are drawn by
    /// the UI renderer, which ignores depth, so they show through whatever is above them.
    /// The colour wash carries the at-a-glance story and clicking a machine gives the
    /// exact figures, which is the better trade.
    /// </summary>
    public static bool ShowMachineLabels;

    /// <summary>
    /// Draw the rolled-up number on each platform in space view. Off for the same reason
    /// as the machine labels: drawn by the UI renderer, so it shows through anything above
    /// it, and the platform's side panel carries the same figure properly.
    /// </summary>
    public static bool ShowPlatformLabels;

    /// <summary>Camera distance past which per-machine numbers stop being drawn.</summary>
    public static float MachineLabelZoom = 80f;

    /// <summary>Widest a per-machine number may get, in tiles.</summary>
    public static float MachineLabelMaxWidth = 2.2f;

    /// <summary>
    /// Show per-machine numbers as a percentage of what the machine could do, rather than
    /// as items per minute.
    /// </summary>
    public static bool LabelAsPercent = true;

    public static void SetTintHeight(float value)
    {
        TintHeight = value;
        Version++;
    }

    public static void SetBlockedShade(float value)
    {
        BlockedShade = value;
        Version++;
    }

    public static string Describe()
    {
        return "tracking " + (TrackingEnabled ? "on" : "off")
            + " | history " + (HistoryEnabled ? "on" : "off")
            + " | range " + MachineHistory.RangeNames[HistoryRange]
            + " | belt-zoom " + BeltZoom
            + " | panel-scroll " + (PanelScrolling ? "on" : "off")
            + " | panel-height " + PanelHeightFraction
            + " | pips " + (ShowSaturationPips ? "on" : "off")
            + " | saturation-threshold " + SaturationThreshold
            + " | platform-labels " + (ShowPlatformLabels ? "on" : "off")
            + " | machine-labels " + (ShowMachineLabels ? "on" : "off")
            + " | tint-height " + TintHeight
            + " | tint-alpha " + TintAlpha
            + " | blocked-shade " + BlockedShade
            + " | label-zoom " + MachineLabelZoom
            + " | label-width " + MachineLabelMaxWidth
            + " | labels " + (LabelAsPercent ? "percent" : "rate");
    }
}
