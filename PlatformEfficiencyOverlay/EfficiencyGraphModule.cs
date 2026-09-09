using System;
using Core.Localization;
using TMPro;
using Unity.Core.View;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A side panel module drawing capacity used over time as a bar chart, in the style of the
/// game's own statistics charts: one bar per bucket, oldest at the left, older bars faded,
/// each one hoverable for the exact figure.
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
        /// Null for a readout with no history behind it.
        public readonly Func<float[], int> Read;

        /// The rate against the ceiling, for one corner.
        public readonly Func<string> Caption;

        /// Capacity used right now, for the other corner.
        public readonly Func<float> Current;

        public Data(Func<float[], int> read, Func<string> caption, Func<float> current)
        {
            Read = read;
            Caption = caption;
            Current = current;
        }

        /// <summary>
        /// A rate and a percentage with no chart under them, for the things that are not
        /// worth a history: a space belt or pipe is one flow of many parallel lanes, and
        /// what anyone wants from it is whether it is full right now.
        /// </summary>
        public static Data Readout(Func<string> caption, Func<float> current)
        {
            return new Data(null, caption, current);
        }

        public bool HasHistory => Read != null;

        public PrefabViewReference<HUDSidePanelModule> GetViewPrefabReference()
        {
            return HasHistory ? EfficiencyGraphTemplate.Reference : EfficiencyGraphTemplate.ReadoutReference;
        }
    }

    /// <summary>The most recently built chart, for the panel diagnostics command.</summary>
    public static EfficiencyGraphModule Latest { get; private set; }

    /// Public so Unity serialises them, which is what makes Instantiate remap these to the
    /// copies instead of leaving them pointing at the template's own children.
    public RawImage[] UIBars = Array.Empty<RawImage>();
    public HUDTooltipTarget[] UITips = Array.Empty<HUDTooltipTarget>();
    public TextMeshProUGUI UISummary;
    public TextMeshProUGUI UICaption;

    private Func<float[], int> Source;
    private Func<string> Caption;
    private Func<float> Current;
    private readonly float[] Series = new float[MachineHistory.Buckets + 1];
    private float NextRefresh;
    private bool FontResolved;

    public override void OnDispose()
    {
        if (ReferenceEquals(Latest, this))
        {
            Latest = null;
        }
    }

    public override void InitFromData(IHUDSidePanelModuleData rawData)
    {
        if (!(rawData is Data data))
        {
            throw new Exception("Invalid data");
        }

        Source = data.Read;
        Caption = data.Caption;
        Current = data.Current;
        NextRefresh = 0f;
        Latest = this;

        Refresh();
    }

    public override void OnUpdate(InputDownstreamContext context)
    {
        base.OnUpdate(context);

        // The panel grows to fit its modules with nothing stopping it running off the
        // bottom of the screen, and a chart is exactly what tips a long one over. Checked
        // every frame rather than once, because the content's height is not final until
        // the layout has run and modules resize as their numbers change.
        PanelScrolling.Apply(transform.parent as RectTransform, Logger);

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

        int range = OverlayTuning.HistoryRange;
        int bucketSeconds = MachineHistory.BucketSeconds[range];
        float total = 0f;
        float peak = 0f;

        for (int i = 0; i < count; i++)
        {
            total += Series[i];

            if (Series[i] > peak)
            {
                peak = Series[i];
            }
        }

        DrawBars(count, bucketSeconds);

        if (Source == null)
        {
            // No history behind this one, so the corner shows the moment instead of an
            // average over a window that was never recorded.
            DrawReadout();
            return;
        }

        DrawSummary(count > 0 ? total / count : 0f, peak, count > 0);
    }

    private void DrawBars(int count, int bucketSeconds)
    {
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

            if (!recorded)
            {
                // Nothing was recorded that long ago. Leave the slot empty rather than
                // drawing a zero bar, so "no data yet" does not read as "stopped".
                bar.transform.localScale = new Vector3(1f, 0f, 1f);
                bar.raycastTarget = false;
                SetTip(i, null, null);
                continue;
            }

            float fraction = Series[sample];
            float height = fraction > 1f ? 1f : fraction;

            // A floor, so a stopped machine is still a visible line rather than a gap -
            // the same thing the game's charts do for an empty bucket.
            float scale = height < 0.02f ? 0.02f : height;

            bar.transform.localScale = new Vector3(1f, scale, 1f);

            Color colour = EfficiencyOverlayRenderer.Gradient(fraction);
            colour.a = EfficiencyGraphTemplate.BarAlpha(i, UIBars.Length);
            bar.color = colour;

            // The bar is squashed by its own scale, so its hit area is squashed too. This
            // stretches it back over the full column height, which is what makes a bar at
            // 3% hoverable at all - the same correction the game's own chart applies.
            bar.raycastTarget = true;
            bar.raycastPadding = new Vector4(
                -1f, -4f, -1f, -4f - (1f - scale) * EfficiencyGraphTemplate.ChartHeight / scale);

            SetTip(i, Percent(fraction), Ago(count - 1 - sample, bucketSeconds));
        }
    }

    private void SetTip(int index, string title, string description)
    {
        if (index >= UITips.Length)
        {
            return;
        }

        HUDTooltipTarget tip = UITips[index];

        if (tip == null)
        {
            return;
        }

        if (title == null)
        {
            tip.enabled = false;
            return;
        }

        tip.enabled = true;
        tip.Title = new RawText(title);
        tip.Description = description == null ? null : (IText)new RawText(description);
    }

    /// <summary>
    /// The average across the window, in the corner of the chart - directly under the
    /// range selector, which is the one number the chart itself cannot show.
    /// </summary>
    private void DrawReadout()
    {
        float current = 0f;

        try
        {
            current = Current != null ? Current() : 0f;
        }
        catch (Exception exception)
        {
            Logger?.Exception?.LogException(exception);
        }

        ResolveFont();

        if (UISummary != null)
        {
            UISummary.text = "now " + Percent(current);
        }

        DrawCaption();
    }

    private void DrawSummary(float average, float peak, bool any)
    {
        ResolveFont();

        if (UISummary != null)
        {
            UISummary.text = any
                ? "avg " + Percent(average) + "   peak " + Percent(peak)
                : "recording...";
        }

        DrawCaption();
    }

    /// <summary>
    /// Borrowed from the panel around us: a mod has no font asset of its own, and a
    /// TextMeshPro label with no font draws nothing at all.
    /// </summary>
    private void ResolveFont()
    {
        if (FontResolved)
        {
            return;
        }

        FontResolved = true;

        TMP_FontAsset font = EfficiencyGraphTemplate.FindFont(transform);

        if (font == null)
        {
            return;
        }

        if (UISummary != null)
        {
            UISummary.font = font;
        }

        if (UICaption != null)
        {
            UICaption.font = font;
        }
    }

    private void DrawCaption()
    {
        if (UICaption == null)
        {
            return;
        }

        string caption = null;

        try
        {
            caption = Caption != null ? Caption() : null;
        }
        catch (Exception exception)
        {
            Logger?.Exception?.LogException(exception);
        }

        UICaption.text = caption ?? string.Empty;
    }

    private static string Percent(float fraction)
    {
        return (int)(fraction * 100f + 0.5f) + "%";
    }

    /// <summary>How long ago a bucket was, phrased for a tooltip.</summary>
    private static string Ago(int bucketsBack, int bucketSeconds)
    {
        if (bucketsBack <= 0)
        {
            return "now";
        }

        int seconds = bucketsBack * bucketSeconds;

        if (seconds < 60)
        {
            return seconds + "s ago";
        }

        if (seconds < 3600)
        {
            return seconds / 60 + "m ago";
        }

        return (seconds / 360) / 10f + "h ago";
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

    /// <summary>Chart height in canvas units. Also the reach of a bar's hover area.</summary>
    public const float ChartHeight = 74f;

    private static GameObject Holder;
    private static EfficiencyGraphModule Prefab;
    private static EfficiencyGraphModule ReadoutPrefab;

    public static PrefabViewReference<HUDSidePanelModule> Reference =>
        new PrefabViewReference<HUDSidePanelModule>(Ensure(BarCount, ChartHeight, ref Prefab));

    public static PrefabViewReference<HUDSidePanelModule> ReadoutReference =>
        new PrefabViewReference<HUDSidePanelModule>(Ensure(0, 20f, ref ReadoutPrefab));

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
    /// Borrows a font from whatever text is already on screen around us, so the summary
    /// matches the panel it sits in. A mod has no font asset of its own and no way to
    /// author one, and a TextMeshPro label without a font draws nothing at all.
    /// </summary>
    public static TMP_FontAsset FindFont(Transform from)
    {
        Transform current = from;

        while (current != null)
        {
            foreach (TMP_Text text in current.GetComponentsInChildren<TMP_Text>(true))
            {
                if (text.font != null)
                {
                    return text.font;
                }
            }

            current = current.parent;
        }

        return null;
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
        ReadoutPrefab = null;
    }

    private static EfficiencyGraphModule Ensure(int bars, float height, ref EfficiencyGraphModule cached)
    {
        if (cached != null)
        {
            return cached;
        }

        if (Holder == null)
        {
            Holder = new GameObject("PlatformEfficiencyOverlay.Templates");
            Holder.SetActive(false);
            UnityEngine.Object.DontDestroyOnLoad(Holder);
        }

        GameObject module = new GameObject("EfficiencyGraph", typeof(RectTransform));
        module.transform.SetParent(Holder.transform, worldPositionStays: false);

        RectTransform rect = (RectTransform)module.transform;
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.sizeDelta = new Vector2(0f, height);

        // The panel stacks its modules with a layout group, which sizes them from this.
        LayoutElement layout = module.AddComponent<LayoutElement>();
        layout.minHeight = height;
        layout.preferredHeight = height;
        layout.flexibleWidth = 1f;

        EfficiencyGraphModule graph = module.AddComponent<EfficiencyGraphModule>();
        RawImage[] barImages = new RawImage[bars];
        HUDTooltipTarget[] tips = new HUDTooltipTarget[bars];

        for (int i = 0; i < bars; i++)
        {
            GameObject bar = new GameObject("Bar" + i, typeof(RectTransform));
            bar.transform.SetParent(module.transform, worldPositionStays: false);

            RectTransform barRect = (RectTransform)bar.transform;
            float step = 1f / bars;

            // Anchored across a slice of the width so the chart follows the panel, and
            // pinned to the bottom so scaling a bar grows it upwards.
            barRect.anchorMin = new Vector2(i * step, 0f);
            barRect.anchorMax = new Vector2((i + 1) * step, 0f);
            barRect.pivot = new Vector2(0.5f, 0f);
            barRect.offsetMin = new Vector2(0.5f, 0f);
            barRect.offsetMax = new Vector2(-0.5f, height);

            // A RawImage with no texture draws a plain white quad, which is all a bar is.
            // Image would need a sprite asset, and a mod has no way to author one.
            RawImage image = bar.AddComponent<RawImage>();
            image.color = Color.clear;

            HUDTooltipTarget tip = bar.AddComponent<HUDTooltipTarget>();
            tip.Alignment = HUDTooltip.TooltipAlignment.Bottom_Center;
            tip.TooltipDistance = 70f;

            bar.transform.localScale = new Vector3(1f, 0f, 1f);
            barImages[i] = image;
            tips[i] = tip;
        }

        graph.UIBars = barImages;
        graph.UITips = tips;

        // Both labels are added last so they draw over the bars rather than behind them.
        graph.UICaption = BuildLabel(module.transform, "Caption", TextAlignmentOptions.TopLeft);
        graph.UISummary = BuildLabel(module.transform, "Summary", TextAlignmentOptions.TopRight);

        cached = graph;

        return cached;
    }

    private static TextMeshProUGUI BuildLabel(Transform parent, string name, TextAlignmentOptions alignment)
    {
        GameObject label = new GameObject(name, typeof(RectTransform));
        label.transform.SetParent(parent, worldPositionStays: false);

        RectTransform rect = (RectTransform)label.transform;
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.offsetMin = new Vector2(4f, -18f);
        rect.offsetMax = new Vector2(-4f, 0f);

        TextMeshProUGUI text = label.AddComponent<TextMeshProUGUI>();
        text.alignment = alignment;
        text.fontSize = 13f;
        text.color = new Color(1f, 1f, 1f, 0.75f);
        text.raycastTarget = false;

        return text;
    }
}
