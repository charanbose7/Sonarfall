using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public enum GameState { Start, Playing, Celebrating, Tutorial }

/// <summary>
/// The brain: level flow, ping budget, fail timer, streak + star scoring, rubber-banding,
/// the twists (moving exit, decoys), near-miss exit glow, and the whole level-clear
/// celebration (hitstop -> burst -> flash -> punch-zoom -> rolling score -> star pops ->
/// auto-advance). Everything else is handed to it by GameBootstrap.
/// </summary>
public class GameManager : MonoBehaviour
{
    public GameState State { get; private set; } = GameState.Start;

    /// <summary>Player may move/ping — true during normal play and while practising in the tutorial.</summary>
    public bool AcceptsInput => (State == GameState.Playing || State == GameState.Tutorial)
                                && !_ui.SettingsOpen && !_ui.TeachOpen;

    /// <summary>A UI button was just pressed, so this tap must not also count as a gameplay tap.</summary>
    public bool UiJustPressed => _ui != null && _ui.UiJustPressed;

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
    private int _stars;        // lives for THIS level: 3 at the start, 0 fails it
    private int _pings, _pingsStart;
    private int _failStreak;   // consecutive fails on the current level (rubber-banding)

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

    // Decoys.
    private SpriteRenderer[] _decoySR;      // pulsing hazard ball
    private SpriteRenderer[] _decoyRingSR;  // hollow highlight ring, lit by the sonar reveal
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

        // Decoy pool.
        var decoyMat = new Material(Shader.Find("Sonarfall/Additive")) { name = "DecoyMat" };
        var ballSprite = VisualUtils.RadialGlow(); // the pulsing hazard
        var ringSprite = VisualUtils.HollowRing(); // the reveal highlight (clean circle outline)
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
            sr.sprite = ballSprite; sr.sharedMaterial = decoyMat;
            sr.color = GameConfig.DecoyColor; sr.sortingOrder = 28;
            go.transform.localScale = Vector3.one * (GameConfig.CellSize * 0.7f);
            go.SetActive(false);
            _decoySR[i] = sr;

