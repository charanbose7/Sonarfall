using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// The progress half of the UI: the clear screen's three grade stars, the stars on the level
/// wheel, the shareable result card used by the Daily and by DESCENT, the Descent depth readout,
/// and the one-time HUD pulses that introduce each readout the first time it matters.
/// </summary>
public partial class UIManager
{
    // =========================================================================================
    //  HUD pulses
    // =========================================================================================
    // The tutorial used to finish with three cards pointing at the clock, the reveal counter and
    // the star row, before the player had any reason to care about any of them. Each readout now
    // introduces itself instead, once, at the moment it first changes: a ring blooms around it
    // three times and its name appears underneath. Nothing pauses.

    private GameObject _pulseGO;
    private RectTransform _pulseRT;
    private Image _pulseRing;
    private TMP_Text _pulseLabel;
    private RectTransform _pulseTarget, _pulseTarget2;
    private float _pulseSize = 170f;
    private float _pulseT = -1f;
    private Color _pulseColor = Accent;
    private readonly Queue<string> _pulseQueue = new Queue<string>(4);

    private void BuildHudPulse()
    {
        _pulseGO = new GameObject("HudPulse");
        _pulseGO.transform.SetParent(_canvas.transform, false);
        _pulseRT = _pulseGO.AddComponent<RectTransform>();
        _pulseRT.anchorMin = _pulseRT.anchorMax = new Vector2(0.5f, 0.5f);
        _pulseRT.pivot = new Vector2(0.5f, 0.5f);

        var ringGO = new GameObject("Ring");
        ringGO.transform.SetParent(_pulseRT, false);
        _pulseRing = ringGO.AddComponent<Image>();
        _pulseRing.sprite = VisualUtils.HollowRing();
        _pulseRing.raycastTarget = false;
        var rrt = _pulseRing.rectTransform;
        rrt.anchorMin = rrt.anchorMax = new Vector2(0.5f, 0.5f); rrt.pivot = new Vector2(0.5f, 0.5f);

        _pulseLabel = Display(Text_("PulseLabel", _pulseRT, new Vector2(0.5f, 0.5f), Vector2.zero,
                                    new Vector2(360, 48), 30, TextAnchor.MiddleCenter, ""));
        _pulseLabel.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        _pulseGO.SetActive(false);
    }

    /// <summary>
    /// Introduce a HUD readout: "clock", "reveals" or "hull". Queued if a callout is up or another
    /// pulse is still playing, so two never talk over each other.
    /// </summary>
    public void PulseHud(string item)
    {
        if (_pulseGO == null) return;
        if (TeachOpen || _pulseT >= 0f)
        {
            if (!_pulseQueue.Contains(item)) _pulseQueue.Enqueue(item);
            return;
        }
        StartPulse(item);
    }

    /// <summary>Play the next queued pulse, if there is one and nothing is in the way.</summary>
    private void FlushHudPulse()
    {
        if (_pulseT >= 0f || TeachOpen || _pulseQueue.Count == 0) return;
        StartPulse(_pulseQueue.Dequeue());
    }

    /// <summary>A pulse is playing or waiting its turn. Callouts hold off until it has finished.</summary>
    public bool HudPulseBusy => _pulseT >= 0f || _pulseQueue.Count > 0;

    /// <summary>Drop everything — the level is being left.</summary>
    private void CancelHudPulses()
    {
        _pulseQueue.Clear();
        _pulseT = -1f;
        if (_pulseGO != null) _pulseGO.SetActive(false);
    }

