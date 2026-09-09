using System;
using JetBrains.Annotations;
using MonoMod.RuntimeDetour;
using ShapezShifter.Flow;
using ShapezShifter.Hijack;
using ShapezShifter.SharpDetour;
using UnityEngine;
using Game.Core.Simulation;
using ILogger = Core.Logging.ILogger;

/// <summary>
/// Platform Efficiency Overlay.
///
/// Adds a toggle to the bottom-right visualization bar that washes every machine, belt and
/// platform in the colour of what it is actually doing - flowing, starved or jammed - with
/// the measured items per minute drawn on top. In space view the same data is rolled up
/// per platform.
///
/// Three hooks: <see cref="MapDrawer.Draw"/> for rendering (which also hands us the map
/// and the visible-chunk list), <see cref="HUDVisualizations.OnGameUpdate"/> to slot the
/// toggle into the HUD, and a per-frame tick for the fallback hotkey.
/// </summary>
[UsedImplicitly]
public class PlatformEfficiencyMod : IMod
{
    /// Only used if the overlay could not be registered as a proper visualization.
    private const KeyCode FallbackToggleKey = KeyCode.F5;

    private readonly ILogger Logger;
    private readonly EfficiencyTracker Tracker;
    private readonly EfficiencyOverlayRenderer Renderer;
    private readonly Hook DrawHook;
    private readonly Hook VisualizationsHook;
    private readonly Hook SessionHook;
    private readonly Hook FluidLaunchHook;
    private readonly RewirerHandle TickHandle;
    private readonly RewirerHandle PanelHandle;
    private readonly RewirerHandle BuildingPanelHandle;
    private readonly RewirerHandle CommandsHandle;

    private readonly PlatformPanelModules Panels;
    private readonly VisualizationHost Visualizations;

    /// The HUD is rebuilt per session, so we track which one we have already extended.
    private HUDVisualizations ExtendedHud;
    private bool VisualizationFailed;

    public PlatformEfficiencyMod(ILogger logger)
    {
        Logger = logger;
        Tracker = new EfficiencyTracker(logger);
        Renderer = new EfficiencyOverlayRenderer(Tracker);
        EfficiencyVisualization.Target = Renderer;

        DrawHook = DetourHelper.CreatePostfixHook<MapDrawer, FrameDrawOptionsNoLOD>(
            (drawer, options) => drawer.Draw(options),
            OnMapDrawn);

        VisualizationsHook = DetourHelper.CreatePostfixHook<HUDVisualizations, InputDownstreamContext, FrameDrawOptions>(
            (visualizations, context, options) => visualizations.OnGameUpdate(context, options),
            OnVisualizationsUpdated);

        // Runs once per session, at a point where the speed provider is bound and the
        // belt research id is known - both needed to scale the side panel gauge.
        SessionHook = DetourHelper.CreatePostfixHook<GameSessionOrchestrator, IslandsModulesLookup>(
            (orchestrator, lookup) => orchestrator.InjectIslandsModuleProviders(lookup),
            OnSessionReady);

        Panels = new PlatformPanelModules(Tracker);
        Visualizations = new VisualizationHost(logger, EfficiencyVisualization.VisualizationId);
        Tracker.MapAttached += Panels.SyncProviders;

        // A space pipe port has no lane to hook - it packages fluid into a buffer - but
        // every package it sends goes through this one call.
        FluidLaunchHook = DetourHelper.CreatePostfixHook<FluidPackageLaunchSimulation, Ticks, FluidPackageData, Ticks>(
            (launch, duration, package, excess) => launch.CreateNewLaunch(duration, package, excess),
            (launch, duration, package, excess) => Tracker.OnFluidPackageLaunched(launch));

        TickHandle = this.OnTick(OnTick);
        PanelHandle = GameRewirers.AddRewirer(Panels);
        BuildingPanelHandle = GameRewirers.AddRewirer(new BuildingPanelModules(Tracker));
        CommandsHandle = GameRewirers.AddRewirer(new TuningCommands(logger, Tracker));

        Logger.Info?.Log("Platform Efficiency Overlay ready.");
    }

    private void OnSessionReady(GameSessionOrchestrator orchestrator, IslandsModulesLookup lookup)
    {
        try
        {
            Panels.BindSession(
                orchestrator.DependencyContainer.Resolve<ISimulationSpeedsProvider>(),
                orchestrator.GetBeltBuildingSpeedId());
        }
        catch (Exception exception)
        {
            // Without these the panel still works, it just falls back to plain text.
            Logger.Exception?.LogException(exception);
        }
    }

    /// <summary>
    /// Adds our toggle to the visualization bar the first time we see a given HUD. Done
    /// from the update rather than the HUD's own construction so the icon prefab and its
    /// parent transform are guaranteed to be in place.
    /// </summary>
    private void OnVisualizationsUpdated(HUDVisualizations visualizations, InputDownstreamContext context,
        FrameDrawOptions options)
    {
        if (VisualizationFailed || ReferenceEquals(ExtendedHud, visualizations))
        {
            return;
        }

        ExtendedHud = visualizations;

        if (!Visualizations.Add<EfficiencyVisualization>(visualizations))
        {
            VisualizationFailed = true;
            Logger.Info?.Log("Could not add the efficiency toggle to the HUD - press F5 instead.");
        }
    }

    private void OnMapDrawn(MapDrawer drawer, FrameDrawOptionsNoLOD options)
    {
        try
        {
            Renderer.Draw(drawer, options);
        }
        catch (Exception exception)
        {
            // A throw here would take the frame down with it, so report once and carry on.
            Logger.Exception?.LogException(exception);
            Renderer.Alpha = 0f;
        }
    }

    private void OnTick(float deltaTime)
    {
        if (VisualizationFailed && Input.GetKeyDown(FallbackToggleKey))
        {
            Renderer.Alpha = Renderer.Alpha > 0.5f ? 0f : 1f;
        }
    }

    public void Dispose()
    {
        // Take the button out of the HUD. Without this the mod cannot be disabled without
        // restarting, and a hot reload leaves the old button behind next to the new one.
        Visualizations.Remove(ExtendedHud);
        ExtendedHud = null;

        GameRewirers.RemoveRewirer(CommandsHandle);
        GameRewirers.RemoveRewirer(BuildingPanelHandle);
        GameRewirers.RemoveRewirer(PanelHandle);
        GameRewirers.RemoveRewirer(TickHandle);
        FluidLaunchHook?.Dispose();
        SessionHook?.Dispose();
        VisualizationsHook?.Dispose();
        DrawHook?.Dispose();
        Tracker.MapAttached -= Panels.SyncProviders;
        // The chart prefab is a component from this assembly. Leaving it behind would have
        // the next generation of the mod instantiating the old assembly's copy.
        EfficiencyGraphTemplate.Release();
        Renderer.Dispose();
        Tracker.Dispose();
        EfficiencyVisualization.Target = null;
    }
}