            // Hollow highlight ring, drawn a touch larger, sitting around the ball.
            var rgo = new GameObject("DecoyRing" + i);
            rgo.transform.SetParent(container, false);
            var rsr = rgo.AddComponent<SpriteRenderer>();
            rsr.sprite = ringSprite; rsr.sharedMaterial = decoyMat;
            rsr.color = GameConfig.DecoyRingColor; rsr.sortingOrder = 29;
            // Bigger than the ball so the outline sits clearly AROUND it (ring ~0.86*scale across).
            rgo.transform.localScale = Vector3.one * (GameConfig.CellSize * 1.35f);
            rgo.SetActive(false);
            _decoyRingSR[i] = rsr;
        }

        // Bonus Echo orb — a golden reward hidden in a dead-end, findable only by pinging.
        var orbGO = new GameObject("BonusOrb");
        orbGO.transform.SetParent(transform, false);
        _orbSR = orbGO.AddComponent<SpriteRenderer>();
        _orbSR.sprite = ballSprite;
        _orbSR.sharedMaterial = decoyMat;
        _orbSR.color = GameConfig.BonusOrbColor;
        _orbSR.sortingOrder = 31;
        orbGO.SetActive(false);

        // Exit target ring — a pulsing green marker that makes the destination pop.
        var exitRingGO = new GameObject("ExitRing");
        exitRingGO.transform.SetParent(transform, false);
        _exitRingSR = exitRingGO.AddComponent<SpriteRenderer>();
        _exitRingSR.sprite = VisualUtils.HollowRing();
        _exitRingSR.sharedMaterial = decoyMat;
        _exitRingSR.color = GameConfig.ExitColor;
        _exitRingSR.sortingOrder = 29;
    }

    /// <summary>Show the main menu. Nothing runs until the player presses PLAY or DAILY.</summary>
    public void StartGame()
    {
        SaveData.ApplySettings();

        _ui.OnPlay = BeginEndlessRun;
        _ui.OnDaily = BeginDailyRun;
        _ui.OnDailyResultClosed = ReturnToMenu;
        _ui.OnResetLevel = ResetLevel;
        _ui.OnHome = GoHome;
        _ui.Audio = _audio;     // lets the UI layer make noise without reaching for a singleton

        ReturnToMenu();
    }

    private void ReturnToMenu()
    {
        if (_tutorial != null) _tutorial.Hide();   // never let the tutorial overlay sit on the menu
        _ui.HideTeachCard();                       // nor an explainer from the run we just left
        _isDaily = false;
        _failStreak = 0;
        _level = SaveData.CurrentLevel;     // the menu previews where they'll pick up
        _dayStreak = SaveData.DayStreak;

        BuildLevel(_level);                 // something alive behind the menu
        State = GameState.Start;
        _ui.ShowStart(_level, _dayStreak, SaveData.DailyDone, SaveData.RunFinished);
    }

    /// <summary>Normal endless progression.</summary>
    private void BeginEndlessRun()
    {
        _isDaily = false;
        _failStreak = 0;
        _stars = GameConfig.StartStars;              // a new run never inherits a carried gold star
        _level = SaveData.CurrentLevel;              // resume, don't restart
        _dayStreak = SaveData.RegisterDailyVisit();  // opening the game counts toward the streak
        _dailyBonusPending = true;                   // spent by the first level this session builds

        BuildLevel(_level);
        _ui.HideStart();
        State = _tutorial != null && _tutorial.ShouldRun ? GameState.Tutorial : GameState.Playing;
        if (State == GameState.Tutorial)
        {
            // Hide RETRY/settings for the duration. ResetLevel refuses to run in the Tutorial
            // state, so leaving the button on screen just gives a first-time player something
            // that visibly does nothing when tapped.
            _ui.ShowInGameGear(false);
            _tutorial.Begin(this, _ui, _player);
        }
        else MaybeTeachLevel();
    }

    /// <summary>
    /// The Daily Maze: one fixed layout per day, a single attempt, its own result screen.
    /// Deliberately a separate mode so the ritual is visible — a daily nobody knows about
    /// retains nobody.
    /// </summary>
    private void BeginDailyRun()
    {
        _isDaily = true;
        _level = 1; _failStreak = 0;
        _stars = GameConfig.StartStars;   // the daily is a standalone maze, no carry-over
        _dayStreak = SaveData.RegisterDailyVisit();

        BuildLevel(_level);
        _ui.HideStart();
        State = GameState.Playing;
        _ui.ShowBanner("DAILY MAZE", new Color(1f, 0.85f, 0.4f, 1f), 1.0f);
        MaybeTeachLevel();
    }

    private void BuildLevel(int level)
    {
        _profile = GameConfig.GetDifficulty(level, _failStreak);

        // The daily run uses the date seed so every player worldwide gets the same layout today.
        int seed = _isDaily ? SaveData.DailySeed : Random.Range(1, int.MaxValue);
        _maze = MazeGenerator.Generate(_profile.mazeSize, GameConfig.CellSize, seed);

        // Per-sector palette: re-tint walls and the ping ring so each chapter reads differently.
        Color sectorColor = GameConfig.SectorWallColor(level);
        _wallCtrl.SetGlowColor(sectorColor);
        _sonar.SetRingColor(sectorColor);

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

        _movingExit = _profile.movingExit;
        _exitMoveTimer = _profile.exitMoveInterval;

        PlaceDecoys(_profile.decoyCount);
        PlaceBonusOrb();

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
        if (_isDaily) _ui.SetDailyHeader();
        else
        {
            _ui.SetLevel(level);
            _ui.SetSector(GameConfig.SectorName(level), GameConfig.LevelInSector(level),
                          GameConfig.LevelsPerSector, sectorColor);
        }
        // Every level starts at full health — but a gold overcharge star you are still holding
        // CARRIES FORWARD. Wiping it at the level boundary made it a one-level trinket; letting it
        // ride turns finding an echo into something you protect across levels, and gives the extra
        // life somewhere meaningful to be spent.
        _stars = Mathf.Clamp(Mathf.Max(_stars, GameConfig.StartStars),
                             GameConfig.StartStars, GameConfig.OverchargeStars);
        _ui.SetStars(_stars, GameConfig.MaxStars);

        // Announce a new sector as you enter it.
        if (!_isDaily && GameConfig.LevelInSector(level) == 1 && level > 1)
            _ui.ShowBanner("SECTOR " + (GameConfig.SectorIndex(level) + 1) + "\n" + GameConfig.SectorName(level),
                           sectorColor, 1.3f);

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

        // Lit by the sonar exactly like the walls, with a gentle pulse on top.
        float reveal = _sonar.RevealAt(_orbPos);
        float pulse = 0.75f + 0.25f * Mathf.Sin(Time.time * GameConfig.BonusOrbPulseSpeed);
        var c = GameConfig.BonusOrbColor;
        c.a = Mathf.Clamp01(reveal * pulse * 1.3f);
        _orbSR.color = c;
        _orbSR.transform.localScale = Vector3.one * (GameConfig.CellSize * (0.45f + 0.15f * reveal));

        // Collect on contact (whether or not it happens to be lit at that instant).
        if (SweptHit(_orbPos, GameConfig.BonusOrbRadius))
            CollectBonusOrb();
    }

    private void CollectBonusOrb()
    {
        _orbActive = false;
        _orbSR.gameObject.SetActive(false);

        // Always worth taking, at every health level. A reward that sometimes pays nothing trains
        // players to ignore it, so there are three outcomes and never a null one:
        //
        //   hurt            -> restore a normal star
        //   full (3)        -> a fourth, GOLD star
        //   already gold(4) -> +1 reveal, since a second gold star has nowhere to go
        //
        // The last case matters because the gold star now carries between levels, so arriving at a
        // level already holding one is common rather than exotic.
        if (_stars >= GameConfig.OverchargeStars)
        {
            _pings++;
            _ui.SetPingsRemaining(_pings);
            _ui.ShowBanner("BONUS ECHO\n+1 REVEAL", GameConfig.BonusOrbColor, 0.9f);
            _audio.PlayOvercharge();
            _ui.Flash(0.3f);
        }
        else if (_stars >= GameConfig.MaxStars)
        {
            _stars = GameConfig.OverchargeStars;
            _ui.SetStars(_stars, GameConfig.MaxStars);
            _ui.ShowBanner("OVERCHARGED\n+1 GOLD STAR", GameConfig.BonusOrbColor, 0.9f);
            _audio.PlayOvercharge();
            _ui.Flash(0.35f);
            _shakeTimer = Mathf.Max(_shakeTimer, 0.12f);
        }
        else
        {
            _stars = Mathf.Min(GameConfig.MaxStars, _stars + GameConfig.BonusOrbStars);
            _ui.SetStars(_stars, GameConfig.MaxStars);
            _ui.ShowBanner("BONUS ECHO\n+1 STAR", GameConfig.BonusOrbColor, 0.7f);
            _audio.PlayStarGained();
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
            // a hit costs a life it is a trap: you lose a star, rewind, and the twin is still
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
        MaybeTeachLevel();          // level 1 may still have an orb to introduce
    }

    /// <summary>The exit's current world position — the tutorial points its marker here.</summary>
    public Vector3 ExitWorldPos { get { return new Vector3(_exitWorld.x, _exitWorld.y, 0f); } }

    /// <summary>The gameplay camera, for UI that has to track a world position on screen.</summary>
    public Camera GameCamera { get { return _cam; } }

    /// <summary>
    /// Explain a mechanic the first time the player actually meets one, at the top of the level
    /// that contains it.
    ///
    /// Keyed off what was really placed, not off the level number: the orb only turns up on some
    /// levels, so "level 5 introduces decoys" would happily fire the orb lesson on a level with
    /// no orb in it and teach nothing.
    ///
    /// At most one card per level. Two stacked modals before a level starts is a wall of text,
    /// and whatever loses the race will still be there to explain the next time it appears.
    /// </summary>
    private void MaybeTeachLevel()
    {
        if (State != GameState.Playing) return;

        // Decoys first when both are new: they are the one mechanic that PUNISHES you, and a
        // player who doesn't understand them reads the rewind as the game malfunctioning.
        if (_decoyCount > 0 && !SaveData.TaughtDecoy)
        {
            SaveData.MarkTaughtDecoy();
            _ui.ShowTeachCard(
                "DECOYS",
                "These pulse in and out along your route.\n\n" +
                "Touch one while it is <b>solid</b> and it costs you <b>a star</b> and " +
                Mathf.RoundToInt(GameConfig.DecoyTimePenalty) + " seconds.\n\n" +
                "Lose all three stars and the level restarts.",
                UIManager.Danger, GameConfig.DecoyColor, null);
            return;
        }

        // Paired decoys. Same hazard, new rule — without naming it the player just experiences a
        // corridor that got mysteriously harder, which reads as unfair rather than as a new idea.
        if (_decoyCount >= 2 && _level >= GameConfig.DecoyGateLevel && !SaveData.TaughtGate)
        {
            SaveData.MarkTaughtGate();
            _ui.ShowTeachCard(
                "PAIRED DECOYS",
                "Some decoys now come in twos.\n\n" +
                "They <b>take turns</b> — when one goes dark, the other lights up.\n\n" +
                "Watch the rhythm and cross when both are dark.",
                UIManager.Danger, GameConfig.DecoyColor, null);
            return;
        }

        // The moving exit debuts on level 6. Without a word of warning the player pings, commits
        // to a route, and arrives at an empty cell — which reads as the game cheating.
        if (_movingExit && !SaveData.TaughtMovingExit)
        {
            SaveData.MarkTaughtMovingExit();
            _ui.ShowTeachCard(
                "THE EXIT MOVES",
                "From here the way out drifts between cells.\n\n" +
                "Don't just remember where it was — watch where it goes.",
                UIManager.Accent, GameConfig.ExitColor, null,
                _cam, ExitWorldPos, "EXIT");
            return;
        }

        if (_orbActive && !SaveData.TaughtOrb)
        {
            SaveData.MarkTaughtOrb();
            _ui.ShowTeachCard(
                "BONUS ECHO",
                "One is hidden in a dead end of this maze.\n\n" +
                "Only a ping reveals it. Reach it and you get <b>a star back</b>.",
                UIManager.Gold, GameConfig.BonusOrbColor, null);
        }
    }

    public void RequestPing()
    {
        // During the tutorial only the ping STEP may fire one, so the drag lesson can't be skipped.
        if (State == GameState.Tutorial)
        {
            if (_tutorial == null || !_tutorial.PingAllowed) return;
            if (Time.time < _pingReadyTime || _pings <= 0) return;
            _pings--;
            _ui.SetPingsRemaining(_pings);
            _ui.DarkFlash();
            _sonar.EmitPing(_player.transform.position);
            _pingReadyTime = Time.time + GameConfig.PingCooldown;
            _tutorial.NotifyPinged();
            return;
        }

        if (State != GameState.Playing) return;
        if (_ui.SettingsOpen) return;
        if (Time.time < _pingReadyTime) return;  // cooldown: can't spam — wait for the reveal to finish
        if (_pings <= 0) return;                  // out of pings, but you can still move blind

        _pings--;
        _ui.SetPingsRemaining(_pings);
        _ui.DarkFlash();                 // brief darken so the ring burst reads as powerful
        _sonar.EmitPing(_player.transform.position);
        _pingReadyTime = Time.time + GameConfig.PingCooldown;
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
                if (_ui.TeachOpen) return;      // the goal card is up — nothing moves behind it
                UpdateBonusOrb();
                PulseExit();
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

        if (_profile.timeLimit > 0f)
        {
            _levelTimer -= Time.deltaTime;
            int secs = Mathf.CeilToInt(Mathf.Max(0f, _levelTimer));
            _ui.SetTimer(secs);

            // Audio cues: one heads-up at 10s, then a rising tick each of the last 5 seconds.
            if (secs != _lastTickSecond)
            {
                // Haptics mirror the audio: the countdown is the one cue a player must not
                // miss, and it has to land even with the phone muted in a pocket.
                if (secs == GameConfig.TimerWarnAt) { _audio.PlayTimeWarning(); Haptics.Medium(); }
                else if (secs >= 1 && secs <= GameConfig.TimerTickFrom)
                {
                    _audio.PlayCountdownTick(secs);
                    if (secs <= 3) Haptics.Medium(); else Haptics.Light();
                }
                _lastTickSecond = secs;
            }

            if (_levelTimer <= 0f) { StartCoroutine(FailRoutine(FailCause.Timeout)); return; }
        }
        else _ui.SetTimer(-1);

        UpdateMovingExit();
        UpdateDecoys();

        // UpdateDecoys can end the level outright (last star lost), and a coroutine runs up to
        // its first yield synchronously — so State is already Celebrating by the time we get
        // here. Without this the same frame could also start WinRoutine and run a win and a fail
        // at once. Not currently reachable (decoys are never placed within reach of the exit),
        // but the cost of the check is nothing and the failure mode is incoherent.
        if (State != GameState.Playing) return;

        UpdateBonusOrb();
        PulseExit();

        float exitR = GameConfig.CellSize * 0.4f;
        if (SweptHit(_exitWorld, exitR))
            StartCoroutine(WinRoutine());
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
                _exitCell = _nbrScratch[Random.Range(0, _nbrScratch.Count)];
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

    private void UpdateDecoys()
    {
        float hitRadius = GameConfig.CellSize * GameConfig.DecoyHitRadius;

        for (int i = 0; i < _decoyCount; i++)
        {
            var sr = _decoySR[i];
            bool blackedOut = Time.time < _decoyHideUntil[i]; // just triggered a rewind, stays gone briefly

            // Pulsing orange ball (the hazard): fades in and out on its own phase — slip through
            // the cell while it's dark, and it only bites while it's visible.
            float wave = Mathf.Sin(Time.time * _profile.decoyFadeSpeed + _decoyPhase[i]);
            float vis = Mathf.Clamp01(wave); vis *= vis;
            float alpha = blackedOut ? 0f : vis * GameConfig.DecoyMaxAlpha;
            var c = GameConfig.DecoyColor; c.a = alpha;
            sr.color = c;
            sr.transform.localScale = Vector3.one *
                (GameConfig.CellSize * (GameConfig.DecoyDrawScale + GameConfig.DecoyDrawPulse * vis));

            // Hollow highlight ring around it: lit by the SONAR as the ping front sweeps over the
            // decoy (same timing as the walls), so a ping also shows you where the decoys are.
            float reveal = blackedOut ? 0f : _sonar.RevealAt(_decoyPos[i]);
            var rc = GameConfig.DecoyRingColor; rc.a = Mathf.Clamp01(reveal * 1.25f);
            _decoyRingSR[i].color = rc;

            // Unchanged: only bites while the ball is actually visible. Swept like the exit, so
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
        // A decoy now costs a life AND time, on top of the rewind. It used to cost only position,
        // which players read as a mild inconvenience — there was no reason to time a crossing when
        // barging through cost you a couple of seconds you probably had spare.
        _stars = Mathf.Max(0, _stars - GameConfig.DecoyStarCost);
        _levelTimer -= GameConfig.DecoyTimePenalty;
        _ui.SetStars(_stars, GameConfig.MaxStars);
        _ui.PulseStarLost();

        _audio.PlayStarLost();               // distinct from the generic "wrong" — a life went
        Haptics.Wrong();                     // the platform's "error" pattern: unmistakably bad
        _ui.FlashColor(GameConfig.DecoyColor, 0.3f);
        _fx.PlayDecoyPop(_decoyPos[i]);
        _shakeTimer = Mathf.Max(_shakeTimer, 0.18f);
        _decoyHideUntil[i] = Time.time + 4f;

        // Out of lives ends the level there and then — no rewind, because there is nothing left
        // to rewind into.
        if (_stars <= 0)
        {
            StartCoroutine(FailRoutine(FailCause.NoStars));
            return;
        }

        _ui.ShowRewind();
        _ui.PlayRewindEffect();
        _player.TriggerRewind();             // plays the rewind sound + retrace + reveal wipe
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

        // Pulsing target ring around the exit, brighter as you approach — makes the goal pop.
        float ringPulse = 0.5f + 0.5f * Mathf.Sin(Time.time * GameConfig.ExitPulseSpeed * 1.3f);
        _exitRingSR.transform.position = _exitSR.transform.position;
        _exitRingSR.transform.localScale = Vector3.one * (GameConfig.CellSize * (1.25f + 0.12f * ringPulse));
        var rc = GameConfig.ExitColor; rc.a = 0.28f + 0.22f * ringPulse + 0.4f * near;
        _exitRingSR.color = rc;
    }

    private IEnumerator WinRoutine()
    {
        State = GameState.Celebrating;

        // The stars you WALK OUT WITH are the result now — not a rating computed from leftover
        // pings. Nothing to calculate: what survived the decoys is what you get.
        int starsLeft = _stars;
        bool clutch = _profile.timeLimit > 0f && _levelTimer <= GameConfig.ClutchSeconds;
        bool sectorDone = GameConfig.IsSectorFinale(_level);

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

        // ---- Celebration panel ----
        _ui.ShowCelebration(_level);
        // The Daily is one standalone maze, so "LEVEL 1 CLEAR" is meaningless there — and its
        // title must win over the sector/clutch variants, which belong to the endless run.
        if (_isDaily)
            _ui.SetCelebrationTitle("DAILY MAZE\nCLEAR", UIManager.Gold);
        else if (sectorDone)
            _ui.SetCelebrationTitle("SECTOR CLEAR\n" + GameConfig.SectorName(_level), GameConfig.SectorWallColor(_level));
        else if (clutch)
            _ui.SetCelebrationTitle("CLUTCH CLEAR!", UIManager.Gold);

        // Point at the next milestone the moment the current one is banked. On a sector finale
        // the title already carries the news; otherwise, once the next sector is close enough to
        // be worth chasing, name it. Left blank further out so it stays an event rather than a
        // permanent readout the eye stops seeing.
        int nextLevel = _level + 1;
        int toNext = GameConfig.LevelsToNextSector(nextLevel);
        _ui.SetCelebrationScoreLine(
            _isDaily || sectorDone || toNext > 2
                ? ""
                : GameConfig.NextSectorName(nextLevel) + "   in   " + toNext);

        // A beat of silence, then the praise word lands. The gap matters: slamming it in on the
        // same frame as the title makes both read as one blob of text instead of a payoff.
        yield return new WaitForSecondsRealtime(0.26f);
        _ui.ShowPraise(GameConfig.PraiseFor(starsLeft),
                       starsLeft >= GameConfig.MaxStars ? UIManager.Gold : UIManager.Accent);
        _audio.PlayPraise(starsLeft);
        Haptics.Success();
        _ui.Flash(0.25f);
        _shakeTimer = Mathf.Max(_shakeTimer, 0.14f);

        // No "new best" callout here. The player can only ever be ON their deepest level —
        // there is no level select and no way back — so every single clear would trigger it,
        // which makes it noise rather than an achievement.

        // The daily is a single maze, not a run — finish it and show its own result screen.
        if (_isDaily)
        {
            yield return new WaitForSecondsRealtime(GameConfig.CelebrationTime);
            bool dailyBest = SaveData.CompleteDaily(starsLeft);
            _ui.HideCelebration();
            _ui.ShowDailyResult(true, starsLeft, _dayStreak, dailyBest);
            State = GameState.Start;   // menu buttons take over again via OnDailyResultClosed
            yield break;
        }

        // ---- One-more-level: auto-advance behind a quick fade so the swap isn't a hard cut ----
        yield return new WaitForSecondsRealtime(GameConfig.CelebrationTime);

        _ui.SetCover(1f);                                   // fade to black
        yield return new WaitForSecondsRealtime(0.22f);

        _failStreak = 0;
        _level++;
        SaveData.CurrentLevel = _level;                     // resume point if they quit here
        BuildLevel(_level);                                 // camera + player swap, hidden by the cover
        _ui.HideCelebration();
        State = GameState.Playing;

        _ui.SetCover(0f);                                   // fade back in on the fresh level
        MaybeTeachLevel();                                  // ...and introduce anything new on it
    }

    /// <summary>Why a level ended — the banner says which, since the fix differs.</summary>
    private enum FailCause { Timeout, NoStars }

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

        _failStreak++;
        if (!_isDaily) SaveData.MarkRunFinished();   // finishing a level unlocks the Daily Maze
        _ui.Flash(0.35f);
        Haptics.Heavy();
        _audio.PlayLose();

        // Light the place up so the player can see the route they missed.
        Vector2 playerPos = _player.transform.position;
        _sonar.RevealAll(playerPos, GameConfig.FailRevealTime);

        int tiles = Mathf.Max(1, Mathf.RoundToInt(Vector2.Distance(playerPos, _exitWorld) / GameConfig.CellSize));
        string head = cause == FailCause.NoStars ? "OUT OF STARS" : "OUT OF TIME";
        string line = tiles <= 2
            ? "SO CLOSE!\n" + tiles + (tiles == 1 ? " tile away" : " tiles away")
            : head + "\n" + tiles + " tiles away";
        if (!_isDaily) line += "\nRETRY LEVEL " + _level;
        _ui.ShowBanner(line, UIManager.Danger, GameConfig.FailRevealTime * 0.6f);

        yield return new WaitForSecondsRealtime(GameConfig.FailRevealTime);

        // The daily allows one attempt — failing ends it.
        if (_isDaily)
        {
            SaveData.CompleteDaily(0);
            _ui.ShowDailyResult(false, 0, _dayStreak, false);
            State = GameState.Start;
            yield break;
        }

        // Same level, brand new layout — the challenge repeats, the solution doesn't.
        _ui.SetCover(1f);
        yield return new WaitForSecondsRealtime(0.22f);
        BuildLevel(_level);
        State = GameState.Playing;
        _ui.SetCover(0f);
        // The regenerated layout may contain something this player has never met — an orb only
        // turns up on 18% of levels, so it can easily first appear on a retry rather than on the
        // clear that would otherwise have introduced it.
        MaybeTeachLevel();
    }

    /// <summary>
    /// Regenerate the current level from scratch. Exposed as a button because a maze whose layout
    /// you have half-memorised but keep dying in is a frustrating place to be stuck, and the old
    /// escape hatch — reset ALL progress — was wildly out of proportion to the problem.
    /// </summary>
    public void ResetLevel()
    {
        if (State != GameState.Playing && State != GameState.Celebrating) return;
        StopAllCoroutines();
        Time.timeScale = 1f;
        StartCoroutine(ResetLevelRoutine());
    }

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
        State = GameState.Celebrating;      // freeze the clock and input during the wipe
        _ui.HideCelebration();
        _ui.HideTeachCard();
        _audio.PlayWhoosh();
        Haptics.Medium();

        _ui.SetCover(1f);
        yield return new WaitForSecondsRealtime(0.28f);

        _failStreak = 0;
        BuildLevel(_level);
        _ui.ShowBanner("LEVEL " + _level + "\nRESET", UIManager.Accent, 0.9f);

        yield return new WaitForSecondsRealtime(0.12f);   // hold the black a beat so it registers

        State = GameState.Playing;
        _ui.SetCover(0f);
        MaybeTeachLevel();      // the fresh layout may introduce something new — see FailRoutine
    }

    /// <summary>Abandon the level and go back to the menu. Progress is already saved per level.</summary>
    public void GoHome()
    {
        StopAllCoroutines();
        Time.timeScale = 1f;
        _ui.HideCelebration();
        _ui.SetCover(0f);
        ReturnToMenu();
    }

    private void FitCamera(MazeData maze)
    {
        _cam.orthographic = true;
        float aspect = _cam.aspect;
        float halfH = maze.worldHeight * 0.5f + GameConfig.CameraPadding;
        float halfW = maze.worldWidth * 0.5f + GameConfig.CameraPadding;
        _baseOrthoSize = Mathf.Max(halfH, halfW / aspect); // fits any aspect (portrait -> width bound)
        _cam.orthographicSize = _baseOrthoSize;
        _camBase = new Vector3(maze.worldCenter.x, maze.worldCenter.y, -10f);
        _cam.transform.position = _camBase;
    }

    private void LateUpdate()
    {
        float dt = Time.unscaledDeltaTime; // survive hitstop (timeScale 0)

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
        _failStreak = 0;
        _level = Mathf.Max(1, level);
        BuildLevel(_level);
        _ui.HideStart();
        _ui.HideCelebration();
        _ui.SetCover(0f);
        State = GameState.Playing;
        MaybeTeachLevel();   // mirror the real level-entry path, so QA sees what a player sees
    }
#endif

    private void OnApplicationPause(bool paused)
    {
        AudioListener.pause = paused; // mute cleanly when backgrounded; timer naturally halts
    }
}