    private void StartPulse(string item)
    {
        switch (item)
        {
            case "clock":
                _pulseTarget = TimerRect; _pulseTarget2 = null;
                _pulseLabel.text = Spaced("TIME LEFT"); _pulseSize = 170f; _pulseColor = Accent;
                break;
            case "reveals":
                _pulseTarget = RevealsRect; _pulseTarget2 = RevealsIconRect;
                _pulseLabel.text = Spaced("REVEALS"); _pulseSize = 165f; _pulseColor = Accent;
                break;
            case "hull":
                // The ring spans the whole row: first plate to third, never the hidden fourth.
                _pulseTarget = _hudHull != null ? _hudHull[0].rectTransform : null;
                _pulseTarget2 = _hudHull != null ? _hudHull[Mathf.Min(2, _hudHull.Length - 1)].rectTransform : null;
                _pulseLabel.text = Spaced("HULL"); _pulseSize = 250f; _pulseColor = Danger;
                break;
            default:
                return;
        }
        if (_pulseTarget == null) return;
        _pulseLabel.color = Color.Lerp(_pulseColor, Color.white, 0.35f);
        Neon(_pulseLabel, _pulseColor, 0.75f);
        _pulseLabel.ForceMeshUpdate();
        _pulseLabel.rectTransform.sizeDelta = new Vector2(Mathf.Max(80f, _pulseLabel.preferredWidth + 24f), 48f);
        _pulseT = 0f;
        _pulseGO.SetActive(true);
        _pulseGO.transform.SetAsLastSibling();
        if (_cover != null) _cover.transform.SetAsLastSibling();   // the level-swap cover still wins
        PlacePulse();
        Haptics.Light();
    }

    /// <summary>Keep the ring on its target (the safe-area inset settles after layout).</summary>
    private void PlacePulse()
    {
        var canvasRT = _canvas.transform as RectTransform;
        Vector2 hA, centre;
        Vector2 a = UiBox(_pulseTarget, canvasRT, out hA);
        if (_pulseTarget2 != null)
        {
            Vector2 hB;
            Vector2 b = UiBox(_pulseTarget2, canvasRT, out hB);
            centre = (Vector2.Min(a - hA, b - hB) + Vector2.Max(a + hA, b + hB)) * 0.5f;
        }
        else centre = a;
        _pulseRT.anchoredPosition = centre;
        _pulseRing.rectTransform.sizeDelta = new Vector2(_pulseSize, _pulseSize);

        // Under the ring (everything pulsed lives along the top edge), clamped inside the screen.
        float halfW = canvasRT.rect.width * 0.5f;
        float lw = _pulseLabel.rectTransform.rect.width * 0.5f;
        float maxX = Mathf.Max(0f, halfW - lw - 12f);
        float x = Mathf.Clamp(centre.x, -maxX, maxX) - centre.x;
        _pulseLabel.rectTransform.anchoredPosition = new Vector2(x, -(_pulseSize * 0.5f + 34f));
    }

    private void TickHudPulse(float dt)
    {
        if (_pulseT < 0f) return;
        _pulseT += dt;
        float dur = GameConfig.HudPulseSeconds;
        if (_pulseT >= dur || _pulseTarget == null)
        {
            _pulseT = -1f;
            _pulseGO.SetActive(false);
            FlushHudPulse();
            return;
        }
        PlacePulse();

        // Three blooms outward, each fading as it grows.
        float beat = (_pulseT / dur) * 3f;
        float f = beat - Mathf.Floor(beat);
        _pulseRing.rectTransform.localScale = Vector3.one * Mathf.Lerp(0.8f, 1.3f, Easing.OutCubic(f));
        _pulseRing.color = new Color(_pulseColor.r, _pulseColor.g, _pulseColor.b, (1f - f) * 0.95f);

        float la = Mathf.Min(Mathf.Clamp01(_pulseT / 0.2f), Mathf.Clamp01((dur - _pulseT) / 0.4f));
        var lc = _pulseLabel.color;
        _pulseLabel.color = new Color(lc.r, lc.g, lc.b, la);
    }

    // =========================================================================================
    //  Grade stars on the clear screen
    // =========================================================================================
    private readonly Image[] _gradeStars = new Image[3];
    private readonly TMP_Text[] _gradeCaps = new TMP_Text[3];
    private readonly bool[] _gradeEarned = new bool[3];
    private readonly bool[] _gradeLanded = new bool[3];
    private float _gradeT = -1f;

    private const float GradeStagger = 0.28f;   // seconds between stars landing

