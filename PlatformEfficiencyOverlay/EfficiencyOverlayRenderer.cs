using System;
using System.Collections.Generic;
using Game.Core.Coordinates;
using Game.Core.Map.Simulation;
using Game.Core.Rendering.Culling;
using Unity.Mathematics;
using UnityEngine;

/// <summary>
/// Paints the measured throughput into the world: a colour wash over every machine and
/// belt telling you whether it is flowing, starved or jammed, and the actual items per
/// minute drawn on top of it.
///
/// Zoomed out into platform view the same data is rolled up per platform, so a whole
/// factory can be read at a glance.
///
/// Everything is drawn through the instanced renderers and only for chunks the game's own
/// culler already reported as visible, so cost tracks what is on screen rather than how
/// big the save is.
/// </summary>
public class EfficiencyOverlayRenderer : IDisposable
{
    /// How far the numbers float above the colour wash. Wash height, label limits and
    /// colours live in <see cref="OverlayTuning"/> so the debug console can adjust them
    /// without a rebuild - they are the parts that can only be judged in game.
    private const float LabelLift = 0.25f;

    /// Labels hold a roughly constant size on screen rather than in the world, so zooming
    /// in doesn't shove them in your face. Zoom is camera distance, so a label of world
    /// size s covers roughly s/zoom of the screen; past OverlayTuning.MachineLabelZoom a
    /// three digit number is under ten pixels a digit - noise, not data.
    private const float MachineLabelScreenShare = 0.03f;
    private const float MachineLabelMinWidth = 0.6f;

    /// FrameDrawOptions uses 999 to mean "no layer limit - show them all".
    private const int LayerUnrestricted = 900;

    private const float PlatformLabelScreenShare = 0.02f;
    /// A platform is 20 units across; a label much wider than this bleeds onto its neighbours.
    private const float PlatformLabelMaxWidth = 28f;
    private const float PlatformLabelMinWidth = 8f;

    private const float GlyphGap = 0.78f;
    private const float ChunkSize = 20f;

    /// The pip sits inside the tile so the wash colour still reads around it.
    private const float PipScale = 0.14f;
    private const float PipLift = 0.06f;

    private const int RunningShades = 14;

    private readonly EfficiencyTracker Tracker;

    private readonly TemporaryMeshReference[] RunningTints = new TemporaryMeshReference[RunningShades];
    private TemporaryMeshReference SaturationPip;
    private GlyphAtlas Glyphs;
    private MaterialPropertyBlock TintProperties;
    private MaterialPropertyBlock PipProperties;
    private MaterialPropertyBlock LabelProperties;

    /// Island chunk layouts are stable; recomputing them per frame is not worth it.
    private readonly Dictionary<IslandId, WorldCoordinate[]> IslandChunkCache =
        new Dictionary<IslandId, WorldCoordinate[]>();

    private IMapModel SubscribedMap;
    private int TuningVersion = -1;
    private int Frame;

    /// <summary>
    /// How strongly the overlay is showing, 0 to 1. Driven by
    /// <see cref="EfficiencyVisualization"/> so the overlay fades with the button, and by
    /// the fallback hotkey if the visualization could not be registered.
    /// </summary>
    public float Alpha;

    private const float LabelAlpha = 0.95f;

    public EfficiencyOverlayRenderer(EfficiencyTracker tracker)
    {
        Tracker = tracker;
    }

    public void Draw(MapDrawer drawer, FrameDrawOptionsNoLOD options)
    {
        IMapModel map = options?.Player?.CurrentMap;
        if (map == null)
        {
            return;
        }

        if (!OverlayTuning.TrackingEnabled)
        {
            if (Tracker.TrackedMap != null)
            {
                Tracker.Detach();
            }

            return;
        }

        WatchIslands(map);
        Tracker.Attach(map);
        Tracker.PumpRegistration();
        Tracker.KeepWarm();

        if (Alpha < 0.01f)
        {
            return;
        }

        Tracker.Update();

        MapCullResult cull = drawer?.LastCullResult;
        if (cull == null)
        {
            return;
        }

        EnsureResources();
        TintProperties.SetFloat(MaterialPropertyHelpers.SHADER_ID_Alpha, OverlayTuning.TintAlpha * Alpha);

        // Nearly opaque, or the wash underneath mixes into it and a green pip on a red
        // tile reads as yellow.
        PipProperties.SetFloat(MaterialPropertyHelpers.SHADER_ID_Alpha, 0.96f * Alpha);
        LabelProperties.SetFloat(MaterialPropertyHelpers.SHADER_ID_Alpha, LabelAlpha * Alpha);
        Frame++;

        // Space view is a mode the player is in, not a zoom threshold: while placing
        // platforms the per-machine detail is noise, so show the platform roll-up instead.
        bool spaceView = options.InOverviewMode
            || options.Player.InteractionState.BaseState == PlayerInteractionBaseState.Islands;

        if (spaceView)
        {
            DrawPlatforms(options, map, cull);
        }
        else
        {
            DrawMachines(options, cull);
        }
    }

