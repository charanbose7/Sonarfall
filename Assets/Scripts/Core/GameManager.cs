using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public enum GameState { Start, Playing, Celebrating, Tutorial }

/// <summary>
/// The brain: level flow, ping budget, fail timer, hull (lives), grades, rubber-banding,
/// the twists (moving exit, decoys), near-miss exit glow, first-encounter callouts, and the
/// whole level-clear celebration (hitstop -> burst -> flash -> punch-zoom -> grade stars ->
/// auto-advance). Everything else is handed to it by GameBootstrap.
///
/// Split across files: the sector rules (hunter, gates, still water, currents) live in
/// GameManager.Hazards.cs and the endless DESCENT mode in GameManager.Descent.cs.
/// </summary>
public partial class GameManager : MonoBehaviour
{
    public GameState State { get; private set; } = GameState.Start;

    /// <summary>Player may move/ping — true during normal play and while practising in the tutorial.</summary>
    public bool AcceptsInput => (State == GameState.Playing || State == GameState.Tutorial)
                                && !_ui.SettingsOpen && !_ui.TeachOpen;

    /// <summary>A UI button was just pressed, so this tap must not also count as a gameplay tap.</summary>
    public bool UiJustPressed => _ui != null && _ui.UiJustPressed;

    /// <summary>Is this screen point over the HUD readout band? See UIManager.IsOverHud.</summary>
    public bool IsOverHud(Vector2 screenPos) => _ui != null && _ui.IsOverHud(screenPos);

    private Camera _cam;
    private PlayerController _player;
    private SonarManager _sonar;
    private UIManager _ui;
    private ProceduralAudio _audio;
    private WallShaderController _wallCtrl;
    private FxManager _fx;
    private SpriteRenderer _exitSR;
    private SpriteRenderer _exitRingSR;
    private Transform _vignette;

    // Progress.
    private int _level = 1;
    private int _hull;         // plates left for THIS level: 3 at the start, 0 fails it
    private int _pings, _pingsStart;
    private int _failStreak;   // consecutive fails on the current level (rubber-banding)
    private bool _assisted;    // this attempt has the rubber band's extra clock and reveals

    // What this attempt will be graded on (see GameConfig.GradeFor).
    private bool _damageTaken;
    private int _revealsUsed;
    private float _levelElapsed;   // real seconds played, for the Daily's shareable time

    // Level state.
    private MazeData _maze;
    private Difficulty _profile;
    private float _levelTimer;
    private float _pingReadyTime;   // ping cooldown gate
    private int _lastTickSecond;    // for timer audio cues

    // Moving exit.
    private bool _movingExit;
    private Vector2Int _exitCell;
    private Vector2 _exitWorld;
    private float _exitMoveTimer;
    private readonly List<Vector2Int> _nbrScratch = new List<Vector2Int>(4);

    // Exit beacon: ripples that leave the exit on every pip of its sound.
    private SpriteRenderer[] _exitRipples;
    private float[] _rippleAge;
    private int _nextRipple;
    private float _beaconT;

    // Decoys — anglerfish lures.
    private SpriteRenderer[] _decoySR;      // the lit lure, swaying on its stalk
    private SpriteRenderer[] _decoyRingSR;  // the jaws behind it, shown by the sonar reveal
    private Vector2[] _decoyPos;
    private float[] _decoyHideUntil;
    private float[] _decoyPhase;
    private int _decoyCount;

    // Bonus Echo orb (variable reward) — only visible while the sonar sweep is over it.
    private SpriteRenderer _orbSR;
    private Vector2 _orbPos;
    private bool _orbActive;
    /// <summary>
    /// Cells on the corridor from the start to the orb. The moving exit is forbidden from
    /// entering these: a perfect maze has exactly one route to any cell, so the exit parking on
    /// that route puts the orb behind the destination and makes it impossible to collect.
    /// </summary>
    private readonly HashSet<Vector2Int> _orbCorridor = new HashSet<Vector2Int>();

    // Daily streak (set once at StartGame).
    private int _dayStreak;
    /// <summary>Armed when a run begins; the next level built spends the consecutive-day reveals.</summary>
    private bool _dailyBonusPending;
    private bool _isDaily;
    private bool _dailyPractice;   // a Daily run after today's one scored attempt was used
    private TutorialController _tutorial;

    // Camera fx.
    private Vector3 _camBase;
    private float _baseOrthoSize;
    private float _shakeTimer;
    private float _punchTimer;

    public void Init(Camera cam, PlayerController player, SonarManager sonar, UIManager ui,
                     ProceduralAudio audio, WallShaderController wallCtrl, FxManager fx,
                     SpriteRenderer exitSR, Transform vignette)
    {
        _cam = cam; _player = player; _sonar = sonar; _ui = ui; _audio = audio;
        _wallCtrl = wallCtrl; _fx = fx; _exitSR = exitSR; _vignette = vignette;

        _tutorial = gameObject.AddComponent<TutorialController>();

        // Decoy pool. Each is a lure — a lit bulb on a curved stalk — with a ring of jaws behind it
        // that only the sonar shows. They were plain orange glows, which under the commonest
        // colour blindness were the same khaki as the exit: the shape now says "bait" on its own.
        var ballSprite = VisualUtils.Lure();
        var ringSprite = VisualUtils.Jaws();
        var container = new GameObject("Decoys").transform;
        container.SetParent(transform, false);
        _decoySR = new SpriteRenderer[GameConfig.MaxDecoys];
        _decoyRingSR = new SpriteRenderer[GameConfig.MaxDecoys];
        _decoyPos = new Vector2[GameConfig.MaxDecoys];
        _decoyHideUntil = new float[GameConfig.MaxDecoys];
        _decoyPhase = new float[GameConfig.MaxDecoys];
        for (int i = 0; i < GameConfig.MaxDecoys; i++)
        {
            var go = new GameObject("Decoy" + i);
            go.transform.SetParent(container, false);
            var sr = go.AddComponent<SpriteRenderer>();
            VisualUtils.SetAdditive(sr, ballSprite);
            sr.color = GameConfig.DecoyColor; sr.sortingOrder = 28;
            go.transform.localScale = Vector3.one * (GameConfig.CellSize * 0.7f);
            go.SetActive(false);
            _decoySR[i] = sr;

            // Hollow highlight ring, drawn a touch larger, sitting around the ball.
            var rgo = new GameObject("DecoyRing" + i);
            rgo.transform.SetParent(container, false);
            var rsr = rgo.AddComponent<SpriteRenderer>();
            VisualUtils.SetAdditive(rsr, ringSprite);
            rsr.color = GameConfig.DecoyRingColor; rsr.sortingOrder = 29;
            // Bigger than the ball so the outline sits clearly AROUND it (ring ~0.86*scale across).
            rgo.transform.localScale = Vector3.one * (GameConfig.CellSize * 1.35f);
            rgo.SetActive(false);
            _decoyRingSR[i] = rsr;
        }

        // Bonus Echo — a gold crystal hidden in a dead-end, findable only by pinging. A cut gem
        // that spins, where the decoy is a round light that sways: under protanopia the two were
        // the same yellow-brown, and the thing that cracks your hull looked like the thing that
        // mends it.
        var orbGO = new GameObject("BonusOrb");
        orbGO.transform.SetParent(transform, false);
        _orbSR = orbGO.AddComponent<SpriteRenderer>();
        VisualUtils.SetAdditive(_orbSR, VisualUtils.Crystal());
        _orbSR.color = GameConfig.BonusOrbColor;
        _orbSR.sortingOrder = 31;
        orbGO.SetActive(false);

        // Exit reticle — a turning dashed ring, so the destination reads as a target by shape.
        var exitRingGO = new GameObject("ExitRing");
        exitRingGO.transform.SetParent(transform, false);
        _exitRingSR = exitRingGO.AddComponent<SpriteRenderer>();
        VisualUtils.SetAdditive(_exitRingSR, VisualUtils.DashedRing());
        _exitRingSR.color = GameConfig.ExitColor;
        _exitRingSR.sortingOrder = 29;

        // Beacon ripples: one leaves the exit on every pip of its sound, so the beacon can be
        // seen as well as heard — and the two always agree.
        _exitRipples = new SpriteRenderer[3];
        _rippleAge = new float[3];
        for (int i = 0; i < _exitRipples.Length; i++)
        {
            var rgo = new GameObject("ExitRipple" + i);
            rgo.transform.SetParent(transform, false);
            var rsr = rgo.AddComponent<SpriteRenderer>();
            VisualUtils.SetAdditive(rsr, VisualUtils.Ring());
            rsr.color = new Color(0f, 0f, 0f, 0f);
            rsr.sortingOrder = 27;
            _exitRipples[i] = rsr;
            _rippleAge[i] = -1f;
        }

        InitHazards();
    }

    /// <summary>
    /// Hide or restore every gameplay object that draws itself independently of the sonar reveal:
    /// the player dot (plus its halo and trail), the exit, the decoys and the bonus orb.
    ///
    /// The walls fade out with the reveal, but these all glow on their own — so on the fail screen
    /// they carried on shining through the dim as bright unexplained blobs, right next to the text
    /// telling the player the level was over. Renderers are disabled rather than the GameObjects,
    /// so positions, coroutines and pulse animations survive untouched for the retry.
    /// </summary>
    private void SetGameplayVisuals(bool on)
    {
        if (_playerRenderers == null && _player != null)
            _playerRenderers = _player.GetComponentsInChildren<Renderer>(true);

        if (_playerRenderers != null)
            for (int i = 0; i < _playerRenderers.Length; i++)
                if (_playerRenderers[i] != null) _playerRenderers[i].enabled = on;

        if (_exitSR != null) _exitSR.enabled = on;
        if (_exitRingSR != null) _exitRingSR.enabled = on;
        if (_orbSR != null) _orbSR.enabled = on;
        if (_exitRipples != null)
            for (int i = 0; i < _exitRipples.Length; i++) _exitRipples[i].enabled = on;

        if (_decoySR != null)
            for (int i = 0; i < _decoySR.Length; i++)
            {
                if (_decoySR[i] != null) _decoySR[i].enabled = on;
                if (_decoyRingSR[i] != null) _decoyRingSR[i].enabled = on;
            }
        SetHazardVisuals(on);
    }