    private void BuildGradeRow(RectTransform parent)
    {
        for (int i = 0; i < 3; i++)
        {
            float x = -240f + i * 240f;
            var sGO = new GameObject("GradeStar" + i);
            sGO.transform.SetParent(parent, false);
            var img = sGO.AddComponent<Image>();
            img.sprite = VisualUtils.PingStar();
            img.raycastTarget = false;
            var rt = img.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(x, 170f); rt.sizeDelta = new Vector2(124f, 124f);
            _gradeStars[i] = img;

            var cap = Text_("GradeCap" + i, parent, new Vector2(0.5f, 0.5f), new Vector2(x, 54f),
                            new Vector2(300f, 96f), 30, TextAnchor.MiddleCenter, "");
            cap.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            cap.lineSpacing = -8f;
            _gradeCaps[i] = cap;
        }
        ResetGradeRow();
    }

    private void ResetGradeRow()
    {
        _gradeT = -1f;
        for (int i = 0; i < 3; i++)
        {
            _gradeLanded[i] = false;
            if (_gradeStars[i] != null) _gradeStars[i].rectTransform.localScale = Vector3.zero;
            if (_gradeCaps[i] != null) { _gradeCaps[i].text = ""; _gradeCaps[i].alpha = 0f; }
        }
    }

    /// <summary>
    /// Land the three stars one after another, each with its rule printed underneath — earned ones
    /// lit, missed ones dim but still labelled, so the player always knows what the missing one
    /// was for. <paramref name="captions"/> are the three rule lines in order.
    /// </summary>
    public void ShowGrade(bool exit, bool noDamage, bool underPar, string[] captions)
    {
        _gradeEarned[0] = exit; _gradeEarned[1] = noDamage; _gradeEarned[2] = underPar;
        for (int i = 0; i < 3; i++)
        {
            _gradeLanded[i] = false;
            _gradeCaps[i].text = captions != null && i < captions.Length ? captions[i] : "";
            _gradeCaps[i].alpha = 0f;
            _gradeStars[i].rectTransform.localScale = Vector3.zero;
        }
        _gradeT = 0f;
    }

    /// <summary>Seconds from ShowGrade until the last star has landed — when to drop the grade word.</summary>
    public static float GradeRowSeconds => GradeStagger * 2f + 0.3f;

    private void TickGrades(float dt)
    {
        if (_gradeT < 0f) return;
        _gradeT += dt;
        bool allDone = true;
        for (int i = 0; i < 3; i++)
        {
            float local = _gradeT - i * GradeStagger;
            if (local < 0f) { allDone = false; continue; }
            bool earned = _gradeEarned[i];

            if (!_gradeLanded[i])
            {
                _gradeLanded[i] = true;
                if (earned)
                {
                    if (Audio != null) Audio.PlayStar(i);
                    Haptics.Light();
                }
            }

            float k = Mathf.Clamp01(local / 0.3f);
            float scale = earned ? Mathf.Lerp(1.7f, 1f, Easing.OutBack(k)) : Mathf.Lerp(0.5f, 0.82f, Easing.OutCubic(k));
            _gradeStars[i].rectTransform.localScale = Vector3.one * scale;
            _gradeStars[i].color = earned ? StarLit : new Color(0.62f, 0.72f, 0.85f, 0.20f);
            _gradeCaps[i].color = earned ? new Color(0.92f, 0.97f, 1f, 1f) : new Color(0.62f, 0.72f, 0.85f, 0.62f);
            _gradeCaps[i].alpha = Mathf.Clamp01(local / 0.25f) * (earned ? 1f : 0.62f);
            if (k < 1f) allDone = false;
        }
        if (allDone) _gradeT = -1f;   // settled; leave everything where it landed
    }

    // =========================================================================================
    //  Stars on the level wheel
    // =========================================================================================
    // Seven rows are ever visible (the band and three either side), so seven sets of three stars
    // are pooled and re-seated whenever the selection moves, rather than 600 images for 200 rows.
    private const int LevelStarRows = 7;
    private Image[,] _levelStars;

