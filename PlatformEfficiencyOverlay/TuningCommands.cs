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

        Register(console, "breakdown", null, context =>
            context.Output?.Invoke(Tracker.DescribeComposition()));
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