    // ---------------------------------------------------------------- machines

    private void DrawMachines(FrameDrawOptionsNoLOD options, MapCullResult cull)
    {
        bool zoomedInEnough = OverlayTuning.ShowMachineLabels
            && options.Viewport.Zoom < OverlayTuning.MachineLabelZoom;
        float labelWidth = math.clamp(options.Viewport.Zoom * MachineLabelScreenShare,
            MachineLabelMinWidth, OverlayTuning.MachineLabelMaxWidth);

        // The numbers are drawn by the UI renderer, which pays no attention to depth, so a
        // label on a platform below would otherwise shine straight through the one above
        // it. Restricting them to the layer being viewed is what stops that. When the
        // player asks to see every layer at once there is no single layer to pick, so they
        // go back to being drawn everywhere.
        int topLayer = options.MaxBuildingIslandLayer;
        bool singleLayer = topLayer < LayerUnrestricted;

        for (int i = 0; i < cull.Chunks.Count; i++)
        {
            GlobalChunkCoordinate chunk = cull.Chunks[i].Chunk_G;
            if (!options.ShouldRenderPlatformContentsAtLayer(chunk.z))
            {
                continue;
            }

            if (!Tracker.TryGetEntries(chunk, out List<EfficiencyTracker.Entry> entries))
            {
                continue;
            }

            bool withLabels = zoomedInEnough && (!singleLayer || chunk.z == topLayer);

            for (int e = 0; e < entries.Count; e++)
            {
                EfficiencyTracker.Entry entry = entries[e];

                // A belt path spans many chunks - draw it once, on whichever comes up first.
                if (entry.LastLabelFrame == Frame)
                {
                    continue;
                }

                entry.LastLabelFrame = Frame;
                DrawEntry(options, entry, withLabels, labelWidth);
            }
        }
    }

    private void DrawEntry(FrameDrawOptionsNoLOD options, EfficiencyTracker.Entry entry,
        bool withLabel, float labelWidth)
    {
        IMeshReference tint = TintFor(entry);
        if (tint == null)
        {
            return;
        }

        IMaterialReference material = options.Theme.BaseResources.UXOverviewModeMapResourcePlaneMaterial;
        InstancedMeshManager renderer = options.Renderers.Misc;
        WorldCoordinate labelAt;

        bool pip = OverlayTuning.ShowSaturationPips && entry.IsSaturated;

        if (entry.Localized is ILocalizedTileSimulation tiles && tiles.NumOccupiedTiles > 0)
        {
            for (int i = 0; i < tiles.NumOccupiedTiles; i++)
            {
                GlobalTileCoordinate tile = tiles.GetOccupiedTile(i);
                renderer.AddWithProperties(tint, material,
                    FastMatrix.TranslateScale(tile.ToCenter_W(OverlayTuning.TintHeight), new float3(1f, 1f, 1f)),
                    TintProperties, PropertyBlockHash.Empty);

                if (pip)
                {
                    renderer.AddWithProperties(SaturationPip, material,
                        FastMatrix.TranslateScale(
                            tile.ToCenter_W(OverlayTuning.TintHeight + PipLift),
                            new float3(PipScale, 1f, PipScale)),
                        PipProperties, PropertyBlockHash.Empty);
                }
            }

            labelAt = tiles.GetOccupiedTile(0).ToCenter_W(OverlayTuning.TintHeight + LabelLift);
        }
        else
        {
            for (int i = 0; i < entry.Localized.NumOccupiedChunks; i++)
            {
                GlobalChunkCoordinate chunk = entry.Localized.GetOccupiedChunk(i);
                renderer.AddWithProperties(tint, material,
                    FastMatrix.TranslateScale(chunk.ToCenter_W(OverlayTuning.TintHeight),
                        new float3(ChunkSize, 1f, ChunkSize)),
                    TintProperties, PropertyBlockHash.Empty);

                if (pip)
                {
                    renderer.AddWithProperties(SaturationPip, material,
                        FastMatrix.TranslateScale(
                            chunk.ToCenter_W(OverlayTuning.TintHeight + PipLift),
                            new float3(ChunkSize * PipScale, 1f, ChunkSize * PipScale)),
                        PipProperties, PropertyBlockHash.Empty);
                }
            }

            labelAt = entry.Localized.GetOccupiedChunk(0).ToCenter_W(OverlayTuning.TintHeight + LabelLift);
        }

        if (!withLabel)
        {
            return;
        }

        // Digits only down here - one machine per tile leaves no room for a suffix.
        if (OverlayTuning.LabelAsPercent)
        {
            if (entry.Utilization > 0.005f)
            {
                DrawRate(options, labelAt, entry.Utilization * 100f, labelWidth, withSuffix: false);
            }
        }
        else if (entry.ItemsPerMinute >= 0.5f)
        {
            DrawRate(options, labelAt, entry.ItemsPerMinute, labelWidth, withSuffix: false);
        }
    }

