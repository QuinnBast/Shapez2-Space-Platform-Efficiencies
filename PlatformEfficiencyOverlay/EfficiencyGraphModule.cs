using System;
using Unity.Core.View;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A side panel module drawing capacity used over time as a bar chart, in the style of the
/// game's own statistics charts: one bar per bucket, oldest at the left, older bars faded.
///
/// The game's chart widget cannot be borrowed - it lives inside the statistics screen's
/// prefab, and a side panel module has to *be* a prefab of its own. So this builds one at
/// runtime: a PrefabViewReference takes any component as its prefab, and the view provider
/// instantiates it with plain Object.Instantiate, which remaps the bar references exactly
/// as it would for an authored prefab.
///
/// Bar height is capacity used and bar colour is the overlay's own gradient, so a machine
/// that reads amber on the map reads amber here. The chart and the wash cannot disagree,
/// because they are the same number through the same gradient.
/// </summary>
public class EfficiencyGraphModule : HUDSidePanelModule
{
    /// <summary>
    /// What to draw. A delegate rather than a snapshot because the module refreshes itself:
    /// the side panel is not rebuilt while it is open, so a snapshot would freeze at the
    /// moment the machine was selected.
    /// </summary>
    public class Data : IHUDSidePanelModuleData
    {
        /// Fills the buffer with fractions of capacity, oldest first, returning how many.
        public readonly Func<float[], int> Read;

        public Data(Func<float[], int> read)
        {
            Read = read;
        }

        public PrefabViewReference<HUDSidePanelModule> GetViewPrefabReference()
        {
            return EfficiencyGraphTemplate.Reference;
        }
    }

    /// Public so Unity serialises it, which is what makes Instantiate remap these to the
    /// copies instead of leaving them pointing at the template's own bars.
    public RawImage[] UIBars = Array.Empty<RawImage>();

    private Func<float[], int> Source;
    private readonly float[] Series = new float[MachineHistory.Buckets + 1];
    private float NextRefresh;

    public override void OnDispose()
    {
    }

    public override void InitFromData(IHUDSidePanelModuleData rawData)
    {
        if (!(rawData is Data data))
        {
            throw new Exception("Invalid data");
        }

        Source = data.Read;
        NextRefresh = 0f;
        Refresh();
    }

    public override void OnUpdate(InputDownstreamContext context)
    {
        base.OnUpdate(context);

        // Four times a second. The finest range has one-second buckets, so anything faster
        // just redraws the same bars.
        if (Time.unscaledTime < NextRefresh)
        {
            return;
        }

        NextRefresh = Time.unscaledTime + 0.25f;
        Refresh();
    }

    private void Refresh()
    {
        int count = 0;

        try
        {
            count = Source != null ? Source(Series) : 0;
        }
        catch (Exception exception)
        {
            // Drawing a stale chart beats taking the side panel down with us.
            Logger?.Exception?.LogException(exception);
        }

        for (int i = 0; i < UIBars.Length; i++)
        {
            RawImage bar = UIBars[i];
            if (bar == null)
            {
                continue;
            }

            // Newest at the right, so a short history fills from the right and the live
            // tip is always in the same place.
            int sample = count - UIBars.Length + i;
            bool recorded = sample >= 0 && sample < count;
            float fraction = recorded ? Series[sample] : 0f;

            if (!recorded)
            {
                // Nothing was recorded that long ago. Leave the slot empty rather than
                // drawing a zero bar, so "no data yet" does not read as "stopped".
                bar.transform.localScale = new Vector3(1f, 0f, 1f);
                continue;
            }

            float height = fraction > 1f ? 1f : fraction;

            // A floor, so a stopped machine is still a visible line rather than a gap -
            // the same thing the game's charts do for an empty bucket.
            bar.transform.localScale = new Vector3(1f, height < 0.02f ? 0.02f : height, 1f);

            Color colour = EfficiencyOverlayRenderer.Gradient(fraction);
            colour.a = EfficiencyGraphTemplate.BarAlpha(i, UIBars.Length);
            bar.color = colour;
        }
    }
}

/// <summary>
/// Builds the graph's prefab once, in code.
///
/// It hangs off a single inactive holder that is never destroyed. The template itself has
/// to stay active, because Object.Instantiate copies activeSelf and an inactive template
/// would produce invisible modules - but it must not be part of a live hierarchy either,
/// which is what the inactive holder is for.
/// </summary>
internal static class EfficiencyGraphTemplate
{
    private const int BarCount = MachineHistory.Buckets;
    private const float ChartHeight = 74f;

    private static GameObject Holder;
    private static EfficiencyGraphModule Prefab;

    public static PrefabViewReference<HUDSidePanelModule> Reference =>
        new PrefabViewReference<HUDSidePanelModule>(Ensure());

    /// <summary>
    /// The fade on the i-th bar, oldest first - lifted from the game's statistics chart so
    /// the two read as the same widget.
    /// </summary>
    public static float BarAlpha(int index, int count)
    {
        float t = count <= 1 ? 1f : 0.1f + 0.9f * index / count;
        return Mathf.Pow(t, 2.2f);
    }

    /// <summary>
    /// Drops the template. Matters on a hot reload: the prefab is a component from this
    /// assembly, and leaving it behind would have the next generation instantiating the
    /// old one.
    /// </summary>
    public static void Release()
    {
        if (Holder != null)
        {
            UnityEngine.Object.Destroy(Holder);
        }

        Holder = null;
        Prefab = null;
    }

    private static EfficiencyGraphModule Ensure()
    {
        if (Prefab != null)
        {
            return Prefab;
        }

        Holder = new GameObject("PlatformEfficiencyOverlay.Templates");
        Holder.SetActive(false);
        UnityEngine.Object.DontDestroyOnLoad(Holder);

        GameObject module = new GameObject("EfficiencyGraph", typeof(RectTransform));
        module.transform.SetParent(Holder.transform, worldPositionStays: false);

        RectTransform rect = (RectTransform)module.transform;
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.sizeDelta = new Vector2(0f, ChartHeight);

        // The panel stacks its modules with a layout group, which sizes them from this.
        LayoutElement layout = module.AddComponent<LayoutElement>();
        layout.minHeight = ChartHeight;
        layout.preferredHeight = ChartHeight;
        layout.flexibleWidth = 1f;

        EfficiencyGraphModule graph = module.AddComponent<EfficiencyGraphModule>();
        RawImage[] bars = new RawImage[BarCount];

        for (int i = 0; i < BarCount; i++)
        {
            GameObject bar = new GameObject("Bar" + i, typeof(RectTransform));
            bar.transform.SetParent(module.transform, worldPositionStays: false);

            RectTransform barRect = (RectTransform)bar.transform;
            float step = 1f / BarCount;

            // Anchored across a slice of the width so the chart follows the panel, and
            // pinned to the bottom so scaling a bar grows it upwards.
            barRect.anchorMin = new Vector2(i * step, 0f);
            barRect.anchorMax = new Vector2((i + 1) * step, 0f);
            barRect.pivot = new Vector2(0.5f, 0f);
            barRect.offsetMin = new Vector2(0.5f, 0f);
            barRect.offsetMax = new Vector2(-0.5f, ChartHeight);

            // A RawImage with no texture draws a plain white quad, which is all a bar is.
            // Image would need a sprite asset, and a mod has no way to author one.
            RawImage image = bar.AddComponent<RawImage>();
            image.raycastTarget = false;
            image.color = Color.clear;

            bar.transform.localScale = new Vector3(1f, 0f, 1f);
            bars[i] = image;
        }

        graph.UIBars = bars;
        Prefab = graph;

        return Prefab;
    }
}
