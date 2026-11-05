using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// First-run tutorial — teach by doing. It refuses to advance until the player has actually
/// performed each action, and it explains nothing in advance.
///
///   Step 1 — an animated finger mimes a drag; the player must really drag the dot.
///   Step 2 — the finger mimes a tap; the player must really fire a ping.
///   Step 3 — a beat to watch the walls light up (the reveal counter pulses as it drops).
///   Step 4 — the game pauses on one line pointing at the exit; any tap starts the clock.
///
/// The goal callout exists because the first two steps teach the controls and nothing else:
/// testers came out of the old tutorial able to move and ping and with no idea there was anything
/// to go *to*. Knowing the buttons is not the same as knowing the goal.
///
/// What it no longer does is walk the player round the HUD. It used to end on three more cards —
/// THE CLOCK, YOUR REVEALS, YOUR STARS — each with an OK button locked for three seconds, before
/// level 1 had even started. Each readout now introduces itself with a pulse the first time it
/// changes (GameManager decides when), which is when the player has a reason to look at it.
///
/// Only ever runs on the very first play (SaveData.HintSeen), and only in the level ladder.
/// </summary>
public class TutorialController : MonoBehaviour
{
    private enum Step { Drag, Ping, Look, Goal, Done }

    private GameManager _gm;
    private UIManager _ui;
    private PlayerController _player;

    private Canvas _canvas;
    private GameObject _root;
    private Image _finger;
    private Image _fingerRipple;
    private TMP_Text _title, _sub;

    private Step _step = Step.Drag;
    private float _t;
    private float _dragAccum;
    private Vector2 _lastPlayerPos;
    private bool _running;

    // Prompt layout, in canvas units up from the bottom edge (see BuildUI).
    private const float PromptTitleY = 370f;
    private const float PromptSubY   = 290f;
    private const float FingerY      = 160f;

    /// <summary>How long to let the first reveal play before pointing at the exit.</summary>
    private const float LookSeconds = 1.3f;

    /// <summary>True when this player has never completed the tutorial.</summary>
    public bool ShouldRun => !SaveData.HintSeen;

    public void Begin(GameManager gm, UIManager ui, PlayerController player)
    {
        _gm = gm; _ui = ui; _player = player;
        // Marked as seen the moment it STARTS, not when it finishes. A player who quits to the
        // menu half way through used to get the whole thing again on their next CONTINUE, which
        // reads as the game refusing to let them past the tutorial. Bailing early is safe now that
        // GameConfig.PingNudgeDelay catches anyone who never learned to ping.
        SaveData.MarkHintSeen();
        _step = Step.Drag; _t = 0f; _dragAccum = 0f;
        // Must be cleared, not just the step index. This controller survives a progress reset, so
        // a leftover _pinged from the previous run satisfied the ping gate the instant the drag
        // step finished — the tutorial jumped straight from "DRAG TO MOVE" to the exit.
        _pinged = false;
        _lastPlayerPos = player.transform.position;
        BuildUI();
        if (_root != null) _root.SetActive(true);
        _running = true;
        SetStepText();
    }