    // --------------------------------------------------------------- platforms

    private void DrawPlatforms(FrameDrawOptionsNoLOD options, IMapModel map, MapCullResult cull)
    {
        IMaterialReference material = options.Theme.BaseResources.UXOverviewModeMapResourcePlaneMaterial;
        InstancedMeshManager renderer = options.Renderers.Misc;
        float labelWidth = math.clamp(options.Viewport.Zoom * PlatformLabelScreenShare,
            PlatformLabelMinWidth, PlatformLabelMaxWidth);

        for (int i = 0; i < cull.Islands.Count; i++)
        {
            IslandId islandId = cull.Islands[i].IslandId;
            if (!Tracker.TryGetSummary(islandId, out EfficiencyTracker.IslandSummary summary)
                || summary.MachineCount == 0)
            {
                continue;
            }

            WorldCoordinate[] chunks = GetIslandChunks(map, islandId);
            if (chunks.Length == 0)
            {
                continue;
            }

            IMeshReference tint = TintForPlatform(summary);
            float3 centre = float3.zero;

            for (int c = 0; c < chunks.Length; c++)
            {
                renderer.AddWithProperties(tint, material,
                    FastMatrix.TranslateScale(chunks[c], new float3(ChunkSize, 1f, ChunkSize)),
                    TintProperties, PropertyBlockHash.Empty);
                centre += (float3)chunks[c];
            }

            centre /= chunks.Length;

            if (OverlayTuning.ShowSaturationPips && summary.IsSaturated)
            {
                renderer.AddWithProperties(SaturationPip, material,
                    FastMatrix.TranslateScale((WorldCoordinate)centre + new WorldVector(0f, 0f, PipLift),
                        new float3(ChunkSize * 0.25f, 1f, ChunkSize * 0.25f)),
                    PipProperties, PropertyBlockHash.Empty);
            }

            if (OverlayTuning.ShowPlatformLabels && summary.HeadlineItemsPerMinute >= 0.5f)
            {
                DrawRate(options, centre, summary.HeadlineItemsPerMinute, labelWidth, withSuffix: true);
            }
        }
    }

    private WorldCoordinate[] GetIslandChunks(IMapModel map, IslandId islandId)
    {
        if (IslandChunkCache.TryGetValue(islandId, out WorldCoordinate[] cached))
        {
            return cached;
        }

        if (!map.TryGetIsland(islandId, out IslandModel island))
        {
            // Don't cache a miss - the island may just not be resolvable yet this frame.
            return Array.Empty<WorldCoordinate>();
        }

        List<WorldCoordinate> positions = new List<WorldCoordinate>(8);
        foreach (GlobalChunkCoordinate chunk in island.Chunks)
        {
            positions.Add(chunk.ToCenter_W(OverlayTuning.TintHeight));
        }

        cached = positions.ToArray();
        IslandChunkCache.Add(islandId, cached);
        return cached;
    }

    /// Cached chunk layouts have to go when the platform they describe does.
    private void WatchIslands(IMapModel map)
    {
        if (ReferenceEquals(SubscribedMap, map))
        {
            return;
        }

        SubscribedMap?.OnBeforeIslandRemoved.TryUnregister(OnIslandRemoved);
        IslandChunkCache.Clear();

        SubscribedMap = map;
        SubscribedMap.OnBeforeIslandRemoved.Register(OnIslandRemoved);
    }

    private void OnIslandRemoved(IslandModel island)
    {
        IslandChunkCache.Remove(island.Id);
    }

    // ------------------------------------------------------------------ labels

