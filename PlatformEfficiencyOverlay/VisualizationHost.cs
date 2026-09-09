using System;
using System.Collections.Generic;
using ILogger = Core.Logging.ILogger;

/// <summary>
/// Adds and removes this mod's entry in the HUD's visualization bar.
///
/// Removal matters more than it looks. A mod that adds a button and never takes it away
/// cannot be disabled mid-session, and cannot be hot reloaded either - the old button
/// stays, wired to the old code, and the reloaded mod adds a second one beside it.
///
/// Everything here matches on the visualization's <c>Id</c> string rather than its type,
/// deliberately: after a reload the button in the bar belongs to the *previous* assembly,
/// so its type is not the same type any more, even though it is the same mod. A string
/// comparison is the only thing that still recognises it.
/// </summary>
public class VisualizationHost
{
    private readonly ILogger Logger;
    private readonly string Id;

    public VisualizationHost(ILogger logger, string id)
    {
        Logger = logger;
        Id = id;
    }

    /// <summary>
    /// Adds our visualization, first clearing out any earlier one with the same id - which
    /// is what a previous generation of this assembly would have left behind.
    /// </summary>
    public bool Add<TVisualization>(HUDVisualizations host) where TVisualization : HUDVisualization
    {
        Remove(host);

        try
        {
            host.AddVisualization<TVisualization>();
            return true;
        }
        catch (Exception exception)
        {
            // The HUD builds visualizations through the game's dependency factory. If it
            // will not take a type from a mod, say so rather than dying.
            Logger.Exception?.LogException(exception);
            return false;
        }
    }

    /// <summary>
    /// Takes our button out of the bar and releases it. Best effort: each step is guarded,
    /// because a half-removed button is still better than an exception during teardown.
    /// </summary>
    public void Remove(HUDVisualizations host)
    {
        if (host == null)
        {
            return;
        }

        List<HUDVisualizations.VisualizationInstance> instances;

        try
        {
            instances = host.Visualizations;
            if (instances == null)
            {
                return;
            }
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
            return;
        }

        for (int i = instances.Count - 1; i >= 0; i--)
        {
            HUDVisualizations.VisualizationInstance instance = instances[i];

            if (instance?.Visualization == null || instance.Visualization.Id != Id)
            {
                continue;
            }

            try
            {
                instance.Dispose();
            }
            catch (Exception exception)
            {
                Logger.Exception?.LogException(exception);
            }

            ReleaseButton(host, instance.Button);

            try
            {
                instances.RemoveAt(i);
            }
            catch (Exception exception)
            {
                Logger.Exception?.LogException(exception);
            }
        }
    }

    /// <summary>
    /// Hands the button back the way its parent would have. HUDComponent.Dispose tears a
    /// view down but leaves the object in place - it is the parent that returns it through
    /// the view provider, so doing only the former leaves the icon on screen.
    /// </summary>
    private void ReleaseButton(HUDVisualizations host, HUDIconButton button)
    {
        if (button == null)
        {
            return;
        }

        try
        {
            host.Children?.Remove(button);

            if (host.LoadedChildren != null && host.LoadedChildren.Remove(button))
            {
                host.ViewInstanceProvider?.ReleaseView(button);
            }
            else
            {
                button.Dispose();
            }
        }
        catch (Exception exception)
        {
            Logger.Exception?.LogException(exception);
        }
    }
}
