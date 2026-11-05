using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Toasts: short replies to something the player just did in a menu — "COPIED", "CLEAR A SECTOR",
/// a diagnostic's result. Not to be confused with the banner, which announces things that happen
/// in the game (a new sector, a bonus echo) and sits mid-screen over the maze.
///
/// Toasts used to be banners, and that is what made them misbehave. The banner draws mid-screen
/// on the main canvas, so a toast fired from Settings printed straight across the Settings rows,
/// then outlived the panel and sat over DESCENT and LEVELS on the menu; on the Dive Over card
/// "COPIED" covered SHARE and DIVE AGAIN. Now:
///   * they live on their own canvas, above every panel, so they are never drawn under one;
///   * at a fixed spot in the bottom third, above the in-game buttons and below every panel's
///     lowest button — the layout is measured in GameConfig-free reference units, see ToastY;
///   * one at a time — a new toast replaces the old;
///   * gone after about two seconds, and gone at once when the screen or panel changes.
/// </summary>
public partial class UIManager
{
    private Canvas _toastCanvas;
    private CanvasScaler _toastScaler;
    private RectTransform _toastRT;
    private Image _toastBg, _toastFrame;
    private TMP_Text _toastText;
    private CanvasGroup _toastGroup;
    private float _toastT = -1f, _toastHold;

    /// <summary>
    /// Centre of the toast above the safe area's bottom edge, in reference units. Measured gaps at
    /// 1080x1920: the in-game gear/RETRY row tops out at 136 and the toast's bottom edge is at 140;
    /// the lowest panel edges are the Settings card (252 above the bottom with all seven rows), the
    /// result card's MENU button (365) and the menu gear (387) — all above the toast's top at 240.
    /// </summary>
    private const float ToastY = 190f;
    private const float ToastMaxW = 940f;
    private const float ToastFadeIn = 0.15f, ToastFadeOut = 0.35f;
    public const float ToastDefaultHold = 1.6f;

    private void BuildToast()
    {
        var go = new GameObject("Toast Canvas");
        go.transform.SetParent(transform, false);
        _toastCanvas = go.AddComponent<Canvas>();
        _toastCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _toastCanvas.sortingOrder = 200;               // above the HUD canvas (100) and all of its panels
        _toastScaler = go.AddComponent<CanvasScaler>();
        _toastScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        _toastScaler.referenceResolution = new Vector2(1080f, 1920f);
        _toastScaler.matchWidthOrHeight = _scaler != null ? _scaler.matchWidthOrHeight : 0.5f;
        // No GraphicRaycaster: a toast is never in the way of a tap.

        var safeGO = new GameObject("ToastSafe");
        safeGO.transform.SetParent(go.transform, false);
        var safe = safeGO.AddComponent<RectTransform>();
        safe.anchorMin = Vector2.zero; safe.anchorMax = Vector2.one;
        safe.offsetMin = Vector2.zero; safe.offsetMax = Vector2.zero;
        safeGO.AddComponent<SafeArea>();

        var rootGO = new GameObject("Toast");
        rootGO.transform.SetParent(safe, false);
        _toastRT = rootGO.AddComponent<RectTransform>();
        _toastRT.anchorMin = _toastRT.anchorMax = new Vector2(0.5f, 0f);
        _toastRT.pivot = new Vector2(0.5f, 0.5f);
        _toastRT.anchoredPosition = new Vector2(0f, ToastY);
        _toastGroup = rootGO.AddComponent<CanvasGroup>();
        _toastGroup.blocksRaycasts = false;
        _toastGroup.interactable = false;

        _toastBg = new GameObject("Bg").AddComponent<Image>();
        _toastBg.transform.SetParent(_toastRT, false);
        _toastBg.color = PanelScrim;
        _toastBg.raycastTarget = false;
        Stretch(_toastBg.rectTransform, 0f);

        _toastFrame = new GameObject("Frame").AddComponent<Image>();
        _toastFrame.transform.SetParent(_toastRT, false);
        _toastFrame.sprite = _brackets; _toastFrame.type = Image.Type.Sliced;
        _toastFrame.raycastTarget = false;
        Stretch(_toastFrame.rectTransform, -8f);

        _toastText = Text_("ToastText", _toastRT, new Vector2(0.5f, 0.5f), Vector2.zero,
                           new Vector2(ToastMaxW - 80f, 100f), 34, TextAnchor.MiddleCenter, "");
        _toastText.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        _toastText.textWrappingMode = TextWrappingModes.Normal;
        _toastText.color = TitleText;

        rootGO.SetActive(false);
    }

    private static void Stretch(RectTransform rt, float inset)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(inset, inset); rt.offsetMax = new Vector2(-inset, -inset);
    }

    /// <summary>Show a toast, replacing any toast already up. Rich text is allowed for a second line.</summary>
    public void ShowToast(string message, Color accent, float hold = ToastDefaultHold)
    {
        if (_toastRT == null) return;
        _toastText.text = message;
        _toastText.ForceMeshUpdate();
        float w = Mathf.Min(ToastMaxW, _toastText.preferredWidth + 80f);
        _toastText.rectTransform.sizeDelta = new Vector2(w - 80f, 100f);
        _toastText.ForceMeshUpdate();
        float h = Mathf.Max(76f, _toastText.preferredHeight + 32f);
        _toastRT.sizeDelta = new Vector2(w, h);
        _toastText.rectTransform.sizeDelta = new Vector2(w - 80f, h);

        _toastFrame.color = new Color(accent.r, accent.g, accent.b, 0.9f);
        NeonColor(_toastText, accent, 0.45f);
        _toastHold = hold;
        _toastT = 0f;
        _toastGroup.alpha = 0f;
        _toastRT.gameObject.SetActive(true);
    }

    /// <summary>Remove the toast immediately — called whenever the screen or a panel changes.</summary>
    public void HideToast()
    {
        _toastT = -1f;
        if (_toastRT != null) _toastRT.gameObject.SetActive(false);
    }

    private void TickToast(float dt)
    {
        if (_toastT < 0f) return;
        _toastT += dt;
        float outAt = ToastFadeIn + _toastHold;
        float a = _toastT < ToastFadeIn ? _toastT / ToastFadeIn
                : _toastT < outAt ? 1f
                : 1f - (_toastT - outAt) / ToastFadeOut;
        _toastGroup.alpha = Mathf.Clamp01(a);
        // A small rise on the way in, so it reads as arriving rather than blinking on.
        float rise = Easing.OutCubic(Mathf.Clamp01(_toastT / 0.25f));
        _toastRT.anchoredPosition = new Vector2(0f, ToastY - 14f * (1f - rise));
        if (_toastT > outAt + ToastFadeOut) HideToast();
    }
}
