using Core.Localization;
using UnityEngine;

/// <summary>
/// Puts the overlay in the bottom-right visualization bar next to the island grid and
/// super chunk coordinates, instead of on a hotkey nobody can discover.
///
/// The base class earns its keep here: it persists the on/off state to the game
/// preferences, drives the button's pressed look, and tweens <c>Alpha</c> so the overlay
/// fades in and out the way the built-in visualizations do.
/// </summary>
public class EfficiencyVisualization : HUDVisualization
{
    /// <summary>
    /// The renderer to drive. The HUD builds visualizations through the game's own
    /// dependency factory, which knows nothing about this mod, so the instance is handed
    /// over here rather than through the constructor.
    /// </summary>
    public static EfficiencyOverlayRenderer Target;

    /// <summary>
    /// The preferences key and the handle the mod uses to find its own button again after
    /// a hot reload, when the old button's type no longer matches this assembly's.
    /// </summary>
    public const string VisualizationId = "platform-efficiency";

    private readonly EfficiencyOverlayRenderer Renderer;

    public override bool IsAvailable => true;

    public EfficiencyVisualization(Player player, Viewport viewport)
        : base(player, viewport, VisualizationId)
    {
        Renderer = Target;
    }

    public override Sprite GetIcon()
    {
        return Globals.Resources.Icons.StatSpeed;
    }

    public override IText GetTitle()
    {
        return new RawText("Platform efficiency");
    }

    public override void OnGameUpdate(InputDownstreamContext context, FrameDrawOptions options)
    {
        base.OnGameUpdate(context, options);

        if (Renderer != null)
        {
            Renderer.Alpha = Alpha;
        }
    }
}
