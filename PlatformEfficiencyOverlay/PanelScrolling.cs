using System.Text;
using UnityEngine;
using UnityEngine.UI;
using ILogger = Core.Logging.ILogger;

/// <summary>
/// Keeps a side panel on screen when it has more in it than the screen is tall.
///
/// The panel stacks its modules and grows to fit them, with nothing to stop it running off
/// the bottom - normally there are few enough modules that it never comes up, and adding a
/// chart is exactly the sort of thing that tips a long panel over the edge.
///
/// So the content gets a viewport: a masked, height-capped parent with a ScrollRect, built
/// in code and slid in between the panel and its content container. The panel is none the
/// wiser - it goes on adding modules to the same container, which is now the scroll
/// content instead of a direct child.
///
/// Installed from the graph module rather than by hooking the panel, because the module is
/// already inside the container and can simply walk up to it. That also scopes it to the
/// panels this mod actually lengthens.
/// </summary>
internal static class PanelScrolling
{
    /// Marks a viewport as ours, so a rebuilt panel is recognised rather than wrapped twice.
    private const string ViewportName = "PlatformEfficiencyOverlay.ScrollViewport";

    /// <summary>
    /// Caps <paramref name="container"/> and makes it scroll if it is taller than the cap.
    /// Safe to call every time a panel is built; the rig is created once per panel.
    /// </summary>
    public static void Apply(RectTransform container, ILogger logger)
    {
        if (!OverlayTuning.PanelScrolling || container == null || container.parent == null)
        {
            return;
        }

        RectTransform viewport = container.parent as RectTransform;

        if (viewport == null)
        {
            return;
        }

        if (viewport.name != ViewportName)
        {
            viewport = Install(container, logger);

            if (viewport == null)
            {
                return;
            }
        }

        Fit(viewport, container);
    }

    /// <summary>
    /// Sizes the viewport to the content, up to the cap. Done every frame the panel is
    /// open because the content's own height is not final until the layout has run, and
    /// modules resize themselves as their numbers change.
    /// </summary>
    private static void Fit(RectTransform viewport, RectTransform container)
    {
        float content = container.rect.height;
        float cap = MaxHeight(viewport);
        float height = content > cap ? cap : content;

        if (height <= 0f)
        {
            return;
        }

        if (!Mathf.Approximately(viewport.sizeDelta.y, height))
        {
            viewport.sizeDelta = new Vector2(viewport.sizeDelta.x, height);
        }

        LayoutElement element = viewport.GetComponent<LayoutElement>();

        if (element != null && !Mathf.Approximately(element.preferredHeight, height))
        {
            element.minHeight = height;
            element.preferredHeight = height;
        }
    }

    /// <summary>
    /// How tall the content is allowed to get, in the canvas's own units so it holds at
    /// any resolution or UI scale.
    /// </summary>
    private static float MaxHeight(RectTransform viewport)
    {
        Canvas canvas = viewport.GetComponentInParent<Canvas>();

        if (canvas == null)
        {
            return 600f;
        }

        RectTransform root = canvas.rootCanvas != null
            ? canvas.rootCanvas.transform as RectTransform
            : canvas.transform as RectTransform;

        float available = root != null ? root.rect.height : 900f;

        return available * OverlayTuning.PanelHeightFraction;
    }

    private static RectTransform Install(RectTransform container, ILogger logger)
    {
        try
        {
            Transform parent = container.parent;
            int index = container.GetSiblingIndex();

            GameObject created = new GameObject(ViewportName, typeof(RectTransform));
            RectTransform viewport = (RectTransform)created.transform;

            viewport.SetParent(parent, worldPositionStays: false);
            viewport.SetSiblingIndex(index);

            // Stand exactly where the container stood, so however the panel positions its
            // content - anchors or a layout group - it positions the viewport the same way.
            viewport.anchorMin = container.anchorMin;
            viewport.anchorMax = container.anchorMax;
            viewport.pivot = container.pivot;
            viewport.anchoredPosition = container.anchoredPosition;
            viewport.sizeDelta = container.sizeDelta;
            viewport.localScale = container.localScale;

            container.SetParent(viewport, worldPositionStays: false);

            // Scroll content hangs from the top edge and grows downwards.
            container.anchorMin = new Vector2(0f, 1f);
            container.anchorMax = new Vector2(1f, 1f);
            container.pivot = new Vector2(0.5f, 1f);
            container.anchoredPosition = Vector2.zero;
            container.offsetMin = new Vector2(0f, container.offsetMin.y);
            container.offsetMax = new Vector2(0f, container.offsetMax.y);

            // Without this the content has no height of its own and there is nothing to
            // scroll. Harmless when the panel already fits: the fitter reports the same
            // height the layout would have given it.
            ContentSizeFitter fitter = container.GetComponent<ContentSizeFitter>();

            if (fitter == null)
            {
                fitter = container.gameObject.AddComponent<ContentSizeFitter>();
            }

            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            created.AddComponent<RectMask2D>();

            ScrollRect scroll = created.AddComponent<ScrollRect>();
            scroll.content = container;
            scroll.viewport = viewport;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;

            // Inertia in a settings-style list feels like a bug rather than a flourish.
            scroll.inertia = false;

            LayoutElement element = created.AddComponent<LayoutElement>();
            element.flexibleWidth = 1f;

            logger?.Info?.Log("Side panel content is now scrollable when it overflows.");

            return viewport;
        }
        catch (System.Exception exception)
        {
            // A panel that runs off the bottom of the screen is still better than no panel.
            logger?.Exception?.LogException(exception);
            return null;
        }
    }

    /// <summary>
    /// The layout of a live panel, from a module up to the canvas: what each level is, how
    /// tall it is, and which layout components drive it. For working out why a panel is
    /// sized the way it is without being able to open the prefab.
    /// </summary>
    public static string Describe(Transform from)
    {
        if (from == null)
        {
            return "Nothing to describe - select a machine or platform first.";
        }

        StringBuilder text = new StringBuilder();
        Transform current = from;
        int depth = 0;

        while (current != null && depth < 12)
        {
            RectTransform rect = current as RectTransform;

            text.Append(depth == 0 ? "" : "\n").Append(new string(' ', depth * 2))
                .Append(current.name);

            if (rect != null)
            {
                text.Append(" [").Append((int)rect.rect.width).Append('x')
                    .Append((int)rect.rect.height).Append(']');
            }

            foreach (Component component in current.GetComponents<Component>())
            {
                if (component is LayoutGroup || component is ContentSizeFitter
                    || component is LayoutElement || component is ScrollRect
                    || component is RectMask2D || component is Mask)
                {
                    text.Append(' ').Append(component.GetType().Name);
                }
            }

            current = current.parent;
            depth++;
        }

        return text.ToString();
    }
}