    private Renderer[] _playerRenderers;

    private Color _sectorColor = GameConfig.WallGlowColor;

    /// <summary>
    /// Tint the ping ring and every revealed wall. STANDARD keeps each sector's own palette; any
    /// earned sonar colour replaces it everywhere the sonar draws.
    ///
    /// The setting used to recolour only the dot, which is a few pixels of a dark screen: players
    /// switched colours mid-level and saw nothing change. The ping and the walls it lights ARE the
    /// sonar, so that is where the reward has to show. Applied immediately, mid-level included.
    /// </summary>
    private void ApplyWorldPalette()
    {
        Color c = SaveData.SonarColour > 0 ? GameConfig.SonarColour(SaveData.SonarColour) : _sectorColor;
        _wallCtrl.SetGlowColor(c);
        _sonar.SetRingColor(c);
    }

    /// <summary>Show the main menu. Nothing runs until the player presses PLAY or DAILY.</summary>
    public void StartGame()
    {
        SaveData.ApplySettings();
        _audio.ApplyMix();
        _player.SetColour(GameConfig.SonarColour(SaveData.SonarColour));

        // Every way into a level goes through the one-time notification prompt first.
        _ui.OnPlay = () => AfterFirstRunPrompt(BeginEndlessRun);
        _ui.OnDaily = () => AfterFirstRunPrompt(BeginDailyRun);
        _ui.OnDescent = () => AfterFirstRunPrompt(BeginDescent);
        _ui.OnResetLevel = ResetLevel;
        _ui.OnHome = GoHome;
        _ui.OnLevelChosen = level => AfterFirstRunPrompt(() => BeginRunAtLevel(level));
        _ui.OnSonarColour = c => { _player.SetColour(c); ApplyWorldPalette(); };
        _ui.Audio = _audio;     // lets the UI layer make noise without reaching for a singleton

        ReturnToMenu();
    }

    private bool _promptInFlight;
    private bool _menuPreview;          // BuildLevel is only dressing the menu's backdrop

    /// <summary>
    /// The notification permission is asked exactly once, the first time the player taps into a
    /// level — PLAY, DAILY or the level wheel, whichever comes first. Before that tap the app has
    /// shown nothing worth being reminded about; after it the player is committed enough that the
    /// dialog reads as part of setting up rather than an ambush on a cold launch.
    ///
    /// The run does not start until the dialog is gone. Android pauses us while it is up, so a
    /// level started underneath would sit frozen with its tutorial half-drawn; and the answer is
    /// reported on the OS thread, so this waits on a flag from a coroutine rather than starting
    /// anything from the callback.
    /// </summary>
    private void AfterFirstRunPrompt(System.Action start)
    {
        if (_promptInFlight) return;                 // double-tap while the dialog is opening
        if (SaveData.NotifAsked || !GameNotifications.CanPrompt) { start(); return; }

        SaveData.MarkNotifAsked();                   // one ask, ever — even if the app dies mid-dialog
        StartCoroutine(PromptThenStart(start));
    }

    private System.Collections.IEnumerator PromptThenStart(System.Action start)
    {
        _promptInFlight = true;
        var request = GameNotifications.BeginPermissionRequest();
        // Unscaled and capped: the menu is not paused, and if the OS never answers (a dismissed
        // dialog on some skins reports nothing) the player must still get their level.
        float waited = 0f;
        while (!request.IsDone && waited < 30f)
        {
            GameNotifications.PollIOS();
            waited += Time.unscaledDeltaTime;
            yield return null;
        }
        _promptInFlight = false;
        start();
    }

    private void ReturnToMenu()
    {
        if (_tutorial != null) _tutorial.Hide();   // never let the tutorial overlay sit on the menu
        _ui.HideTeachCard();                       // nor a callout from the run we just left
        _ui.HideFailPanel();                       // nor a fail screen we never pressed RETRY on
        _ui.HideResult();
        _levelBusy = false;
        _ui.SetResetInteractable(true);
        _isDaily = false;
        _isDescent = false;
        _failStreak = 0;
        _level = SaveData.CurrentLevel;     // the menu previews where they'll pick up
        _dayStreak = SaveData.LiveStreak;   // as it stands today, not as last written
        Time.timeScale = 1f;

        _menuPreview = true;                // no sector banner or riser over the menu
        BuildLevel(_level);                 // the maze's walls stay faintly alive behind the menu...
        _menuPreview = false;
        // ...but its glows do not. The exit ring and the player's halo sat on top of the
        // SONARFALL logo on a fresh install, reading as stray UI. Every way into a level turns
        // them back on.
        SetGameplayVisuals(false);
        _audio.SilenceWorld();
        State = GameState.Start;
        _ui.ShowStart(_level, _dayStreak, SaveData.DailyDone, SaveData.RunFinished);
    }

    /// <summary>Normal endless progression — resume wherever the player left off.</summary>
    private void BeginEndlessRun() => BeginRunAtLevel(SaveData.CurrentLevel);

    /// <summary>
    /// Start the endless run at a given level. CONTINUE and the level wheel both come through here.
    ///
    /// These used to be two near-identical methods, which is exactly how they drifted apart: the
    /// wheel's copy skipped the tutorial outright on the theory that anyone picking a level had
    /// played before — but a brand-new player's only unlocked level IS 1, so LEVELS was the first
    /// thing they could press and they got no tutorial at all. Meanwhile CONTINUE's copy tested
    /// ShouldRun with no level check, so the tutorial it never got would later ambush them on
    /// level 7. One method, one rule.
    ///
    /// Note this deliberately does NOT write the chosen level back to SaveData — dropping into
    /// level 3 for a rematch must not throw away a resume point of 47. WinRoutine only ever raises
    /// CurrentLevel, so progress can climb from here but never fall.
    /// </summary>
    private void BeginRunAtLevel(int level)
    {
        // Reachable from the fail screen's LEVELS button as well as the menu, and when it is,
        // FailRoutine is still parked on its "wait for RETRY" loop. Tear that down first: left
        // running it would sit there forever holding a stale callback, and a later failure would
        // start a second copy of the same coroutine.
        StopAllCoroutines();
        Time.timeScale = 1f;
        _ui.HideFailPanel();
        SetGameplayVisuals(true);       // FailRoutine hides these; it is not around to restore them
        _ui.SetCover(0f);
        _levelBusy = false;
        _ui.SetResetInteractable(true);

        _isDaily = false;
        _isDescent = false;
        _failStreak = 0;
        _hull = GameConfig.StartHull;                // a new run never inherits a carried gold plate
        _level = Mathf.Max(1, level);
        _dayStreak = SaveData.RegisterDailyVisit();  // opening the game counts toward the streak
        _dailyBonusPending = true;                   // spent by the first level this session builds

        // The tutorial belongs to level 1 and nowhere else. It teaches "drag, ping, here is the
        // exit" — none of which is news to someone starting level 12, and all of which is baffling
        // as a mid-ladder interruption.
        bool tutorialPending = _tutorial != null && _tutorial.ShouldRun;
        bool runTutorial = tutorialPending && _level == 1;

        // Starting above level 1 with the tutorial still unseen means the player has demonstrably
        // moved past needing it. Retire it now rather than leaving it armed to fire later.
        if (tutorialPending && !runTutorial) SaveData.MarkHintSeen();

        BuildLevel(_level);
        _ui.HideStart();
        _ui.SetRetryVisible(true);
        State = runTutorial ? GameState.Tutorial : GameState.Playing;
        if (runTutorial)
        {
            // Hide RETRY/settings for the duration. ResetLevel refuses to run in the Tutorial
            // state, so leaving the button on screen just gives a first-time player something
            // that visibly does nothing when tapped.
            _ui.ShowInGameGear(false);
            _tutorial.Begin(this, _ui, _player);
        }
        else
        {
            _ui.ShowInGameGear(true);
            // Beginner on level 1 who is NOT getting the tutorial — almost always someone who
            // quit part way through it. They may never have been shown the ping, so arm the same
            // nudge the tutorial hands over to. Anyone who has finished a level once is past this.
            if (_level == 1 && !SaveData.RunFinished)
            {
                _pingNudgeArmed = true;
                _pingNudgeT = 0f;
            }
        }
    }

    /// <summary>
    /// The Daily Maze: one fixed layout per day, a single attempt, its own result screen.
    /// Deliberately a separate mode so the ritual is visible — a daily nobody knows about
    /// retains nobody.
    ///
    /// The first run of the day is the one that counts. Later runs are practice: the layout is
    /// fixed, so a replay is a rehearsal, and scoring one would let a player retry until they
    /// could share a perfect result. Practice records nothing, shares nothing and says so.
    /// </summary>
    private void BeginDailyRun()
    {
        StopAllCoroutines();
        Time.timeScale = 1f;
        _isDaily = true;
        _isDescent = false;
        _dailyPractice = SaveData.DailyAttempted;
        if (!_dailyPractice) SaveData.MarkDailyAttempted();
        _level = 1; _failStreak = 0;
        _hull = GameConfig.StartHull;     // the daily is a standalone maze, no carry-over
        _dayStreak = SaveData.RegisterDailyVisit();

        SetGameplayVisuals(true);
        BuildLevel(_level);
        _ui.HideStart();
        _ui.SetRetryVisible(_dailyPractice);   // RETRY replays this same maze: never on the scored run
        State = GameState.Playing;
        _ui.ShowBanner(_dailyPractice
                           ? "DAILY  #" + SaveData.DailyNumber + "\n<size=55%>PRACTICE   ·   not scored</size>"
                           : "DAILY MAZE  #" + SaveData.DailyNumber + "\n<size=55%>ONE ATTEMPT</size>",
                       new Color(1f, 0.85f, 0.4f, 1f), 1.0f);
    }

