using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// Press-and-hold confirmation for a button that must not fire by accident, with a fill bar that
/// shows how far along the hold is.
///
/// Built for RETRY. It sits in the bottom-left corner, which is exactly where a right-handed
/// thumb sweeps when it drags the dot left — and one tap threw the whole attempt away. A hold
/// can't happen by brushing past: the finger has to land on the button and stay there. Sliding
/// off it cancels without firing, and a plain tap reports itself so the button can say "hold".
/// </summary>
public class HoldButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
{
    public const float DefaultHoldSeconds = 0.55f;

    public float HoldSeconds = DefaultHoldSeconds;
    public System.Action OnHeld;
    /// <summary>Released before the hold completed — the moment to tell the player to hold.</summary>
    public System.Action OnTapped;
    /// <summary>Filled-type image whose fillAmount tracks the hold. Optional.</summary>
    public Image Fill;

    private float _downAt = -1f;
    private bool _fired;

    public void OnPointerDown(PointerEventData e)
    {
        if (!Interactable()) return;
        _downAt = Time.unscaledTime;
        _fired = false;
    }

    public void OnPointerUp(PointerEventData e)
    {
        bool wasHolding = _downAt >= 0f && !_fired;
        Cancel();
        if (wasHolding && OnTapped != null) OnTapped();
    }

    public void OnPointerExit(PointerEventData e) => Cancel();

    private void OnDisable() => Cancel();

    private void Cancel()
    {
        _downAt = -1f;
        if (Fill != null) Fill.fillAmount = 0f;
    }

    /// <summary>Respect the Button's own interactable flag, so a greyed-out button can't be held.</summary>
    private bool Interactable()
    {
        var b = GetComponent<Selectable>();
        return b == null || b.IsInteractable();
    }

    private void Update()
    {
        if (_downAt < 0f || _fired) return;
        float p = (Time.unscaledTime - _downAt) / Mathf.Max(0.05f, HoldSeconds);
        if (Fill != null) Fill.fillAmount = Mathf.Clamp01(p);
        if (p < 1f) return;

        _fired = true;          // one shot per press
        _downAt = -1f;
        if (Fill != null) Fill.fillAmount = 0f;
        if (OnHeld != null) OnHeld();
    }
}