    private void BuildUI()
    {
        if (_root != null) return;

        _canvas = GetComponentInChildren<Canvas>();
        if (_canvas == null) return;

        _root = new GameObject("TutorialOverlay");
        _root.transform.SetParent(_canvas.transform, false);
        var rt = _root.AddComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;

        // Use the game's own typefaces. This overlay used to fall back to TMP's default
        // (LiberationSans), so the very first text a new player ever saw was in a font that appears
        // nowhere else in the game — testers reported it as blurry and hard to read next to the
        // Orbitron/ChakraPetch everything else uses.
        var titleFont = _ui != null ? _ui.TitleFont : null;
        var bodyFont  = _ui != null ? _ui.BodyFont  : null;
        if (titleFont == null) titleFont = TMP_Settings.defaultFontAsset;
        if (bodyFont  == null) bodyFont  = titleFont;

        // A light dim — the player is playing, not reading, so the maze stays the main thing.
        var dim = new GameObject("Dim");
        dim.transform.SetParent(_root.transform, false);
        var dimImg = dim.AddComponent<Image>();
        dimImg.color = new Color(0.01f, 0.015f, 0.03f, 0.35f);
        dimImg.raycastTarget = false;
        var drt = dimImg.rectTransform;
        drt.anchorMin = Vector2.zero; drt.anchorMax = Vector2.one; drt.offsetMin = Vector2.zero; drt.offsetMax = Vector2.zero;

        // Sizes up ~20% and the subtitle nearly opaque: at 34pt / 0.8 alpha over a lit maze the
        // second line was the thing testers said they could not read at all.
        //
        // Placed BELOW the maze, measured up from the bottom edge, not across its middle. Centred
        // a little above mid-screen, the prompt ran straight through the exit whenever the exit
        // spawned top-right — which on the tutorial layout is always. The camera keeps the maze
        // inside the band between the HUD and the bottom bar (UIManager.GetPlayfieldInsets), and
        // on level 1's 6x6 grid its bottom edge measures 1444px of 1920; the title starts at
        // ~1500. The mimed finger sits lower still, in the thumb zone, where a real thumb goes.
        _title = MakeText(titleFont, "TutTitle", new Vector2(0, PromptTitleY), 76, "DRAG TO MOVE");
        _title.color = new Color(0.85f, 0.96f, 1f, 1f);
        _title.characterSpacing = 8f;          // matches the spaced caps used everywhere else
        _sub = MakeText(bodyFont, "TutSub", new Vector2(0, PromptSubY), 42, "slide your finger anywhere");
        _sub.color = new Color(0.88f, 0.94f, 1f, 0.97f);

        // The mimed finger: a soft dot plus an expanding ripple for taps.
        var ripple = new GameObject("Ripple");
        ripple.transform.SetParent(_root.transform, false);
        _fingerRipple = ripple.AddComponent<Image>();
        _fingerRipple.sprite = VisualUtils.HollowRing();
        _fingerRipple.raycastTarget = false;
        _fingerRipple.color = new Color(1f, 1f, 1f, 0f);
        var rrt = _fingerRipple.rectTransform;
        rrt.anchorMin = rrt.anchorMax = new Vector2(0.5f, 0f); rrt.pivot = new Vector2(0.5f, 0.5f);
        rrt.sizeDelta = new Vector2(190, 190);

        var fingerGO = new GameObject("Finger");
        fingerGO.transform.SetParent(_root.transform, false);
        _finger = fingerGO.AddComponent<Image>();
        _finger.sprite = VisualUtils.Disc();
        _finger.raycastTarget = false;
        _finger.color = new Color(1f, 1f, 1f, 0.85f);
        var frt = _finger.rectTransform;
        frt.anchorMin = frt.anchorMax = new Vector2(0.5f, 0f); frt.pivot = new Vector2(0.5f, 0.5f);
        frt.sizeDelta = new Vector2(86, 86);
    }