    private void BuildLevel(int level)
    {
        _pingNudgeArmed = false;   // never let the first-level nudge leak into a later level
        _profile = _isDescent ? GameConfig.GetDescentDifficulty(_descentIndex, _descentSeed)
                              : GameConfig.GetDifficulty(level, _failStreak);
        _assisted = !_isDaily && !_isDescent && _failStreak >= GameConfig.RubberBandFails;

        // The daily run uses the date seed so every player worldwide gets the same layout today.
        int seed = _isDaily ? SaveData.DailySeed : Random.Range(1, int.MaxValue);
        // On the tutorial layout the spawn must open sideways — see MazeGenerator.horizontalStart.
        // Gated on level 1 as well as ShouldRun, matching where the tutorial can actually run.
        bool tutorialLayout = !_isDaily && !_isDescent && level == 1 && _tutorial != null && _tutorial.ShouldRun;
        _maze = MazeGenerator.Generate(_profile.mazeSize, GameConfig.CellSize, seed, tutorialLayout);

        // Per-sector palette: re-tint walls and the ping ring so each chapter reads differently.
        // A dive changes colour every five mazes in the same order, so depth is visible too.
        Color sectorColor = _isDescent
            ? GameConfig.SectorWallColors[(_descentIndex / GameConfig.LevelsPerSector) % GameConfig.SectorWallColors.Length]
            : GameConfig.SectorWallColor(level);
        _sectorColor = sectorColor;
        ApplyWorldPalette();

        _wallCtrl.Build(_maze);
        _sonar.SetWalls(_maze.walls);
        _sonar.ApplyProfile(_profile);
        _sonar.SetMazeDiagonal(new Vector2(_maze.worldWidth, _maze.worldHeight).magnitude);
        _sonar.ResetPings();

        _player.PlaceAt(_maze.startPos);

        _exitCell = _maze.exitCell;
        _exitWorld = _maze.exitPos;
        _exitSR.transform.position = new Vector3(_exitWorld.x, _exitWorld.y, 0f);
        _exitSR.transform.localScale = Vector3.one * (GameConfig.CellSize * 0.8f);
        _exitRingSR.transform.position = new Vector3(_exitWorld.x, _exitWorld.y, 0f);
        for (int i = 0; i < _exitRipples.Length; i++)
        {
            _rippleAge[i] = -1f;
            _exitRipples[i].color = new Color(0f, 0f, 0f, 0f);
        }
        _beaconT = 0.6f;     // first pip shortly after the level appears

        _movingExit = _profile.movingExit;
        _exitMoveTimer = _profile.exitMoveInterval;
        _exitHasMoved = false;

        // The attempt starts clean: graded on what happens from here.
        _damageTaken = false;
        _revealsUsed = 0;
        _levelElapsed = 0f;
        _clockPulsedThisLevel = false;

        PlaceDecoys(_profile.decoyCount);
        PlaceBonusOrb();
        PlaceHazards();

        // Daily-streak reward: extra reveals on your first level of the day.
        // Keyed to the first level of a SESSION, not to level 1.
        //
        // This used to read `level == 1`, which was correct when every run started there. Once
        // progress began persisting, a returning player resumed on level 24 and never saw level 1
        // again — so the reward for playing on consecutive days silently became unreachable for
        // everyone past their first session. The streak still counted; it just paid nothing.
        int dailyBonus = _dailyBonusPending
            ? Mathf.Min(GameConfig.DailyBonusPingsMax, Mathf.Max(0, _dayStreak - 1))
            : 0;
        _dailyBonusPending = false;
        _pingsStart = _profile.pings + dailyBonus;
        _pings = _pingsStart;
        _ui.BuildPingDots(_pingsStart);
        _ui.SetPingsRemaining(_pings);
        // The Daily is a standalone one-maze challenge, not level 1 of a run, so it gets its
        // own header instead of borrowing the endless run's level/sector readout.
        if (_isDaily) _ui.SetDailyHeader(_dailyPractice);
        else if (_isDescent) SetDescentHud(sectorColor);
        else
        {
            _ui.SetLevel(level);
            _ui.SetSector(GameConfig.SectorName(level), GameConfig.LevelInSector(level),
                          GameConfig.LevelsPerSector, sectorColor, _assisted);
        }
        if (_isDescent)
        {
            // One hull for the whole dive: it carries from maze to maze, cracks and all.
            _hull = Mathf.Clamp(_hull, 0, GameConfig.ReinforcedHull);
        }
        else
        {
            // Every level starts with a full hull — but a gold reinforced plate you are still
            // holding CARRIES FORWARD. Wiping it at the level boundary made it a one-level
            // trinket; letting it ride turns finding an echo into something you protect.
            _hull = Mathf.Clamp(Mathf.Max(_hull, GameConfig.StartHull),
                                GameConfig.StartHull, GameConfig.ReinforcedHull);
        }
        _ui.SetHull(_hull, GameConfig.MaxHull);

        // Announce a new sector as you enter it, with the trailer's rising sweep under it.
        if (!_isDaily && !_isDescent && !_menuPreview && GameConfig.LevelInSector(level) == 1 && level > 1)
        {
            _ui.ShowBanner("SECTOR " + (GameConfig.SectorIndex(level) + 1) + "\n" + GameConfig.SectorName(level),
                           sectorColor, 1.3f);
            _audio.PlayRiser();
        }

        _levelTimer = _profile.timeLimit;
        // Push the clock to the HUD immediately. It used to be written only from TickPlaying, so
        // during the tutorial — where TickPlaying never runs — the "THE CLOCK" card pointed at a
        // readout still showing the previous level's value, or nothing at all.
        _ui.SetTimer(_profile.timeLimit > 0f ? Mathf.CeilToInt(_profile.timeLimit) : -1);
        _pingReadyTime = 0f;
        _lastTickSecond = int.MaxValue;
        FitCamera(_maze);
    }

    /// <summary>
    /// Maybe hide a bonus orb in a dead-end. It only appears some of the time (uncertain rewards
    /// are far more compelling than guaranteed ones) and is invisible until a ping sweeps over it,
    /// so every ping carries a small "did I find gold?" thrill.
    /// </summary>
    private void PlaceBonusOrb()
    {
        _orbActive = false;
        _orbCorridor.Clear();
        _orbSR.gameObject.SetActive(false);

        // Only dead ends reachable without crossing the exit — otherwise the orb sits "behind"
        // the destination and the level ends the moment you go for it.
        var ends = _maze.reachableDeadEnds;
        if (ends == null || ends.Count == 0) return;
        if (Random.value > GameConfig.BonusOrbChance) return;

        // Prefer a dead-end away from the start so it's a real detour decision.
        float minDistSqr = (GameConfig.CellSize * 2.5f) * (GameConfig.CellSize * 2.5f);
        for (int attempt = 0; attempt < 12; attempt++)
        {
            var cell = ends[Random.Range(0, ends.Count)];
            Vector2 pos = _maze.CellCenter(cell.x, cell.y);
            if ((pos - _maze.startPos).sqrMagnitude < minDistSqr) continue;

            // Record the route to it and fence the moving exit out of those cells. Placement
            // already avoids dead ends behind the exit's STARTING cell, but from level 6 the exit
            // drifts — and stepping onto this corridor strands the orb exactly as if it had been
            // placed behind the exit in the first place. That is the "sometimes unreachable" case.
            _orbCorridor.Clear();
            var route = MazeGenerator.SolvePath(_maze.cells, _maze.size, new Vector2Int(0, 0), cell);
            if (route != null)
                for (int r = 0; r < route.Count; r++) _orbCorridor.Add(route[r]);

            _orbPos = pos;
            _orbActive = true;
            _orbSR.transform.position = new Vector3(pos.x, pos.y, 0f);
            _orbSR.transform.localScale = Vector3.one * (GameConfig.CellSize * 0.5f);
            _orbSR.color = new Color(GameConfig.BonusOrbColor.r, GameConfig.BonusOrbColor.g, GameConfig.BonusOrbColor.b, 0f);
            _orbSR.gameObject.SetActive(true);
            return;
        }
    }

    private void UpdateBonusOrb()
    {
        if (!_orbActive) return;

        // Lit by the sonar exactly like the walls, with a gentle pulse on top — and spinning, its
        // width folding through |cos|, so it reads as a turning gem rather than as a light.
        float reveal = _sonar.RevealAt(_orbPos);
        float pulse = 0.75f + 0.25f * Mathf.Sin(Time.time * GameConfig.BonusOrbPulseSpeed);
        var c = GameConfig.BonusOrbColor;
        c.a = Mathf.Clamp01(reveal * pulse * 1.3f);
        _orbSR.color = c;
        float size = GameConfig.CellSize * (0.55f + 0.15f * reveal);
        float spin = 0.25f + 0.75f * Mathf.Abs(Mathf.Cos(Time.time * 2.2f));
        _orbSR.transform.localScale = new Vector3(size * spin, size, 1f);

        // Collect on contact (whether or not it happens to be lit at that instant).
        if (SweptHit(_orbPos, GameConfig.BonusOrbRadius))
            CollectBonusOrb();
    }

    private void CollectBonusOrb()
    {
        _orbActive = false;
        _orbSR.gameObject.SetActive(false);

        // Always worth taking, at every hull level. A reward that sometimes pays nothing trains
        // players to ignore it, so there are three outcomes and never a null one:
        //
        //   damaged            -> repair a plate
        //   full (3)           -> a fourth, GOLD reinforced plate
        //   already gold (4)   -> +1 reveal, since a second gold plate has nowhere to go
        //
        // The last case matters because the gold plate carries between levels, so arriving at a
        // level already holding one is common rather than exotic.
        if (_hull >= GameConfig.ReinforcedHull)
        {
            _pings++;
            _ui.SetPingsRemaining(_pings);
            _ui.ShowBanner("BONUS ECHO\n+1 REVEAL", GameConfig.BonusOrbColor, 0.9f);
            _audio.PlayReinforced();
            _ui.Flash(0.3f);
        }
        else if (_hull >= GameConfig.MaxHull)
        {
            _hull = GameConfig.ReinforcedHull;
            _ui.SetHull(_hull, GameConfig.MaxHull);
            _ui.ShowBanner("REINFORCED\n+1 GOLD PLATE", GameConfig.BonusOrbColor, 0.9f);
            _audio.PlayReinforced();
            _ui.Flash(0.35f);
            _shakeTimer = Mathf.Max(_shakeTimer, 0.12f);
            TeachHud("hull");
        }
        else
        {
            _hull = Mathf.Min(GameConfig.MaxHull, _hull + GameConfig.BonusOrbHull);
            _ui.SetHull(_hull, GameConfig.MaxHull);
            _ui.ShowBanner("BONUS ECHO\nHULL REPAIRED", GameConfig.BonusOrbColor, 0.7f);
            _audio.PlayHullRepaired();
            TeachHud("hull");
        }

        _ui.FlashColor(GameConfig.BonusOrbColor, 0.3f);
        _fx.PlayExitBurst(_orbPos);
        Haptics.Success();
    }

