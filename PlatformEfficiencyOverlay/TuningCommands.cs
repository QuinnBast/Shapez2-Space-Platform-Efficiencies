using System;
using Game.Core.Map.Simulation;
using ShapezShifter.Hijack;
using ShapezShifter.Kit;
using ILogger = Core.Logging.ILogger;

/// <summary>
/// Exposes the look-and-feel numbers on the debug console (F1), because a new mod DLL
/// cannot be loaded without restarting the game - anything that has to be judged by eye
/// is far quicker to adjust in place.
///
/// Registering as an <see cref="IConsoleRewirer"/> directly rather than through
/// ModConsoleCommandsCreator keeps the command names short: that helper prefixes
/// everything with the assembly name, which would make these "platformefficiencyoverlay.*".
/// </summary>
public class TuningCommands : IConsoleRewirer
{
    private const string Prefix = "peo.";

    private readonly ILogger Logger;
    private readonly EfficiencyTracker Tracker;

    public TuningCommands(ILogger logger, EfficiencyTracker tracker)
    {
        Logger = logger;
        Tracker = tracker;
    }

    public void RegisterCommands(IDebugConsole console)
    {
        Register(console, "report", null, context => Report(context));

        Register(console, "blocked-shade", new DebugConsole.FloatOption("shade", 0f, 1f),
            context =>
            {
                OverlayTuning.SetBlockedShade(context.GetFloat(0));
                Report(context);
            });

        Register(console, "tint-alpha", new DebugConsole.FloatOption("alpha", 0f, 1f),
            context =>
            {
                OverlayTuning.TintAlpha = context.GetFloat(0);
                Report(context);
            });

        Register(console, "tint-height", new DebugConsole.FloatOption("height", 0f, 6f),
            context =>
            {
                OverlayTuning.SetTintHeight(context.GetFloat(0));
                Report(context);
            });

        Register(console, "label-zoom", new DebugConsole.FloatOption("zoom", 0f, 1500f),
            context =>
            {
                OverlayTuning.MachineLabelZoom = context.GetFloat(0);
                Report(context);
            });

        Register(console, "label-width", new DebugConsole.FloatOption("tiles", 0.2f, 8f),
            context =>
            {
                OverlayTuning.MachineLabelMaxWidth = context.GetFloat(0);
                Report(context);
            });

        Register(console, "tracking", new DebugConsole.BoolOption("enabled"),
            context =>
            {
                OverlayTuning.TrackingEnabled = context.GetBool(0);
                Report(context);
            });

        Register(console, "pips", new DebugConsole.BoolOption("show"),
            context =>
            {
                OverlayTuning.ShowSaturationPips = context.GetBool(0);
                Report(context);
            });

        Register(console, "saturation-threshold", new DebugConsole.FloatOption("level", 0f, 1f),
            context =>
            {
                OverlayTuning.SaturationThreshold = context.GetFloat(0);
                Report(context);
            });

        Register(console, "platform-labels", new DebugConsole.BoolOption("show"),
            context =>
            {
                OverlayTuning.ShowPlatformLabels = context.GetBool(0);
                Report(context);
            });

        Register(console, "machine-labels", new DebugConsole.BoolOption("show"),
            context =>
            {
                OverlayTuning.ShowMachineLabels = context.GetBool(0);
                Report(context);
            });

        Register(console, "label-percent", new DebugConsole.BoolOption("percent"),
            context =>
            {
                OverlayTuning.LabelAsPercent = context.GetBool(0);
                Report(context);
            });

        Register(console, "inspect", null, Inspect);

        Register(console, "history", null, History);

        Register(console, "range", new DebugConsole.StringOption("5m|30m|1h|6h"), context =>
        {
            string wanted = context.GetString(0);

            for (int range = 0; range < MachineHistory.Ranges; range++)
            {
                if (string.Equals(MachineHistory.RangeNames[range], wanted, StringComparison.OrdinalIgnoreCase))
                {
                    OverlayTuning.HistoryRange = range;
                    Report(context);
                    return;
                }
            }

            context.Output?.Invoke("Ranges are: " + string.Join(", ", MachineHistory.RangeNames));
        });

        Register(console, "history-enabled", new DebugConsole.BoolOption("enabled"), context =>
        {
            OverlayTuning.HistoryEnabled = context.GetBool(0);
            Report(context);
            context.Output?.Invoke("Applies to flows registered from now on - reload the save to apply it to everything.");
        });

        Register(console, "breakdown", null, context =>
            context.Output?.Invoke(Tracker.DescribeComposition()));

        Register(console, "types", null, context =>
        {
            // The console prints one line per call, so split the report up - and mirror it
            // into the log, because in-game console text cannot be selected or copied.
            foreach (string line in Tracker.DescribeTypes(int.MaxValue).Split('\n'))
            {
                string trimmed = line.TrimEnd('\r');
                context.Output?.Invoke(trimmed);
                Logger.Info?.Log(trimmed);
            }
        });
    }

    /// <summary>
    /// Dumps what the overlay actually measured for the selected buildings, including the
    /// ceiling it is dividing by and where that ceiling came from. A percentage that looks
    /// wrong is nearly always a wrong denominator, and this is how to tell which.
    /// </summary>
    private void Inspect(DebugConsole.CommandContext context)
    {
        Action<string> output = context.Output;
        if (output == null)
        {
            return;
        }

        IMapModel map = Tracker.TrackedMap;
        if (map == null)
        {
            output("No map is being tracked.");
            return;
        }

        Player player = GameHelper.Core?.LocalPlayer;
        if (player == null || player.InteractionState.BuildingSelection.Count == 0)
        {
            output("Select one or more buildings first, then run peo.inspect again.");
            return;
        }

        int reported = 0;
        foreach (BuildingModel building in player.InteractionState.BuildingSelection)
        {
            if (reported >= 12)
            {
                output("...more selected than shown.");
                break;
            }

            reported++;
            output(Describe(map, building));
        }
    }