    private TMP_Text MakeText(TMP_FontAsset font, string name, Vector2 pos, int size, string content)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_root.transform, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        t.font = font; t.fontSize = size; t.alignment = TextAlignmentOptions.Center;
        t.text = content; t.raycastTarget = false;
        t.textWrappingMode = TextWrappingModes.NoWrap;
        t.overflowMode = TextOverflowModes.Overflow;
        var rt = t.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f); rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos; rt.sizeDelta = new Vector2(1000, 120);
        return t;
    }

    private void SetStepText()
    {
        if (_title == null) return;
        if (_step == Step.Drag) { _title.text = "DRAG TO MOVE";  _sub.text = "slide your finger anywhere"; }
        else                    { _title.text = "TAP TO PING";   _sub.text = "reveals the walls around you"; }
    }

    private void Update()
    {
        if (!_running) return;
        _t += Time.unscaledDeltaTime;

        if (_step == Step.Drag) AnimateDrag();
        else if (_step == Step.Ping) AnimateTap();

        CheckProgress();
    }

    /// <summary>Finger slides back and forth to mime a drag.</summary>
    private void AnimateDrag()
    {
        float loop = Mathf.Repeat(_t, 2.2f) / 2.2f;
        float eased = Easing.InOutSine(Mathf.PingPong(loop * 2f, 1f));
        float x = Mathf.Lerp(-190f, 190f, eased);
        _finger.rectTransform.anchoredPosition = new Vector2(x, FingerY);
        _finger.color = new Color(1f, 1f, 1f, 0.9f);
        _fingerRipple.color = new Color(1f, 1f, 1f, 0f);
    }

    /// <summary>Finger presses in place with an expanding ripple to mime a tap.</summary>
    private void AnimateTap()
    {
        float loop = Mathf.Repeat(_t, 1.5f) / 1.5f;
        _finger.rectTransform.anchoredPosition = new Vector2(0f, FingerY);
        float press = loop < 0.2f ? 1f - loop / 0.2f * 0.25f : 0.75f + Mathf.Min(1f, (loop - 0.2f) / 0.3f) * 0.25f;
        _finger.rectTransform.localScale = Vector3.one * press;

        float rip = Mathf.Clamp01((loop - 0.15f) / 0.6f);
        _fingerRipple.rectTransform.anchoredPosition = new Vector2(0f, FingerY);
        _fingerRipple.rectTransform.localScale = Vector3.one * Mathf.Lerp(0.4f, 1.8f, rip);
        _fingerRipple.color = new Color(1f, 1f, 1f, (1f - rip) * 0.7f * (loop > 0.15f ? 1f : 0f));
    }

    /// <summary>Gate each step on the player genuinely performing the action.</summary>
    private void CheckProgress()
    {
        if (_step == Step.Drag)
        {
            Vector2 now = _player.transform.position;
            _dragAccum += Vector2.Distance(now, _lastPlayerPos);
            _lastPlayerPos = now;

            if (_dragAccum > GameConfig.TutorialDragDistance)
            {
                _step = Step.Ping;
                _t = 0f;
                _finger.rectTransform.localScale = Vector3.one;
                SetStepText();
                Haptics.Light();
            }
        }
        else if (_step == Step.Ping)
        {
            // GameManager tells us when a ping actually fired.
            if (_pinged)
            {
                // Get out of the way and let the reveal be the lesson: the walls bloom, the
                // counter drops and pulses, and only then does anything else get said.
                _step = Step.Look;
                _t = 0f;
                if (_root != null) _root.SetActive(false);
                Haptics.Light();
            }
        }
        else if (_step == Step.Look)
        {
            if (_t >= LookSeconds) ShowGoal();
        }
    }

    /// <summary>
    /// Last step: one line, pointing at the exit. The callout brings its own veil and marker and
    /// pauses the world; the clock starts on the tap that dismisses it.
    /// </summary>
    private void ShowGoal()
    {
        _step = Step.Goal;
        _running = false;                            // nothing left to animate here
        if (_ui == null || _gm == null) { Finish(); return; }

        _ui.ShowTeachCard(
            "THE EXIT",
            "Reach it before the clock runs out.",
            UIManager.Accent, GameConfig.ExitColor,
            Finish,
            _gm.GameCamera, _gm.ExitWorldPos, "EXIT",
            VisualUtils.DashedRing());
    }

    private bool _pinged;
    /// <summary>Called by GameManager when the player fires a ping.</summary>
    public void NotifyPinged() => _pinged = true;

    /// <summary>Is the tutorial currently blocking the ping action? (Drag step must come first.)</summary>
    public bool PingAllowed => _step == Step.Ping;

    /// <summary>Force the overlay away (e.g. when bailing back to the main menu).</summary>
    public void Hide()
    {
        _running = false;
        if (_root != null) _root.SetActive(false);
    }

    private void Finish()
    {
        _running = false;
        _step = Step.Done;
        SaveData.MarkHintSeen();
        if (_root != null) _root.SetActive(false);
        if (_gm != null) _gm.OnTutorialComplete();
    }
}