    private void PlaceDecoys(int count)
    {
        _decoyCount = 0;
        for (int i = 0; i < GameConfig.MaxDecoys; i++)
        {
            _decoySR[i].gameObject.SetActive(false);
            _decoyRingSR[i].gameObject.SetActive(false);
        }

        var path = _maze.solutionPath;
        if (path == null || path.Count < 4 || count <= 0) return;

        // Place decoys ON the route the player has to take, spaced along it, avoiding the
        // first couple of cells (near start) and the exit cell.
        int lo = 2;
        int hi = path.Count - 2;               // exclusive of exit
        if (hi <= lo) return;
        int slots = Mathf.Min(count, GameConfig.MaxDecoys);

        int placed = 0;
        int lastIdx = -1;
        for (int k = 1; k <= slots; k++)
        {
            int idx = Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(lo, hi, k / (float)(slots + 1))), lo, hi);

            // Never stack two decoys in one cell. On a short solution path the even spacing
            // rounds several slots onto the same index — with a path of 8 cells and 5 decoys the
            // indices come out 3,3,4,5,5. Two decoys sharing a cell used to be cosmetic; now that
            // a hit costs a plate it is a trap: you crack one, rewind, and the twin is still
            // sitting there waiting to take the next one.
            if (idx <= lastIdx) idx = lastIdx + 1;
            if (idx > hi) break;                    // ran out of room — fewer decoys, not stacked ones
            lastIdx = idx;

            var cell = path[idx];
            Vector2 pos = _maze.CellCenter(cell.x, cell.y);

            _decoyPos[placed] = pos;
            _decoyHideUntil[placed] = 0f;

            // Blink offset. Below DecoyGateLevel every decoy is independent; from there they are
            // paired into GATES — the second of each pair sits exactly half a cycle behind the
            // first, so the two take turns and never clear together. Pairs are consecutive along
            // the solution path, which means a gate always occupies one stretch of corridor
            // rather than two unrelated corners of the maze.
            bool gates = _level >= GameConfig.DecoyGateLevel;
            bool secondOfPair = gates && (placed % 2) == 1;
            _decoyPhase[placed] = secondOfPair
                ? _decoyPhase[placed - 1] + Mathf.PI
                : Random.Range(0f, Mathf.PI * 2f);
            var transparent = new Color(GameConfig.DecoyColor.r, GameConfig.DecoyColor.g, GameConfig.DecoyColor.b, 0f);

            var sr = _decoySR[placed];
            sr.transform.position = new Vector3(pos.x, pos.y, 0f);
            sr.color = transparent;
            sr.gameObject.SetActive(true);