    /// <summary>
    /// The selected machine's throughput over the chosen window, as a sparkline.
    ///
    /// A crude readout, but it is the honest one: it prints the same buckets the graph
    /// will draw, so if this looks wrong the data is wrong rather than the drawing.
    /// </summary>
    private void History(DebugConsole.CommandContext context)
    {
        Action<string> output = context.Output;
        if (output == null)
        {
            return;
        }

        IMapModel map = Tracker.TrackedMap;
        if (map == null)
        {
            output("No map is being tracked.");
            return;
        }

        Player player = GameHelper.Core?.LocalPlayer;
        if (player == null || player.InteractionState.BuildingSelection.Count == 0)
        {
            output("Select a machine first, then run peo.history again.");
            return;
        }

        int range = OverlayTuning.HistoryRange;
        float now = map.Simulator != null ? map.Simulator.SimulationTime.FloatSeconds : 0f;
        float[] series = new float[MachineHistory.Buckets + 1];
        int shown = 0;

        foreach (BuildingModel building in player.InteractionState.BuildingSelection)
        {
            if (shown >= 4)
            {
                output("...more selected than shown.");
                break;
            }

            if (!map.Simulator.TryFindTileSimulation(building.Tile_G, out ILocalizedTileSimulation localized)
                || !Tracker.TryGetEntry(localized, out EfficiencyTracker.Entry entry))
            {
                continue;
            }

            shown++;

            if (entry.History == null)
            {
                output(building.Definition.Id + ": no history - only machines measured "
                    + "against their own stated rate keep one (this one uses " + entry.MaxSource + ")");
                continue;
            }

            int count = entry.History.Read(range, now, series);
            float peak = 0f;

            for (int i = 0; i < count; i++)
            {
                if (series[i] > peak)
                {
                    peak = series[i];
                }
            }

            output(building.Definition.Id + ": " + MachineHistory.RangeNames[range]
                + ", " + Covered(entry.History.CoveredSeconds(range)) + " recorded"
                + ", peak " + peak.ToString("0") + "/min"
                + ", now " + entry.ItemsPerMinute.ToString("0") + "/min"
                + ", ceiling " + entry.MaxItemsPerMinute.ToString("0") + "/min");

            output("  " + Sparkline(series, count, entry.HasKnownCeiling ? entry.MaxItemsPerMinute : peak));
        }

        if (shown == 0)
        {
            output("Nothing selected is tracked.");
        }
    }

    /// Scaled against the ceiling, not against its own peak, so the height means capacity
    /// used - the same thing the colour wash means - rather than "busy for this machine".
    private static string Sparkline(float[] series, int count, float ceiling)
    {
        if (count <= 0)
        {
            return "(nothing recorded yet)";
        }

        const string Levels = " .:-=+*#%@";
        char[] line = new char[count];

        for (int i = 0; i < count; i++)
        {
            float fraction = ceiling > 0f ? series[i] / ceiling : 0f;
            int level = (int)(fraction * (Levels.Length - 1) + 0.5f);

            if (level < 0)
            {
                level = 0;
            }
            else if (level >= Levels.Length)
            {
                level = Levels.Length - 1;
            }

            line[i] = Levels[level];
        }

        return new string(line);
    }

    private static string Covered(int seconds)
    {
        if (seconds < 60)
        {
            return seconds + "s";
        }

        return seconds < 3600
            ? seconds / 60 + "m"
            : (seconds / 360) / 10f + "h";
    }

    private string Describe(IMapModel map, BuildingModel building)
    {
        string name = building.Definition.Id.ToString();

        if (!map.Simulator.TryFindTileSimulation(building.Tile_G, out ILocalizedTileSimulation localized))
        {
            return name + ": no simulation at " + building.Tile_G;
        }

        if (!Tracker.TryGetEntry(localized, out EfficiencyTracker.Entry entry))
        {
            return name + ": not tracked (nothing item-carrying to measure)";
        }

        string ceiling = entry.HasKnownCeiling
            ? entry.MaxItemsPerMinute.ToString("0.0") + "/min from " + entry.MaxSource
            : "unknown, comparing against best seen " + entry.PeakItemsPerMinute.ToString("0.0") + "/min";

        return name + ": measured " + entry.ItemsPerMinute.ToString("0.0") + "/min"
            + ", ceiling " + ceiling
            + ", so " + (int)(entry.Utilization * 100f) + "%"
            + ", " + entry.Status
            + ", supply " + (int)(entry.Saturation * 100f) + "% full"
            + (entry.IsSaturated ? " (fed - look here or downstream)" : " (starved - look upstream)")
            + ", metering " + entry.MeteredLanes.Length + " lane(s)"
            + " on " + localized.Simulation.GetType().Name;
    }

    private static void Report(DebugConsole.CommandContext context)
    {
        context.Output?.Invoke(OverlayTuning.Describe());
    }

    /// One command failing to register must not take the rest of them down with it.
    private void Register(IDebugConsole console, string id, DebugConsole.ConsoleOption option,
        Action<DebugConsole.CommandContext> handler)
    {
        try
        {
            if (option == null)
            {
                console.Register(Prefix + id, handler);
            }
            else
            {
                console.Register(Prefix + id, option, handler);
            }
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }
    }
}