    /// <summary>
    /// Draws a number flat on the ground, centred on <paramref name="at"/> and sized to fit
    /// inside <paramref name="width"/> - so a four digit number shrinks rather than sprawling
    /// across its neighbours. The character atlas has no percent sign, so a percentage is
    /// drawn as bare digits; in an efficiency overlay that reads clearly enough.
    /// </summary>
    private void DrawRate(FrameDrawOptionsNoLOD options, WorldCoordinate at, float amount,
        float width, bool withSuffix)
    {
        int value = (int)math.round(amount);
        int digits = GlyphAtlas.DigitCount(value);
        int glyphs = withSuffix ? digits + 2 : digits;
        float size = width / (glyphs * GlyphGap);

        IMaterialReference material = options.Theme.BaseResources.UXSuperChunkCoordinatesRendererMaterial;
        InstancedMeshManager renderer = options.Renderers.UI;

        float step = size * GlyphGap;
        float start = -(glyphs - 1) * 0.5f * step;
        float3 scale = new float3(size, 1f, size);

        for (int i = 0; i < glyphs; i++)
        {
            int glyph;
            if (i < digits)
            {
                glyph = GlyphAtlas.DigitAt(value, i, digits);
            }
            else if (i == digits)
            {
                glyph = GlyphAtlas.GlyphSlash;
            }
            else
            {
                glyph = GlyphAtlas.GlyphM;
            }

            WorldCoordinate position = at + new WorldVector(start + i * step, 0f, 0f);
            renderer.AddWithProperties(Glyphs.Get(glyph), material,
                FastMatrix.TranslateScale(position, scale),
                LabelProperties, PropertyBlockHash.Empty);
        }
    }

    // ------------------------------------------------------------------ colours

    /// <summary>
    /// One colour scale for everything: how much of what it could move is it moving. Why
    /// it is slow is the pip's job, not the colour's - a dark tile with a pip is fed and
    /// still failing to keep up, a dark tile without one is waiting on something upstream.
    /// </summary>
    private IMeshReference TintFor(EfficiencyTracker.Entry entry)
    {
        if (entry.Status == EfficiencyStatus.Unknown)
        {
            return null;
        }

        return RunningTints[ShadeIndex(entry.Utilization)];
    }

    private IMeshReference TintForPlatform(EfficiencyTracker.IslandSummary summary)
    {
        return RunningTints[ShadeIndex(summary.Utilization)];
    }

    /// <summary>
    /// Utilization to colour. Runs dark red through red, orange and yellow to green, so
    /// how bad something is reads off the colour directly: a belt limping along at 1% is
    /// nearly as dark as one that has stopped, not a comfortable yellow.
    /// </summary>
    private static Color Gradient(float t)
    {
        float floor = OverlayTuning.BlockedShade;

        Color[] stops =
        {
            new Color(floor, floor * 0.07f, floor * 0.05f), // 0% - stopped, or as good as
            new Color(0.74f, 0.10f, 0.04f), // 25%  - deep red
            new Color(0.92f, 0.55f, 0.05f), // 50%  - orange
            new Color(0.55f, 0.80f, 0.20f), // 75%  - green enough to read as flowing
            new Color(0.20f, 0.90f, 0.34f)  // 100% - full green
        };

        float scaled = math.clamp(t, 0f, 1f) * (stops.Length - 1);
        int low = (int)scaled;
        if (low >= stops.Length - 1)
        {
            return stops[stops.Length - 1];
        }

        return Color.Lerp(stops[low], stops[low + 1], scaled - low);
    }

    private static int ShadeIndex(float utilization)
    {
        int index = (int)(utilization * RunningShades);
        return math.clamp(index, 0, RunningShades - 1);
    }

    private void EnsureResources()
    {
        if (Glyphs != null && TuningVersion == OverlayTuning.Version)
        {
            return;
        }

        // A console tweak to a colour or a height invalidates the baked meshes and any
        // island position we cached from them.
        if (Glyphs != null)
        {
            DisposeResources();
        }

        TuningVersion = OverlayTuning.Version;
        IslandChunkCache.Clear();
        Glyphs = new GlyphAtlas();

        TintProperties = new MaterialPropertyBlock();
        PipProperties = new MaterialPropertyBlock();
        LabelProperties = new MaterialPropertyBlock();

        // Green reads as "there is stuff here" against the severity colour underneath.
        SaturationPip = GeometryHelpers.GeneratePlaneMeshUVColoredUncached(new Color(0.18f, 1f, 0.35f));

        for (int i = 0; i < RunningShades; i++)
        {
            float t = RunningShades == 1 ? 1f : (float)i / (RunningShades - 1);
            RunningTints[i] = GeometryHelpers.GeneratePlaneMeshUVColoredUncached(Gradient(t));
        }
    }

    public void Dispose()
    {
        SubscribedMap?.OnBeforeIslandRemoved.TryUnregister(OnIslandRemoved);
        SubscribedMap = null;

        DisposeResources();
        IslandChunkCache.Clear();
    }

    private void DisposeResources()
    {
        Glyphs?.Dispose();
        Glyphs = null;

        SaturationPip?.Dispose();
        SaturationPip = null;

        for (int i = 0; i < RunningTints.Length; i++)
        {
            RunningTints[i]?.Dispose();
            RunningTints[i] = null;
        }
    }
}