            var ring = _decoyRingSR[placed];
            ring.transform.position = new Vector3(pos.x, pos.y, 0f);
            ring.color = transparent;
            ring.gameObject.SetActive(true);
            placed++;
        }
        _decoyCount = placed;
    }

    /// <summary>Called by the tutorial when every step is done — hands control back to play.</summary>
    public void OnTutorialComplete()
    {
        if (State != GameState.Tutorial) return;
        State = GameState.Playing;
        _ui.ShowInGameGear(true);   // hidden while the tutorial ran; the buttons work from here
        // Arm the ping nudge. Level 1 is small enough to solve by feeling along the walls, and
        // testers who did that finished their first maze having never fired a ping voluntarily —
        // they learned the button existed and not that it was the whole game.
        _pingNudgeArmed = true;
        _pingNudgeT = 0f;
    }

    // ---- first-level ping nudge ----
    private bool  _pingNudgeArmed;
    private float _pingNudgeT;

    /// <summary>The exit's current world position — the tutorial points its marker here.</summary>
    public Vector3 ExitWorldPos { get { return new Vector3(_exitWorld.x, _exitWorld.y, 0f); } }

    /// <summary>The gameplay camera, for UI that has to track a world position on screen.</summary>
    public Camera GameCamera { get { return _cam; } }

    /// <summary>
    /// Teach by doing: pause and say one line the moment the player first actually MEETS
    /// something — not at the top of the level that happens to contain it.
    ///
    /// The old explainer cards fired before the level began, about things the player could not
    /// yet see, and were dismissed unread. Now the decoy line waits until a lit decoy is within
    /// reach, the bonus-echo line until a ping has actually found one, the drifting-exit line
    /// until the exit has visibly moved. The thing being explained is on screen, frozen, with a
    /// marker on it.
    ///
    /// Keyed off what was really placed, never off the level number, and at most one per frame;
    /// whatever loses the race is still there to be explained a moment later.
    /// </summary>
    private void CheckFirstEncounters()
    {
        if (State != GameState.Playing || _ui.TeachOpen || _player.IsRewinding) return;
        // A pulse promises the game is not pausing, so nothing may pause under it. The tutorial's
        // own ping often lights a bonus echo, and without this its card froze the world two frames
        // after the EXIT card closed, just as TIME LEFT pulsed and the player first took control:
        // the controls looked dead until they found the card at the bottom of the screen.
        if (_ui.HudPulseBusy) return;
        Vector2 pp = _player.transform.position;

        // A decoy close enough to matter, and lit. They are the one mechanic that PUNISHES you,
        // and a player who doesn't know what they are reads the rewind as the game breaking.
        if (_decoyCount > 0 && !SaveData.TaughtDecoy)
        {
            for (int i = 0; i < _decoyCount; i++)
            {
                if (Time.time < _decoyHideUntil[i]) continue;
                if ((_decoyPos[i] - pp).magnitude > GameConfig.DecoyCalloutRadius) continue;
                if (DecoyVisibility(i) < 0.55f && _sonar.RevealAt(_decoyPos[i]) < 0.4f) continue;
                SaveData.MarkTaughtDecoy();
                ShowDecoyFrozen(i);
                _ui.ShowTeachCard("DECOY", "Touch it while it's lit and your hull cracks.",
                                  UIManager.Danger, GameConfig.DecoyColor, null,
                                  _cam, _decoyPos[i], "DECOY", VisualUtils.Lure());
                return;
            }
        }

        // Paired decoys. Same hazard, new rule — without naming it the player just meets a
        // corridor that got mysteriously harder, which reads as unfair rather than as a new idea.
        if (_decoyCount >= 2 && _level >= GameConfig.DecoyGateLevel && !_isDescent && !SaveData.TaughtGate)
        {
            Vector2 mid = (_decoyPos[0] + _decoyPos[1]) * 0.5f;
            if ((mid - pp).magnitude < GameConfig.DecoyCalloutRadius + 0.6f)
            {
                SaveData.MarkTaughtGate();
                ShowDecoyFrozen(0); ShowDecoyFrozen(1);
                _ui.ShowTeachCard("PAIRED DECOYS", "They take turns. Cross in the gap.",
                                  UIManager.Danger, GameConfig.DecoyColor, null,
                                  _cam, new Vector3(mid.x, mid.y, 0f), "PAIR", VisualUtils.Lure());
                return;
            }
        }

        // The exit's first move. Without a word the player pings, commits to a route and arrives
        // at an empty cell — which reads as the game cheating. Said the moment it visibly drifts.
        if (_movingExit && _exitHasMoved && !SaveData.TaughtMovingExit)
        {
            SaveData.MarkTaughtMovingExit();
            _ui.ShowTeachCard("THE EXIT DRIFTS", "It moves between cells. Watch where it goes.",
                              UIManager.Accent, GameConfig.ExitColor, null,
                              _cam, ExitWorldPos, "EXIT", VisualUtils.DashedRing());
            return;
        }

        // The bonus echo, the moment a ping finds it — the only moment it is visible at all.
        if (_orbActive && !SaveData.TaughtOrb && _sonar.RevealAt(_orbPos) > 0.35f)
        {
            SaveData.MarkTaughtOrb();
            var c = GameConfig.BonusOrbColor; c.a = 1f;
            _orbSR.color = c;                       // frozen at full brightness under the callout
            _ui.ShowTeachCard("BONUS ECHO", "Reach it to repair a hull plate.",
                              UIManager.Gold, GameConfig.BonusOrbColor, null,
                              _cam, new Vector3(_orbPos.x, _orbPos.y, 0f), "BONUS", VisualUtils.Crystal());
            return;
        }

        CheckRuleEncounters(pp);
    }

    /// <summary>Hold a decoy fully lit, jaws showing, while a callout freezes the world on it.</summary>
    private void ShowDecoyFrozen(int i)
    {
        if (i < 0 || i >= _decoyCount) return;
        var c = GameConfig.DecoyColor; c.a = GameConfig.DecoyMaxAlpha;
        _decoySR[i].color = c;
        var rc = GameConfig.DecoyRingColor; rc.a = 1f;
        _decoyRingSR[i].color = rc;
    }

    /// <summary>
    /// Introduce one HUD readout with a pulse, once ever: "clock", "reveals" or "hull". Replaces the
    /// tutorial cards that pointed at all three before any of them meant anything.
    /// </summary>
    private void TeachHud(string item)
    {
        if (SaveData.HudTaught(item)) return;
        SaveData.MarkHudTaught(item);
        _ui.PulseHud(item);
    }

    private bool _exitHasMoved;          // set the first time the drifting exit changes cell
    private bool _clockPulsedThisLevel;

    public void RequestPing()
    {
        // During the tutorial only the ping STEP may fire one, so the drag lesson can't be skipped.
        if (State == GameState.Tutorial)
        {
            if (_tutorial == null || !_tutorial.PingAllowed) return;
            if (Time.time < _pingReadyTime || _pings <= 0) return;
            SpendPing();
            _tutorial.NotifyPinged();
            return;
        }

        if (State != GameState.Playing) return;
        if (_ui.SettingsOpen) return;
        if (Time.time < _pingReadyTime) return;  // cooldown: can't spam — wait for the reveal to finish
        if (_pings <= 0) return;                  // out of pings, but you can still move blind

        SpendPing();
        _pingNudgeArmed = false;         // they found it on their own; say nothing
        OnPingHazards(_player.transform.position);   // hunters turn toward the sound
    }

    private float _lastPingTime = -100f;

    private void SpendPing()
    {
        _pings--;
        _revealsUsed++;
        _ui.SetPingsRemaining(_pings);
        _ui.DarkFlash();                 // brief darken so the ring burst reads as powerful
        _sonar.EmitPing(_player.transform.position);
        _pingReadyTime = Time.time + GameConfig.PingCooldown;
        _lastPingTime = Time.time;
        TeachHud("reveals");             // the counter just dropped: this is when it matters
    }

    private void Update()
    {
        switch (State)
        {
            case GameState.Start:
                return; // menu buttons drive everything from here

            case GameState.Celebrating:
                return; // fully automatic

            case GameState.Tutorial:
                // The maze is live so the player can practise, but the level timer is paused and
                // the exit can't be completed until they've learned both controls.
                if (_ui.TeachOpen) return;      // the goal callout is up — nothing moves behind it
                UpdateBonusOrb();
                PulseExit();
                TickBeacon();
                return;

            case GameState.Playing:
                // Settings and the explainer card both act as a hard pause. The explainer in
                // particular MUST stop the clock: it fires at the top of a level, and a player
                // reading it should not be spending their 45 seconds doing so.
                if (_ui.SettingsOpen || _ui.TeachOpen) return;
                TickPlaying();
                return;
        }
    }

    private void TickPlaying()
    {
        if (_player.IsRewinding) return; // world is frozen mid-rewind

        // Nudge a brand-new player who came out of the tutorial and started groping along the walls
        // instead of pinging. Fires once, only on the level the tutorial ran on, and only if they
        // genuinely have not pinged. The card pauses the clock like every other explainer, so this
        // costs them nothing.
        if (_pingNudgeArmed && !_ui.TeachOpen)
        {
            _pingNudgeT += Time.deltaTime;
            if (_pingNudgeT >= GameConfig.PingNudgeDelay)
            {
                _pingNudgeArmed = false;
                _ui.ShowTeachCardAtUI(
                    "TAP TO PING",
                    "Tap anywhere to light up the walls near you.",
                    UIManager.Accent, Color.clear,
                    null,
                    _ui.RevealsRect, "REVEALS", 165f, _ui.RevealsIconRect);
                return;
            }
        }

        _levelElapsed += Time.deltaTime;

        if (_profile.timeLimit > 0f)
        {
            // The clock introduces itself the first time it ever starts running.
            if (!_clockPulsedThisLevel) { _clockPulsedThisLevel = true; TeachHud("clock"); }

            _levelTimer -= Time.deltaTime;
            int secs = Mathf.CeilToInt(Mathf.Max(0f, _levelTimer));
            _ui.SetTimer(secs);

            // Audio cues: one heads-up at 10s, then a heartbeat for each of the last 5 seconds.
            if (secs != _lastTickSecond)
            {
                // Haptics mirror the audio: the countdown is the one cue a player must not
                // miss, and it has to land even with the phone muted in a pocket.
                if (secs == GameConfig.TimerWarnAt) { _audio.PlayTimeWarning(); Haptics.Medium(); }
                else if (secs >= 1 && secs <= GameConfig.TimerTickFrom)
                {
                    _audio.PlayHeartbeat(secs);
                    if (secs <= 3) Haptics.Heavy(); else Haptics.Medium();
                }
                _audio.SetTension(secs <= GameConfig.TimerTickFrom ? 1f : secs <= GameConfig.TimerWarnAt ? 0.35f : 0f);
                _lastTickSecond = secs;
            }

            if (_levelTimer <= 0f)
            {
                if (_isDescent) { DescentTimeout(); return; }
                StartCoroutine(FailRoutine(FailCause.Timeout));
                return;
            }
        }
        else _ui.SetTimer(-1);

        UpdateMovingExit();
        UpdateDecoys();
        if (State == GameState.Playing) UpdateHazards();

        // UpdateDecoys / UpdateHazards can end the level outright (last plate lost), and a
        // coroutine runs up to its first yield synchronously — so State is already Celebrating by
        // the time we get here. Without this the same frame could also start WinRoutine and run a
        // win and a fail at once.
        if (State != GameState.Playing) return;

        UpdateBonusOrb();
        PulseExit();
        TickBeacon();
        CheckFirstEncounters();
        if (_ui.TeachOpen) return;      // a callout just froze the world; the exit can wait

        float exitR = GameConfig.CellSize * 0.4f;
        if (SweptHit(_exitWorld, exitR))
            StartCoroutine(WinRoutine());
    }

    /// <summary>
    /// The exit's beacon. Panned toward the exit, louder, higher and faster as you close in — a
    /// player who has lost the picture can still steer by ear. A ripple leaves the exit on every
    /// pip, so the beacon is visible as well as audible.
    /// </summary>
    private void TickBeacon()
    {
        _beaconT -= Time.deltaTime;
        if (_beaconT > 0f) return;

        Vector2 d = _exitWorld - (Vector2)_player.transform.position;
        float near = Mathf.Clamp01(1f - d.magnitude / GameConfig.BeaconRange);
        _beaconT = Mathf.Lerp(GameConfig.BeaconIntervalFar, GameConfig.BeaconIntervalNear, near);
        _audio.PlayBeacon(Mathf.Clamp(d.x / 3f, -1f, 1f) * 0.85f, Mathf.Lerp(0.16f, 0.5f, near), Mathf.Lerp(0.92f, 1.22f, near));

        _rippleAge[_nextRipple] = 0f;
        _nextRipple = (_nextRipple + 1) % _exitRipples.Length;
    }

    /// <summary>
    /// Did the dot pass within <paramref name="radius"/> of <paramref name="target"/> at any point
    /// this frame? Tests the whole segment travelled, not just where the dot ended up — a fast drag
    /// applies its finger delta in one go and could otherwise skip clean over the exit.
    /// </summary>
    private bool SweptHit(Vector2 target, float radius)
    {
        Vector2 a = _player.PrevPosition;
        Vector2 b = _player.transform.position;
        Vector2 ab = b - a;
        float len2 = Vector2.Dot(ab, ab);
        Vector2 closest = len2 < 1e-8f
            ? a
            : a + ab * Mathf.Clamp01(Vector2.Dot(target - a, ab) / len2);
        return (target - closest).sqrMagnitude < radius * radius;
    }

    private void UpdateMovingExit()
    {
        if (!_movingExit) return;

        _exitMoveTimer -= Time.deltaTime;
        if (_exitMoveTimer <= 0f)
        {
            _exitMoveTimer = _profile.exitMoveInterval;
            OpenNeighbors(_exitCell, _nbrScratch);

            // Never drift onto the orb's corridor. Doing so seals the orb off behind the exit —
            // the player can see it lit by a ping and simply cannot reach it, which reads as the
            // game cheating. If every option is fenced off the exit just holds still this tick.
            if (_orbActive && _orbCorridor.Count > 0)
                for (int i = _nbrScratch.Count - 1; i >= 0; i--)
                    if (_orbCorridor.Contains(_nbrScratch[i])) _nbrScratch.RemoveAt(i);

            if (_nbrScratch.Count > 0)
            {
                _exitCell = _nbrScratch[Random.Range(0, _nbrScratch.Count)];
                _exitHasMoved = true;
            }
        }

        Vector2 target = _maze.CellCenter(_exitCell.x, _exitCell.y);
        _exitWorld = Vector2.Lerp(_exitWorld, target, Time.deltaTime * GameConfig.ExitMoveLerp);
        _exitSR.transform.position = new Vector3(_exitWorld.x, _exitWorld.y, 0f);
    }

    private void OpenNeighbors(Vector2Int c, List<Vector2Int> outList)
    {
        outList.Clear();
        if (_maze.IsOpen(c.x, c.y, MazeGenerator.N)) outList.Add(new Vector2Int(c.x, c.y + 1));
        if (_maze.IsOpen(c.x, c.y, MazeGenerator.E)) outList.Add(new Vector2Int(c.x + 1, c.y));
        if (_maze.IsOpen(c.x, c.y, MazeGenerator.S)) outList.Add(new Vector2Int(c.x, c.y - 1));
        if (_maze.IsOpen(c.x, c.y, MazeGenerator.W)) outList.Add(new Vector2Int(c.x - 1, c.y));
    }

    /// <summary>0..1 — how solid decoy <paramref name="i"/> is right now (it only bites above DecoyVisibleHit).</summary>
    private float DecoyVisibility(int i)
    {
        float wave = Mathf.Sin(Time.time * _profile.decoyFadeSpeed + _decoyPhase[i]);
        float vis = Mathf.Clamp01(wave);
        return vis * vis;
    }

    private void UpdateDecoys()
    {
        float hitRadius = GameConfig.CellSize * GameConfig.DecoyHitRadius;
        Vector2 pp = _player.transform.position;

        for (int i = 0; i < GameConfig.MaxDecoys; i++)
        {
            if (i >= _decoyCount) { _audio.SetHum(i, 0f, 0f); continue; }

            var sr = _decoySR[i];
            bool blackedOut = Time.time < _decoyHideUntil[i]; // just triggered a rewind, stays gone briefly

            // The lure (the hazard): fades in and out on its own phase — slip through the cell
            // while it's dark, and it only bites while it's lit. It sways on its stalk as it
            // glows, which no other object does.
            float vis = DecoyVisibility(i);
            float alpha = blackedOut ? 0f : vis * GameConfig.DecoyMaxAlpha;
            var c = GameConfig.DecoyColor; c.a = alpha;
            sr.color = c;
            sr.transform.localScale = Vector3.one *
                (GameConfig.CellSize * (GameConfig.DecoyDrawScale + GameConfig.DecoyDrawPulse * vis));
            sr.transform.localRotation = Quaternion.Euler(0f, 0f, 14f * Mathf.Sin(Time.time * 1.6f + _decoyPhase[i] * 1.3f));

            // The jaws behind it: lit by the SONAR as the ping front sweeps over the decoy (same
            // timing as the walls), so a ping shows you what the light is attached to.
            float reveal = blackedOut ? 0f : _sonar.RevealAt(_decoyPos[i]);
            var rc = GameConfig.DecoyRingColor; rc.a = Mathf.Clamp01(reveal * 1.25f);
            _decoyRingSR[i].color = rc;

            // It hums while it is solid — you can hear when it is safe to cross without looking.
            float dist = (_decoyPos[i] - pp).magnitude;
            float near = Mathf.Clamp01(1f - dist / 4.5f);
            _audio.SetHum(i, blackedOut ? 0f : vis * near * near * 0.6f,
                          Mathf.Clamp((_decoyPos[i].x - pp.x) / 3f, -1f, 1f) * 0.8f);

            // Unchanged: only bites while the lure is actually lit. Swept like the exit, so
            // flicking through a lit decoy can't dodge the penalty either.
            if (!blackedOut && vis > GameConfig.DecoyVisibleHit &&
                SweptHit(_decoyPos[i], hitRadius))
            {
                HitDecoy(i);
                return;
            }
        }
    }

    private void HitDecoy(int i)
    {
        // A decoy costs a plate AND time, on top of the rewind. It used to cost only position,
        // which players read as a mild inconvenience — there was no reason to time a crossing when
        // barging through cost you a couple of seconds you probably had spare.
        _levelTimer -= GameConfig.DecoyTimePenalty;
        _fx.PlayDecoyPop(_decoyPos[i]);
        _decoyHideUntil[i] = Time.time + 4f;
        _audio.SetHum(i, 0f, 0f);

        // Out of hull ends the level there and then — no rewind, because there is nothing left
        // to rewind into.
        if (DamageHull(GameConfig.DecoyColor)) return;

        _ui.ShowRewind();
        _ui.PlayRewindEffect();
        _player.TriggerRewind();             // plays the rewind sound + retrace + reveal wipe
    }

    /// <summary>
    /// Crack one hull plate, from any source. Returns true if that was the last one — the level
    /// (or the dive) has already been ended by the time this returns.
    /// </summary>
    private bool DamageHull(Color flash, int plates = GameConfig.DecoyHullCost)
    {
        _hull = Mathf.Max(0, _hull - plates);
        _damageTaken = true;                 // the NO DAMAGE star is gone for this attempt
        _ui.SetHull(_hull, GameConfig.MaxHull);
        _ui.PulseHullLost();
        TeachHud("hull");

        _audio.PlayHullLost();               // distinct from the generic "wrong" — a plate went
        Haptics.Wrong();                     // the platform's "error" pattern: unmistakably bad
        _ui.FlashColor(flash, 0.3f);
        _shakeTimer = Mathf.Max(_shakeTimer, 0.18f);

        if (_hull > 0) return false;
        StartCoroutine(FailRoutine(FailCause.NoHull));
        return true;
    }

    private void PulseExit()
    {
        float d = Vector2.Distance(_player.transform.position, _exitWorld);
        float near = Mathf.Clamp01(1f - d / GameConfig.ExitNearRadius);      // 1 when player is close
        float baseP = GameConfig.ExitPulseBaseAlpha +
                      GameConfig.ExitPulseAmp * (0.5f + 0.5f * Mathf.Sin(Time.time * GameConfig.ExitPulseSpeed));
        float alpha = baseP + GameConfig.ExitNearMaxBoost * near;             // "I'm close!" brightening
        var c = GameConfig.ExitColor; c.a = alpha;
        _exitSR.color = c;
        _exitSR.transform.localScale = Vector3.one * (GameConfig.CellSize * (0.8f + 0.25f * near));

        // Turning dashed reticle around the exit, brighter as you approach — makes the goal pop,
        // and the rotation means it reads as "the target" even to a player who can't tell its
        // green from a decoy's orange.
        float ringPulse = 0.5f + 0.5f * Mathf.Sin(Time.time * GameConfig.ExitPulseSpeed * 1.3f);
        _exitRingSR.transform.position = _exitSR.transform.position;
        _exitRingSR.transform.localScale = Vector3.one * (GameConfig.CellSize * (1.25f + 0.12f * ringPulse));
        _exitRingSR.transform.localRotation = Quaternion.Euler(0f, 0f, -Time.time * 38f);
        var rc = GameConfig.ExitColor; rc.a = 0.32f + 0.22f * ringPulse + 0.4f * near;
        _exitRingSR.color = rc;

        // Beacon ripples (started by TickBeacon in time with the pips).
        const float rippleLife = 1.5f;
        for (int i = 0; i < _exitRipples.Length; i++)
        {
            if (_rippleAge[i] < 0f) continue;
            _rippleAge[i] += Time.deltaTime;
            float k = _rippleAge[i] / rippleLife;
            if (k >= 1f) { _rippleAge[i] = -1f; _exitRipples[i].color = new Color(0f, 0f, 0f, 0f); continue; }
            _exitRipples[i].transform.position = _exitSR.transform.position;
            // Born on the reticle (Ring's line sits at a quarter of its scale) and carried well past
            // it. An ease-out curve parked them on the reticle's radius for most of their life,
            // where they filled in its dashes and hid them.
            _exitRipples[i].transform.localScale = Vector3.one * (GameConfig.CellSize * Mathf.Lerp(2.2f, 6.5f, Easing.OutQuad(k)));
            var pc = GameConfig.ExitColor; pc.a = (1f - k) * (1f - k) * (0.40f + 0.30f * near);
            _exitRipples[i].color = pc;
        }
    }

    private IEnumerator WinRoutine()
    {
        State = GameState.Celebrating;
        _audio.SilenceWorld();
        _ui.ClearBanner();                  // a banner still fading would print over the clear card

        // The grade: one star per stated rule. Reaching the exit is always one; keeping the hull
        // intact is another; spending no more than half the level's reveals is the third.
        int par = GameConfig.RevealPar(_profile.pings);
        bool noDamage = !_damageTaken;
        // An assisted run can't earn the reveal star, so it tops out at SHARP. Spending half of a
        // budget that was raised to help you is not what PERFECT means. RETRY drops the assist.
        bool underPar = !_assisted && _revealsUsed <= par;
        int grade = GameConfig.GradeFor(true, noDamage, underPar);
        bool clutch = _profile.timeLimit > 0f && _levelTimer <= GameConfig.ClutchSeconds;
        bool sectorDone = !_isDaily && !_isDescent && GameConfig.IsSectorFinale(_level);

        // Banked before anything else can interrupt the routine. firstClear is whether this was
        // the player's frontier level — the only clear that can complete a sector for the first
        // time and earn its colour.
        bool firstClear = !_isDaily && !_isDescent && _level >= SaveData.CurrentLevel;
        int oldGrade = !_isDaily && !_isDescent ? SaveData.LevelGrade(_level) : 0;
        bool improved = !_isDaily && !_isDescent && SaveData.RecordGrade(_level, grade) && oldGrade > 0;

        // Clearing a level unlocks the Daily Maze. This used to live only in FailRoutine, back when
        // the flag meant "played an endless run to its end" — which, in a score run, meant dying.
        // On a level ladder there is no run to end, so the only way to unlock the Daily was to LOSE
        // a level: clear level 1 and it stayed locked, fail it and it opened. Winning is the more
        // obvious qualification of the two.
        if (!_isDaily) SaveData.MarkRunFinished();

        // Banked here for the same reason as the grade above: this routine can still be cut short.
        // Stored at the end of it, leaving during the celebration lost the clear and reopened
        // the Daily for another go.
        bool dailyBest = _isDaily && !_dailyPractice && SaveData.CompleteDaily(grade);

        // The notification prompt used to fire here, after the first clear. It moved to the first
        // PLAY tap (AfterFirstRunPrompt): testers who had already cleared a level on an earlier
        // build never saw it, and on Android 13+ "never asked" means "never delivered".

        // ---- Immediate impact ----
        // Your own ping, coming back. The cue that used to be here was a C5-E5-G5-C6 arpeggio and
        // the praise sting 0.4s later also climbs from C5 — two ascending arpeggios that close
        // together read as one sound stuttering rather than two events. This one is a gesture
        // rather than a tune, so the sting can answer it cleanly.
        _audio.PlayExitReached();
        Haptics.Success();
        _fx.PlayExitBurst(_exitWorld);
        _ui.Flash(0.7f);
        _shakeTimer = GameConfig.ShakeDuration;
        _punchTimer = GameConfig.PunchZoomTime;

        // Standout finishes are shown INSIDE the celebration panel (title + score line) rather than
        // as a floating banner, which used to overlap the panel's own title.

        // ---- Hitstop ----
        Time.timeScale = 0f;
        yield return new WaitForSecondsRealtime(GameConfig.HitstopTime);
        Time.timeScale = 1f;

        // A dive doesn't stop for a ceremony between mazes; it has its own short beat.
        if (_isDescent) { yield return DescentClearRoutine(); yield break; }

        // ---- Celebration panel ----
        _ui.ShowCelebration(_level);
        // The world's glows go dark under the card. They are additive sprites that draw on their
        // own schedule, and the exit's pulsing ring was animating straight through the title and
        // the stars. The exit burst particles stay: they ARE the celebration.
        SetGameplayVisuals(false);
        // The Daily is one standalone maze, so "LEVEL 1 CLEAR" is meaningless there — and its
        // title must win over the sector/clutch variants, which belong to the ladder.
        if (_isDaily)
            _ui.SetCelebrationTitle("DAILY  #" + SaveData.DailyNumber + (_dailyPractice ? "\nPRACTICE" : "\nCLEAR"), UIManager.Gold);
        else if (sectorDone)
            _ui.SetCelebrationTitle("SECTOR CLEAR\n" + GameConfig.SectorName(_level), GameConfig.SectorWallColor(_level));
        else if (clutch)
            _ui.SetCelebrationTitle("CLUTCH CLEAR!", UIManager.Gold);

        // The line under the grade, in priority order: a sector's reward the first time it is
        // cleared, then a beaten best on a replay, then the next sector once it is close enough
        // to chase. Left blank otherwise so it stays an event rather than a readout.
        int nextLevel = _level + 1;
        int toNext = GameConfig.LevelsToNextSector(nextLevel);
        int sectorIdx = GameConfig.SectorIndex(_level);
        string line = "";
        if (sectorDone && firstClear && sectorIdx < GameConfig.SectorNames.Length)
        {
            // Clearing a sector earns a sonar colour named after it.
            Color earned = GameConfig.SonarColour(sectorIdx + 1);
            line = "NEW SONAR COLOUR   <color=#" + ColorUtility.ToHtmlStringRGB(earned) + ">"
                 + GameConfig.SonarColourName(sectorIdx + 1) + "</color>\n<size=70%>pick it in Settings</size>";
        }
        else if (sectorDone)
            line = SaveData.SectorStars(sectorIdx) + " / " + (GameConfig.LevelsPerSector * 3) + " STARS IN " + GameConfig.SectorName(_level);
        else if (improved)
            line = "NEW BEST ON LEVEL " + _level;
        else if (!_isDaily && toNext <= 2)
            line = GameConfig.NextSectorName(nextLevel) + "   in   " + toNext;
        _ui.SetCelebrationScoreLine(line);

        // The stars land one at a time, each with its rule underneath — earned or not — so the
        // player always knows what a missing star was for.
        yield return new WaitForSecondsRealtime(0.2f);
        _ui.ShowGrade(true, noDamage, underPar, new[]
        {
            "EXIT\n<size=72%>reached</size>",
            "NO DAMAGE\n<size=72%>" + (noDamage ? "hull intact" : "hull hit") + "</size>",
            _assisted ? "ASSISTED\n<size=72%>no reveal star</size>"
                      : "MAX " + par + " REVEALS\n<size=72%>used " + _revealsUsed + "</size>",
        });
        yield return new WaitForSecondsRealtime(UIManager.GradeRowSeconds);

        // Then the word that sums them up lands, with the sting. The gap matters: slamming it in
        // on the same frame as the stars makes both read as one blob instead of a payoff.
        Color wordCol = grade >= 3 ? UIManager.Gold : grade == 2 ? UIManager.Accent : new Color(0.75f, 0.85f, 0.95f, 1f);
        _ui.ShowPraise(GameConfig.GradeWords[grade], wordCol);
        _audio.PlayGrade(grade, sectorDone);
        Haptics.Success();
        _ui.Flash(0.25f);
        _shakeTimer = Mathf.Max(_shakeTimer, 0.14f);

        // The daily is a single maze, not a run — finish it and show its own result card.
        if (_isDaily)
        {
            yield return new WaitForSecondsRealtime(GameConfig.CelebrationTime);
            _ui.HideCelebration();
            ShowDailyCard(true, grade, dailyBest);
            State = GameState.Start;   // the card's CONTINUE takes the player back to the menu
            yield break;
        }

        // ---- One-more-level: auto-advance behind a quick fade so the swap isn't a hard cut ----
        yield return new WaitForSecondsRealtime(GameConfig.CelebrationTime);

        _ui.SetCover(1f);                                   // fade to black
        yield return new WaitForSecondsRealtime(0.22f);

        _failStreak = 0;
        _level++;
        // Only ever move the resume point FORWARD. Level select lets the player drop back to an
        // earlier maze whenever they like; writing that lower number back would quietly erase the
        // real progress, so replaying level 3 at level 47 must not reset CONTINUE to 4.
        if (_level > SaveData.CurrentLevel) SaveData.CurrentLevel = _level;
        BuildLevel(_level);                                 // camera + player swap, hidden by the cover
        SetGameplayVisuals(true);                           // back on for the new maze, still under the cover
        _ui.HideCelebration();
        State = GameState.Playing;

        _ui.SetCover(0f);                                   // fade back in on the fresh level
    }

    /// <summary>
    /// The Daily's result card. Shareable as one line — "Sonarfall Daily #59 · 9.4s · 2 pings" —
    /// which is the Wordle trick: the result is the advert. Only a scored clear gets the button:
    /// a loss is a poor advert, and practice isn't a result.
    /// </summary>
    private void ShowDailyCard(bool cleared, int grade, bool newBest, string failHead = null)
    {
        int n = SaveData.DailyNumber;
        string pings = _revealsUsed + " ping" + (_revealsUsed == 1 ? "" : "s");
        string secs = _levelElapsed.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s";
        string stars = cleared ? StarText(grade) : "";

        string big = cleared ? secs + "   ·   " + pings : failHead;
        // Words, not ★: the UI fonts have no star glyph (the card draws its stars as sprites).
        string best = SaveData.DailyBest > 0 ? "best  " + SaveData.DailyBest + "/3 stars" : "";
        string detail = _dailyPractice
            ? "Practice runs aren't scored."
            : (newBest ? "<color=#FFD15A>NEW DAILY BEST</color>\n" : "")
              + "Day streak  " + _dayStreak + (best.Length > 0 ? "      " + best : "");
        detail += cleared || _dailyPractice
            ? "\n<size=80%>A new maze unlocks tomorrow.</size>"
            : "\n<size=80%>Practice it today. A new maze unlocks tomorrow.</size>";
        string share = cleared && !_dailyPractice
            ? "Sonarfall Daily #" + n + " · " + secs + " · " + pings + " · " + stars + "\n" + GameConfig.StoreUrl
            : null;

        _ui.ShowResult((_dailyPractice ? "PRACTICE  #" : "DAILY  #") + n, UIManager.Gold, cleared ? grade : 0,
                       big, detail, share, "CONTINUE", ReturnToMenu);
    }

    /// <summary>★★☆ — for share text, where the UI's sprite stars can't go.</summary>
    private static string StarText(int grade)
    {
        var sb = new System.Text.StringBuilder(3);
        for (int i = 0; i < 3; i++) sb.Append(i < grade ? '★' : '☆');
        return sb.ToString();
    }

    /// <summary>Why a level ended — the banner says which, since the fix differs.</summary>
    private enum FailCause { Timeout, NoHull }

    /// <summary>
    /// Level failed. The maze lights up first so the player sees how close the exit actually was:
    /// "you were 2 tiles away" turns a failure into an instant retry instead of a quit.
    ///
    /// This retries the SAME level. The old behaviour dumped the player back to level 1, which
    /// made sense when a score run was the unit of play — it is exactly wrong for a ladder of
    /// puzzles, where losing level 24 and restarting at 1 discards an hour of real progress.
    /// </summary>
    private IEnumerator FailRoutine(FailCause cause)
    {
        State = GameState.Celebrating;      // reuse the "cutscene" state: input + ticking are off
        _audio.SilenceWorld();

        _failStreak++;
        if (!_isDaily) SaveData.MarkRunFinished();   // finishing a level unlocks the Daily Maze
        _ui.Flash(0.35f);
        Haptics.Heavy();
        _audio.PlayLose();

        // Light the place up so the player can see the route they missed.
        Vector2 playerPos = _player.transform.position;
        _sonar.SetStillZones(null);                  // the post-mortem shows everything
        _sonar.RevealAll(playerPos, GameConfig.FailRevealTime);

        string head = cause == FailCause.NoHull ? "HULL BREACHED" : "OUT OF TIME";
        string detail = ProgressPhrase(playerPos);

        // Let the reveal play out on its own first: the point of lighting the maze is to show the
        // route that was missed, and a panel drawn over it defeats that.
        yield return new WaitForSecondsRealtime(GameConfig.FailRevealTime);

        // A breached hull ends a dive outright: that is the whole contract of DESCENT.
        if (_isDescent)
        {
            SetGameplayVisuals(false);
            ShowDescentCard();
            State = GameState.Start;
            yield break;
        }

        // Failing the daily ends the scored attempt but not the day. It used to call
        // CompleteDaily(0), which locked the button to "DAILY DONE" — so a player who died eight
        // seconds in had nothing left to come back for. They can still practise today's maze;
        // what they can't do is keep retrying until a run is worth sharing.
        if (_isDaily)
        {
            SetGameplayVisuals(false);
            ShowDailyCard(false, 0, false, head);
            State = GameState.Start;
            yield break;
        }

        // Now dim it all and wait. Nothing rebuilds until the player presses RETRY.
        // The glows go with the maze: once the reveal has had its moment there is nothing left to
        // read here, and a floating exit marker behind the words is just clutter.
        SetGameplayVisuals(false);
        _failRetryRequested = false;
        // Just the outcome. The level number is already in the HUD directly above and the
        // button itself says RETRY, so "Retry level 27." was restating two things already
        // on screen.
        _ui.ShowFailPanel(head, detail,
                          () => _failRetryRequested = true);

        while (!_failRetryRequested) yield return null;

        _ui.HideFailPanel();

        // Same level, brand new layout — the challenge repeats, the solution doesn't.
        _ui.SetCover(1f);
        yield return new WaitForSecondsRealtime(0.22f);
        SetGameplayVisuals(true);       // restore behind the cover, before anything is on screen
        BuildLevel(_level);
        State = GameState.Playing;
        _ui.SetCover(0f);
        if (_failStreak == GameConfig.RubberBandFails) AnnounceAssist();
    }

    /// <summary>
    /// Say what the rubber band just gave. It used to be silent, so a player who noticed a retry
    /// with more time and reveals had every reason to read it as leftover state from the last
    /// attempt. The HUD caption keeps saying ASSIST for as long as it lasts.
    /// </summary>
    private void AnnounceAssist()
    {
        var plain = GameConfig.GetDifficulty(_level, 0);
        int seconds = Mathf.RoundToInt(_profile.timeLimit - plain.timeLimit);
        _ui.ShowBanner("ASSIST ON\n<size=55%>+" + seconds + "s   ·   +" + (_profile.pings - plain.pings)
                       + " reveals   ·   no reveal star</size>", UIManager.Accent, 1.5f);
    }

    private bool _failRetryRequested;

    /// <summary>
    /// How far along the route the player actually got, 0..1, measured through the maze rather than
    /// across it.
    ///
    /// The fail screen used to report a tile count from a straight-line distance, which was wrong
    /// twice over. A player who has never SEEN the maze has no idea how big a tile is or how many
    /// there are, so "13 tiles" carried no meaning; and crow-flies distance ignores every wall in
    /// between, so it understated the real journey badly. A fraction of the route needs no spatial
    /// reference at all — it answers the only question the player is actually asking, which is
    /// "was I close?".
    /// </summary>
    private float RouteProgress(Vector2 playerPos)
    {
        if (_maze == null || _maze.cells == null) return 0f;

        int size = _maze.size;
        var here = new Vector2Int(
            Mathf.Clamp(Mathf.RoundToInt(playerPos.x / GameConfig.CellSize), 0, size - 1),
            Mathf.Clamp(Mathf.RoundToInt(playerPos.y / GameConfig.CellSize), 0, size - 1));

        var whole = MazeGenerator.SolvePath(_maze.cells, size, new Vector2Int(0, 0), _exitCell);
        var left  = MazeGenerator.SolvePath(_maze.cells, size, here, _exitCell);
        if (whole == null || whole.Count <= 1 || left == null) return 0f;

        // Counts include both endpoints, so the number of STEPS is Count - 1.
        return Mathf.Clamp01(1f - (float)(left.Count - 1) / (whole.Count - 1));
    }

    /// <summary>Plain-language version of RouteProgress, for a player who cannot see the maze.</summary>
    private string ProgressPhrase(Vector2 playerPos)
    {
        float p = RouteProgress(playerPos);
        if (p >= 0.90f) return "The exit was right there.";
        if (p >= 0.70f) return "You almost had it.";
        if (p >= 0.45f) return "You were past halfway.";
        if (p >= 0.20f) return "You had a fair way still to go.";
        return "The exit was still a long way off.";
    }

    /// <summary>
    /// Regenerate the current level from scratch. Exposed as a button because a maze whose layout
    /// you have half-memorised but keep dying in is a frustrating place to be stuck, and the old
    /// escape hatch — reset ALL progress — was wildly out of proportion to the problem.
    /// </summary>
    public void ResetLevel()
    {
        if (State != GameState.Playing && State != GameState.Celebrating) return;
        // A free re-roll would let a dive skip any maze it didn't like; DESCENT hides the button,
        // and this is the backstop.
        if (_isDescent) return;
        // Same layout, full clock: on the scored Daily that is a free second attempt.
        if (_isDaily && !_dailyPractice) return;
        // Reject re-entry while a rebuild is already running. This used to StopAllCoroutines() and
        // start over unconditionally, so a second tap could kill the previous routine in the window
        // between SetCover(1f) and SetCover(0f) — the cover never came back down and the game was
        // left on a permanently black screen with no way out.
        if (_levelBusy) return;
        StopAllCoroutines();
        Time.timeScale = 1f;
        StartCoroutine(ResetLevelRoutine());
    }

    /// <summary>True while a level rebuild is in flight and the screen is covered.</summary>
    private bool _levelBusy;

    /// <summary>
    /// Regenerate the level behind a full blackout.
    ///
    /// It used to swap the maze instantly, which was indistinguishable from nothing happening —
    /// the layout is invisible anyway, so a player pressing RETRY got a black screen before and a
    /// black screen after and no confirmation the button did anything. The wipe and the callout
    /// are the feedback.
    /// </summary>
    private IEnumerator ResetLevelRoutine()
    {
        _levelBusy = true;
        _ui.SetResetInteractable(false);    // grey the button out for the duration of the wipe
        State = GameState.Celebrating;      // freeze the clock and input during the wipe
        _ui.HideCelebration();
        _ui.HideTeachCard();
        _audio.PlayWhoosh();
        Haptics.Medium();

        _ui.SetCover(1f);
        yield return new WaitForSecondsRealtime(0.28f);

        _failStreak = 0;
        BuildLevel(_level);
        SetGameplayVisuals(true);           // a reset from the clear card must bring the glows back
        _ui.ShowBanner((_isDaily ? "PRACTICE" : "LEVEL " + _level) + "\nRESET", UIManager.Accent, 0.9f);

        yield return new WaitForSecondsRealtime(0.12f);   // hold the black a beat so it registers

        State = GameState.Playing;
        _ui.SetCover(0f);
        _levelBusy = false;
        _ui.SetResetInteractable(true);
    }

    /// <summary>Abandon the level and go back to the menu. Progress is already saved per level.</summary>
    public void GoHome()
    {
        StopAllCoroutines();
        Time.timeScale = 1f;
        _ui.HideCelebration();
        _ui.SetCover(0f);
        // Quitting a dive banks the depth reached — walking away is not worse than dying.
        if (_isDescent) SaveData.RecordDescent(_descentIndex);
        ReturnToMenu();
    }

    /// <summary>
    /// Fit the maze into the band of screen between the HUD at the top and the gear/RETRY row at
    /// the bottom, at any aspect ratio, and centre it in that band.
    ///
    /// It used to fit the maze to the whole screen. In portrait that happened to work, because a
    /// square maze is width-bound and leaves room above and below; in a landscape browser window it
    /// is height-bound, so it filled the screen top to bottom and ran under the HUD — the clock
    /// drew inside the maze's top row and the exit's ring ran into the LEVEL header.
    /// </summary>
    private void FitCamera(MazeData maze)
    {
        _cam.orthographic = true;
        _fitW = Screen.width; _fitH = Screen.height;
        float sw = Mathf.Max(1f, Screen.width), sh = Mathf.Max(1f, Screen.height);

        float topPx, bottomPx;
        _ui.GetPlayfieldInsets(out topPx, out bottomPx);
        // Never let the reserves squeeze the maze into a sliver on an extreme window shape.
        float bandH = Mathf.Max(sh * 0.45f, sh - topPx - bottomPx);
        if (bandH >= sh) { bandH = sh; bottomPx = 0f; }

        float halfH = maze.worldHeight * 0.5f + GameConfig.CameraPadding;
        float halfW = maze.worldWidth * 0.5f + GameConfig.CameraPadding;
        float ppu = Mathf.Min(bandH / (2f * halfH), sw / (2f * halfW));   // screen pixels per world unit
        _baseOrthoSize = sh / (2f * ppu);
        _cam.orthographicSize = _baseOrthoSize;

        // Shift so the maze sits in the middle of the band rather than the middle of the screen.
        float bandCentre = bottomPx + bandH * 0.5f;
        float offsetWorld = (bandCentre - sh * 0.5f) / ppu;
        _camBase = new Vector3(maze.worldCenter.x, maze.worldCenter.y - offsetWorld, -10f);
        _cam.transform.position = _camBase;
    }

    private int _fitW, _fitH;

    private void LateUpdate()
    {
        float dt = Time.unscaledDeltaTime; // survive hitstop (timeScale 0)

        // A resized window (WebGL, desktop, a rotated tablet) refits the maze to the new band.
        if (_maze != null && (Screen.width != _fitW || Screen.height != _fitH)) FitCamera(_maze);

        // Punch-zoom: snap in, ease back out.
        float size = _baseOrthoSize;
        if (_punchTimer > 0f)
        {
            _punchTimer -= dt;
            float t = 1f - Mathf.Clamp01(_punchTimer / GameConfig.PunchZoomTime);
            size = Mathf.Lerp(_baseOrthoSize * (1f - GameConfig.PunchZoom), _baseOrthoSize, Easing.OutCubic(t));
        }
        _cam.orthographicSize = size;

        // Shake.
        if (_shakeTimer > 0f)
        {
            _shakeTimer -= dt;
            float amt = GameConfig.ShakeMagnitude * Mathf.Clamp01(_shakeTimer / GameConfig.ShakeDuration);
            Vector2 off = Random.insideUnitCircle * amt;
            _cam.transform.position = _camBase + new Vector3(off.x, off.y, 0f);
        }
        else _cam.transform.position = _camBase;

        // Vignette covers the (possibly punched) viewport.
        if (_vignette != null)
        {
            float h = size * 2f;
            float w = h * _cam.aspect;
            _vignette.localScale = new Vector3(w * 1.08f, h * 1.08f, 1f);
        }
    }