    private void BuildLevelStars()
    {
        _levelStars = new Image[LevelStarRows, 3];
        for (int r = 0; r < LevelStarRows; r++)
        for (int j = 0; j < 3; j++)
        {
            var go = new GameObject("LvlStar" + r + "_" + j);
            go.transform.SetParent(_levelContent, false);
            var img = go.AddComponent<Image>();
            img.sprite = VisualUtils.PingStar();
            img.raycastTarget = false;
            var rt = img.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f); rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(30f, 30f);
            go.SetActive(false);
            _levelStars[r, j] = img;
        }
    }

    /// <summary>Seat one pooled set of stars just right of a row's number.</summary>
    private void PlaceLevelStars(int slot, RectTransform row, int level, float alpha)
    {
        if (_levelStars == null || slot >= LevelStarRows) return;
        int grade = SaveData.LevelGrade(level);
        var t = row.GetComponent<TMP_Text>();
        float s = row.localScale.x;
        float half = (t != null ? t.preferredWidth : 60f) * 0.5f * s;
        for (int j = 0; j < 3; j++)
        {
            var img = _levelStars[slot, j];
            img.gameObject.SetActive(true);
            img.rectTransform.anchoredPosition = row.anchoredPosition + new Vector2(half + 34f + j * 36f, 0f);
            img.rectTransform.localScale = Vector3.one * Mathf.Lerp(0.85f, 1.1f, Mathf.InverseLerp(0.82f, 1.18f, s));
            img.color = j < grade ? new Color(StarLit.r, StarLit.g, StarLit.b, alpha)
                                  : new Color(0.62f, 0.78f, 0.92f, 0.20f * alpha);
        }
    }

    private void HideLevelStarsFrom(int slot)
    {
        if (_levelStars == null) return;
        for (int r = slot; r < LevelStarRows; r++)
            for (int j = 0; j < 3; j++)
                if (_levelStars[r, j].gameObject.activeSelf) _levelStars[r, j].gameObject.SetActive(false);
    }

    // =========================================================================================
    //  Result card — the Daily and the end of a dive
    // =========================================================================================
    // Laid out as a card on purpose, framed and self-contained, because it is the thing players
    // will screenshot. SHARE sends the same result as one line of text — "Sonarfall Daily #59 ·
    // 9.4s · 2 pings" — through the phone's own share sheet, which is free marketing and needs no
    // network code in the game at all: the app the player shares to does the sending.

    private GameObject _resultPanel;
    private TMP_Text _resTitle, _resBig, _resDetail;
    private readonly Image[] _resStars = new Image[3];
    private TMP_Text _resPrimaryLbl, _resSecondaryLbl;
    private Button _resSecondaryBtn, _resShareBtn;
    private System.Action _resPrimary, _resSecondary;
    private string _resShareText;

    public bool ResultOpen => _resultPanel != null && _resultPanel.activeSelf;

    private GameObject BuildResultPanel()
    {
        var go = new GameObject("ResultCard");
        go.transform.SetParent(_canvas.transform, false);
        var dim = go.AddComponent<Image>();
        // Fully opaque: the menu/maze behind must not read through and compete for attention.
        dim.color = new Color(PanelScrim.r, PanelScrim.g, PanelScrim.b, 1f);
        dim.raycastTarget = true;
        var drt = dim.rectTransform; drt.anchorMin = Vector2.zero; drt.anchorMax = Vector2.one; drt.offsetMin = Vector2.zero; drt.offsetMax = Vector2.zero;
        var root = go.transform as RectTransform;

        // The card: everything worth a screenshot, inside one frame.
        var cardGO = new GameObject("Card");
        cardGO.transform.SetParent(root, false);
        var card = cardGO.AddComponent<Image>();
        card.color = CardFill; card.raycastTarget = false;
        var crt = card.rectTransform;
        crt.anchorMin = crt.anchorMax = new Vector2(0.5f, 0.5f); crt.pivot = new Vector2(0.5f, 0.5f);
        crt.anchoredPosition = new Vector2(0, 170); crt.sizeDelta = new Vector2(920, 640);

        var frameGO = new GameObject("Frame");
        frameGO.transform.SetParent(root, false);
        var frame = frameGO.AddComponent<Image>();
        frame.sprite = _brackets; frame.type = Image.Type.Sliced; frame.raycastTarget = false;
        frame.color = new Color(Gold.r, Gold.g, Gold.b, 0.9f);
        _resFrame = frame;
        var fr = frame.rectTransform;
        fr.anchorMin = fr.anchorMax = new Vector2(0.5f, 0.5f); fr.pivot = new Vector2(0.5f, 0.5f);
        fr.anchoredPosition = new Vector2(0, 170); fr.sizeDelta = new Vector2(940, 660);

        var brand = Text_("ResBrand", root, new Vector2(0.5f, 0.5f), new Vector2(0, 440), new Vector2(800, 40), 26, TextAnchor.MiddleCenter, Spaced("SONARFALL"));
        brand.color = new Color(0.62f, 0.78f, 0.92f, 0.6f);
        brand.rectTransform.pivot = new Vector2(0.5f, 0.5f);

        _resTitle = Display(Text_("ResTitle", root, new Vector2(0.5f, 0.5f), new Vector2(0, 370), new Vector2(880, 100), 64, TextAnchor.MiddleCenter, ""));
        _resTitle.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        _resTitle.enableAutoSizing = true; _resTitle.fontSizeMax = 64f; _resTitle.fontSizeMin = 36f;

        for (int i = 0; i < 3; i++)
        {
            var sGO = new GameObject("ResStar" + i);
            sGO.transform.SetParent(root, false);
            var img = sGO.AddComponent<Image>();
            img.sprite = VisualUtils.PingStar(); img.raycastTarget = false;
            var rt = img.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(-150f + i * 150f, 250f); rt.sizeDelta = new Vector2(104f, 104f);
            _resStars[i] = img;
        }

        _resBig = Display(Text_("ResBig", root, new Vector2(0.5f, 0.5f), new Vector2(0, 120), new Vector2(900, 110), 66, TextAnchor.MiddleCenter, ""));
        _resBig.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        _resBig.enableAutoSizing = true; _resBig.fontSizeMax = 66f; _resBig.fontSizeMin = 36f;
        _resBig.color = TitleText;
        Neon(_resBig, Accent, 0.6f);

        _resDetail = Text_("ResDetail", root, new Vector2(0.5f, 0.5f), new Vector2(0, -20), new Vector2(860, 180), 34, TextAnchor.MiddleCenter, "");
        _resDetail.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        _resDetail.color = new Color(0.86f, 0.93f, 1f, 0.92f);
        _resDetail.textWrappingMode = TextWrappingModes.Normal;

        TMP_Text shareLbl;
        _resShareBtn = Button_("ResShare", root, new Vector2(0.5f, 0.5f), new Vector2(0, -250),
                               new Vector2(420, 104), Spaced("SHARE"), 36, Gold, false, out shareLbl);
        _resShareBtn.onClick.AddListener(() =>
        {
            if (string.IsNullOrEmpty(_resShareText)) return;
            // The toast's font has no star glyph: quote the line up to them, the clipboard keeps them.
            string line = _resShareText.Split('\n')[0].TrimEnd('★', '☆', ' ', '·');
            // Only say COPIED once the copy has actually happened — on WebGL that is a browser
            // promise, so the toast waits for it. A share sheet speaks for itself; a cancelled
            // one needs no comment.
            ShareUtil.ShareText(_resShareText, r =>
            {
                if (r == ShareUtil.Result.Copied) ShowToast("COPIED\n<size=60%>" + line + "</size>", Gold);
                else if (r == ShareUtil.Result.Failed) ShowToast("COULDN'T COPY\n<size=60%>your browser blocked the clipboard</size>", Danger);
            });
        });

        TMP_Text okLbl;
        var okBtn = Button_("ResPrimary", root, new Vector2(0.5f, 0.5f), new Vector2(0, -400), new Vector2(480, 128),
                            Spaced("CONTINUE"), 42, Accent, true, out okLbl);
        _resPrimaryLbl = okLbl;
        okBtn.onClick.AddListener(() =>
        {
            go.SetActive(false);
            var cb = _resPrimary; _resPrimary = null; _resSecondary = null;
            if (cb != null) cb();
        });

        TMP_Text secLbl;
        _resSecondaryBtn = Button_("ResSecondary", root, new Vector2(0.5f, 0.5f), new Vector2(0, -545), new Vector2(380, 100),
                                   Spaced("MENU"), 34, Accent, false, out secLbl);
        _resSecondaryLbl = secLbl;
        _resSecondaryBtn.onClick.AddListener(() =>
        {
            go.SetActive(false);
            var cb = _resSecondary; _resPrimary = null; _resSecondary = null;
            if (cb != null) cb();
        });

        go.SetActive(false);
        return go;
    }

    private Image _resFrame;

    /// <summary>
    /// Show the result card. <paramref name="stars"/> &lt; 0 hides the star row (a dive has a depth,
    /// not a grade). <paramref name="onSecondary"/> null hides the second button, and a null
    /// <paramref name="shareText"/> hides SHARE.
    /// </summary>
    public void ShowResult(string title, Color titleColor, int stars, string bigLine, string detail,
                           string shareText, string primaryLabel, System.Action onPrimary,
                           string secondaryLabel = null, System.Action onSecondary = null)
    {
        if (_resultPanel == null) return;
        _resTitle.text = Spaced(title);
        _resTitle.color = Color.Lerp(titleColor, Color.white, 0.25f);
        Neon(_resTitle, titleColor, 0.8f);
        if (_resFrame != null) _resFrame.color = new Color(titleColor.r, titleColor.g, titleColor.b, 0.9f);

        for (int i = 0; i < 3; i++)
        {
            _resStars[i].gameObject.SetActive(stars >= 0);
            _resStars[i].color = i < stars ? StarLit : new Color(0.62f, 0.72f, 0.85f, 0.2f);
        }
        _resBig.text = bigLine;
        _resDetail.text = detail;
        _resShareText = shareText;
        _resShareBtn.gameObject.SetActive(!string.IsNullOrEmpty(shareText));

        _resPrimaryLbl.text = Spaced(primaryLabel);
        _resPrimary = onPrimary;
        bool hasSecondary = !string.IsNullOrEmpty(secondaryLabel);
        _resSecondaryBtn.gameObject.SetActive(hasSecondary);
        if (hasSecondary) _resSecondaryLbl.text = Spaced(secondaryLabel);
        _resSecondary = onSecondary;

        ShowHud(false);
        ShowInGameGear(false);
        HideToast();
        _resultPanel.SetActive(true);
        _resultPanel.transform.SetAsLastSibling();
        if (_teachPanel != null) _teachPanel.transform.SetAsLastSibling();
        if (_bannerBg != null) { _bannerBg.transform.SetAsLastSibling(); _bannerFrame.transform.SetAsLastSibling(); _bannerText.transform.SetAsLastSibling(); }
        if (_cover != null) _cover.transform.SetAsLastSibling();
    }

    public void HideResult()
    {
        HideToast();
        if (_resultPanel != null) _resultPanel.SetActive(false);
        _resPrimary = null; _resSecondary = null;
    }

    // =========================================================================================
    //  DESCENT header
    // =========================================================================================
    /// <summary>
    /// The dive's HUD heading: depth instead of a level number, and the maze's rules (or the dive
    /// count) where the sector caption normally sits.
    /// </summary>
    public void SetDescentHeader(int mazesCleared, string sub, Color color)
    {
        _levelText.text = Spaced("DEPTH  " + GameConfig.DepthText(mazesCleared).ToUpperInvariant());
        if (_sectorText == null) return;
        _sectorText.text = sub;
        _sectorText.color = Color.Lerp(color, Color.white, 0.35f);
        NeonColor(_sectorText, color, 0.6f);
    }
}
