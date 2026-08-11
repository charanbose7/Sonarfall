using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// First-run tutorial, deliberately BLOCKING: playtesters skipped text-only hints entirely and
/// started with no idea what the controls were, so this refuses to advance until the player has
/// actually performed each action.
///
///   Step 1 — an animated finger mimes a drag; the player must really drag the dot.
///   Step 2 — the finger mimes a tap; the player must really fire a ping.
///   Step 3 — a marker points at the exit and the player has to acknowledge it.
///   Steps 4-6 — the clock, the reveal counter and the star row, each pointed at in turn.
///
/// Step 3 exists because the first two teach the controls and nothing else: testers came out of
/// the old tutorial able to move and ping, were shown "GO!", and had no idea there was anything
/// to go *to*. Knowing the buttons is not the same as knowing the goal.
///
/// Steps 4-6 exist for the same reason one level down. Telling someone "reach it before the clock
/// runs out" is worthless if they cannot say which number on screen is the clock — so each of the
/// three resources gets its own card with a reticle sitting on the actual HUD element, rather than
/// one card listing all three in prose.
///
/// Only ever runs on the very first play (SaveData.HintSeen), and only in the endless run.
/// </summary>
public class TutorialController : MonoBehaviour
{
    private enum Step { Drag, Ping, Goal, Clock, Reveals, Lives, Done }

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
        // step finished — the tutorial jumped straight from "DRAG TO MOVE" to "GO!".
        _pinged = false;
        _lastPlayerPos = player.transform.position;
        BuildUI();
        _running = true;
        SetStepText();
    }

    private void BuildUI()
    {
        if (_root != null) { _root.SetActive(true); return; }

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

        // Dim the maze slightly so the instruction reads, without hiding the game.
        var dim = new GameObject("Dim");
        dim.transform.SetParent(_root.transform, false);
        var dimImg = dim.AddComponent<Image>();
        dimImg.color = new Color(0.01f, 0.015f, 0.03f, 0.55f);
        dimImg.raycastTarget = false;
        var drt = dimImg.rectTransform;
        drt.anchorMin = Vector2.zero; drt.anchorMax = Vector2.one; drt.offsetMin = Vector2.zero; drt.offsetMax = Vector2.zero;

        // Sizes up ~20% and the subtitle nearly opaque: at 34pt / 0.8 alpha over a lit maze the
        // second line was the thing testers said they could not read at all.
        _title = MakeText(titleFont, "TutTitle", new Vector2(0, 300), 76, "DRAG TO MOVE");
        _title.color = new Color(0.85f, 0.96f, 1f, 1f);
        _title.characterSpacing = 8f;          // matches the spaced caps used everywhere else
        _sub = MakeText(bodyFont, "TutSub", new Vector2(0, 200), 42, "slide your finger anywhere");
        _sub.color = new Color(0.88f, 0.94f, 1f, 0.97f);

        // The mimed finger: a soft dot plus an expanding ripple for taps.
        var ripple = new GameObject("Ripple");
        ripple.transform.SetParent(_root.transform, false);
        _fingerRipple = ripple.AddComponent<Image>();
        _fingerRipple.sprite = VisualUtils.HollowRing();
        _fingerRipple.raycastTarget = false;
        _fingerRipple.color = new Color(1f, 1f, 1f, 0f);
        var rrt = _fingerRipple.rectTransform;
        rrt.anchorMin = rrt.anchorMax = new Vector2(0.5f, 0.5f); rrt.pivot = new Vector2(0.5f, 0.5f);
        rrt.sizeDelta = new Vector2(190, 190);

        var fingerGO = new GameObject("Finger");
        fingerGO.transform.SetParent(_root.transform, false);
        _finger = fingerGO.AddComponent<Image>();
        _finger.sprite = VisualUtils.Disc();
        _finger.raycastTarget = false;
        _finger.color = new Color(1f, 1f, 1f, 0.85f);
        var frt = _finger.rectTransform;
        frt.anchorMin = frt.anchorMax = new Vector2(0.5f, 0.5f); frt.pivot = new Vector2(0.5f, 0.5f);
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
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f);
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
        _finger.rectTransform.anchoredPosition = new Vector2(x, -140f);
        _finger.color = new Color(1f, 1f, 1f, 0.9f);
        _fingerRipple.color = new Color(1f, 1f, 1f, 0f);
    }

    /// <summary>Finger presses in place with an expanding ripple to mime a tap.</summary>
    private void AnimateTap()
    {
        float loop = Mathf.Repeat(_t, 1.5f) / 1.5f;
        _finger.rectTransform.anchoredPosition = new Vector2(0f, -140f);
        float press = loop < 0.2f ? 1f - loop / 0.2f * 0.25f : 0.75f + Mathf.Min(1f, (loop - 0.2f) / 0.3f) * 0.25f;
        _finger.rectTransform.localScale = Vector3.one * press;

        float rip = Mathf.Clamp01((loop - 0.15f) / 0.6f);
        _fingerRipple.rectTransform.anchoredPosition = new Vector2(0f, -140f);
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
                _step = Step.Goal;
                Haptics.Light();
                ShowGoal();
            }
        }
    }

    /// <summary>
    /// Last step: stop miming controls and show what they are for. The teach card brings its own
    /// veil and marker, so this overlay gets out of the way entirely rather than stacking two
    /// dimmed layers and two titles on top of each other.
    /// </summary>
    private void ShowGoal()
    {
        _running = false;                            // nothing left to animate here
        if (_root != null) _root.SetActive(false);
        if (_ui == null || _gm == null) { Finish(); return; }

        _ui.ShowTeachCard(
            "FIND THE EXIT",
            "That marker is the way out.\n\n" +
            "Ping to remember the walls, then reach it before the clock runs out.",
            UIManager.Accent, GameConfig.ExitColor,
            ShowClock,
            _gm.GameCamera, _gm.ExitWorldPos, "EXIT");
    }

    /// <summary>Step 4 — which number is the clock.</summary>
    private void ShowClock()
    {
        _step = Step.Clock;
        _ui.ShowTeachCardAtUI(
            "THE CLOCK",
            "This is your time.\n\n" +
            "Run it down to zero and the level restarts.",
            UIManager.Danger, Color.clear,
            ShowReveals,
            // Capped by geometry, not taste. Measured HUD column, top to bottom:
            //   LEVEL     883..946
            //   SHALLOWS  855..886
            //   34        741..856   <- the number's top edge is 856
            //   SECONDS   728..754
            // The sector caption starts 1px above the number, so a ring wide enough to enclose a
            // 143x115 glyph (radius ~92) unavoidably cuts through it. 165 is the largest that
            // still reads as circling the clock rather than slicing the heading.
            _ui.TimerRect, "TIME", 165f);
    }

    /// <summary>Step 5 — the reveal budget, the actual scarce resource.</summary>
    private void ShowReveals()
    {
        _step = Step.Reveals;
        _ui.ShowTeachCardAtUI(
            "YOUR REVEALS",
            "Every ping costs one of these.\n\n" +
            "Run out and you can still move — but you'll be doing it from memory, in the dark.",
            UIManager.Accent, Color.clear,
            ShowLives,
            // Both halves of the readout: the digit sits at x 445 and the dot at 506, so anchoring
            // on the digit alone left the icon outside the ring. Centred on the pair it lands at 479.
            _ui.RevealsRect, "REVEALS", 165f, _ui.RevealsIconRect);
    }

    /// <summary>Step 6 — stars are lives, not a score.</summary>
    private void ShowLives()
    {
        _step = Step.Lives;
        _ui.ShowTeachCardAtUI(
            "YOUR STARS",
            "These are your lives — <b>" + GameConfig.StartStars + "</b> per level.\n\n" +
            "Hitting a decoy costs one. Lose them all and the level restarts.",
            UIManager.Gold, Color.clear,
            Finish,
            // Bigger than the others on purpose: this ring has to enclose all three stars, which
            // span 124px centre-to-centre plus the star width either side.
            _ui.StarsRect, "LIVES", 250f);
    }

    private bool _pinged;
    /// <summary>Called by GameManager when the player fires a ping.</summary>
    public void NotifyPinged() => _pinged = true;

    /// <summary>Is the tutorial currently blocking the ping action? (Drag step must come first.)</summary>
    public bool PingAllowed => _step != Step.Drag;

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
        // No "GO!" banner any more: the goal step already told the player where to go, and a
        // banner on top of that is one more thing between them and the maze.
        if (_gm != null) _gm.OnTutorialComplete();
    }
}