#if UNITY_EDITOR
    /// <summary>
    /// Editor-only QA hook: drop straight into an arbitrary level so deep progression can be
    /// tested without playing 80 levels by hand. Compiled out of player builds entirely.
    /// </summary>
    public void DebugJumpToLevel(int level)
    {
        StopAllCoroutines();
        Time.timeScale = 1f;
        _isDaily = false;
        _isDescent = false;
        _failStreak = 0;
        _level = Mathf.Max(1, level);
        _ui.HideTeachCard();
        _ui.HideFailPanel();
        _ui.HideResult();
        SetGameplayVisuals(true);
        BuildLevel(_level);
        _ui.HideStart();
        _ui.HideCelebration();
        _ui.SetCover(0f);
        _ui.SetRetryVisible(true);
        State = GameState.Playing;
    }

    /// <summary>
    /// Editor-only QA hook: run the real timeout path immediately. Verifying the fail screen by
    /// actually waiting out a 34-second clock is not workable, and faking it by calling the UI
    /// directly would skip the reveal, the visual teardown and the RETRY gating — which is exactly
    /// the part worth testing. Compiled out of player builds.
    /// </summary>
    public void DebugFailNow()
    {
        if (State != GameState.Playing) return;
        StartCoroutine(FailRoutine(FailCause.Timeout));
    }

    /// <summary>Editor-only QA hook: clear the current level immediately, via the real win path.</summary>
    public void DebugWinNow()
    {
        if (State != GameState.Playing && State != GameState.Tutorial) return;
        StopAllCoroutines();
        StartCoroutine(WinRoutine());
    }
#endif

    private void OnApplicationPause(bool paused)
    {
        AudioListener.pause = paused; // mute cleanly when backgrounded; timer naturally halts

        // Reminders are rebuilt every time the app leaves the foreground and wiped every time it
        // returns, so they always reflect the current save and can never fire at someone who is
        // already playing.
        if (paused) GameNotifications.OnAppBackground();
        else        GameNotifications.OnAppForeground();
    }

    /// <summary>Android "back to home" and desktop quit both land here, not in OnApplicationPause.</summary>
    private void OnApplicationQuit()
    {
        GameNotifications.OnAppBackground();
    }
}
