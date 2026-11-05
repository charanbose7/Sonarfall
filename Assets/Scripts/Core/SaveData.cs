using UnityEngine;

/// <summary>
/// Thin PlayerPrefs wrapper for persistence: the level to resume on, the consecutive-day
/// streak, Daily Maze state, the settings toggles, and the one-time flags for the tutorial
/// and each mechanic explainer. Every setter commits immediately.
/// </summary>
public static class SaveData
{
    private const string KCurLevel   = "em_cur_level";    // level to resume on next launch
    private const string KHintSeen   = "em_hint_seen";
    private const string KDayStreak  = "em_day_streak";
    private const string KLastDay    = "em_last_day";   // UTC day number of the last session
    private const string KBestDay    = "em_best_day_streak";
    private const string KSound      = "em_sound";
    private const string KHaptics    = "em_haptics";
    private const string KNotifs     = "em_notifs";       // local reminders opt-out
    private const string KNotifAsked = "em_notif_asked";  // the one-time permission prompt has run
    private const string KDailyDone  = "em_daily_done";   // UTC day number of the last daily clear
    private const string KDailyBest  = "em_daily_best";   // best daily grade (0..3)
    private const string KRunFinished= "em_run_finished";  // player has completed one endless run
    private const string KTaughtOrb  = "em_taught_orb";    // seen the bonus-echo explainer
    private const string KTaughtDecoy= "em_taught_decoy";  // seen the decoy explainer
    private const string KTaughtExit = "em_taught_exit";   // seen the moving-exit explainer
    private const string KTaughtGate = "em_taught_gate";   // seen the paired-decoy explainer
    private const string KTaughtRule = "em_taught_rule_";  // + LevelRule value: seen that rule's callout
    private const string KHudTaught  = "em_hud_";          // + item name: HUD element introduced
    private const string KGrade      = "em_grade_";        // + level: best grade 0..3
    private const string KMusic      = "em_music";
    private const string KSfx        = "em_sfx";
    private const string KBright     = "em_brightness";    // 0..2, index into GameConfig.BrightnessGain
    private const string KSonarCol   = "em_sonar_colour";  // chosen sonar colour index
    private const string KDescentBest= "em_descent_best";  // deepest Descent, in mazes cleared
    private const string KDescentRuns= "em_descent_runs";

    /// <summary>
    /// The level to resume on. This is a puzzle game, not an endless runner: quitting on level 24
    /// and coming back to level 1 throws away everything the player built up. They resume exactly
    /// where they were — on a freshly generated layout, so it is the same challenge rather than
    /// the same maze memorised.
    /// </summary>
    public static int CurrentLevel
    {
        get { return Mathf.Max(1, PlayerPrefs.GetInt(KCurLevel, 1)); }
        set { PlayerPrefs.SetInt(KCurLevel, Mathf.Max(1, value)); PlayerPrefs.Save(); }
    }

    // No "best level" record any more. There is no level select and no way back, so the level
    // you are on IS the deepest you have reached — a separate best would always equal it. Flush()
    // went with it: every remaining setter commits on write, so there is nothing left to batch.

    // ---- Daily streak ----------------------------------------------------------------
    // "Come back tomorrow" is the strongest retention hook we can add without a backend.
    // Everything is keyed off the UTC day number so it can't be gamed by timezone changes.

    /// <summary>Whole days since epoch, in UTC — the canonical "which day is it" value.</summary>
    public static int TodayNumber => (int)(System.DateTime.UtcNow.Date - new System.DateTime(1970, 1, 1)).TotalDays;

    public static int DayStreak => PlayerPrefs.GetInt(KDayStreak, 0);
    public static int BestDayStreak => PlayerPrefs.GetInt(KBestDay, 0);

    /// <summary>Seed for today's shared maze — same layout for every player on a given day.</summary>
    public static int DailySeed => TodayNumber * 7919 + 13;

    /// <summary>True if today's daily has already been played (so we only reward it once).</summary>
    public static bool PlayedToday => PlayerPrefs.GetInt(KLastDay, -1) == TodayNumber;

    /// <summary>
    /// Register a session for today. Consecutive days extend the streak, a skipped day resets it.
    /// Returns the streak length after the update. Safe to call repeatedly on the same day.
    /// </summary>
    public static int RegisterDailyVisit()
    {
        int today = TodayNumber;
        int last = PlayerPrefs.GetInt(KLastDay, -1);
        if (last == today) return DayStreak;             // already counted today

        int streak = last == today - 1 ? DayStreak + 1 : 1;
        PlayerPrefs.SetInt(KDayStreak, streak);
        PlayerPrefs.SetInt(KLastDay, today);
        if (streak > BestDayStreak) PlayerPrefs.SetInt(KBestDay, streak);
        PlayerPrefs.Save();
        return streak;
    }

    // ---- Daily Maze run ----
    /// <summary>Has today's daily maze already been completed? (One attempt per day.)</summary>
    public static bool DailyDone => PlayerPrefs.GetInt(KDailyDone, -1) == TodayNumber;

    /// <summary>Best grade ever earned on a Daily, 0..3. Clamped because it used to hold the
    /// stars-left count (up to 4) from before grades existed.</summary>
    public static int DailyBest => Mathf.Clamp(PlayerPrefs.GetInt(KDailyBest, 0), 0, 3);

    /// <summary>Mark today's daily finished. Returns true if it was also a personal daily best.</summary>
    public static bool CompleteDaily(int grade)
    {
        PlayerPrefs.SetInt(KDailyDone, TodayNumber);
        bool best = grade > DailyBest;
        if (best) PlayerPrefs.SetInt(KDailyBest, grade);
        PlayerPrefs.Save();
        return best;
    }

    /// <summary>Today's Daily number for sharing — "Sonarfall Daily #59".</summary>
    public static int DailyNumber => Mathf.Max(1, TodayNumber - GameConfig.DailyEpochDay + 1);

    /// <summary>
    /// The streak as it stands right now, not as last written. The stored value only updates when a
    /// run starts, so a player who skipped three days would otherwise see their dead streak on the
    /// menu as if it were still alive.
    /// </summary>
    public static int LiveStreak
    {
        get
        {
            int last = PlayerPrefs.GetInt(KLastDay, -1);
            if (last < 0) return 0;
            return last >= TodayNumber - 1 ? DayStreak : 0;
        }
    }

    /// <summary>Has the player ever started a run on any day?</summary>
    public static bool EverPlayed => PlayerPrefs.GetInt(KLastDay, -1) >= 0;

    /// <summary>True when a streak of 2+ existed and lapsed — worth one line saying so.</summary>
    public static bool StreakLapsed
    {
        get
        {
            int last = PlayerPrefs.GetInt(KLastDay, -1);
            return last >= 0 && last < TodayNumber - 1 && DayStreak >= 2;
        }
    }

    // ---- Level grades ---------------------------------------------------------------------
    /// <summary>
    /// Best grade ever earned on a level: 0 = never cleared, 1..3 stars. A level cleared before
    /// grades existed has no record, but it is behind the resume point, so it was reached and
    /// passed — it gets its EXIT star rather than reading as never played.
    /// </summary>
    public static int LevelGrade(int level)
    {
        int g = PlayerPrefs.GetInt(KGrade + level, 0);
        return g == 0 && level < CurrentLevel ? 1 : g;
    }

    /// <summary>Record a clear. Keeps the best; returns true if this beat the old one.</summary>
    public static bool RecordGrade(int level, int grade)
    {
        int old = LevelGrade(level);
        if (grade <= old) return false;
        PlayerPrefs.SetInt(KGrade + level, grade);
        PlayerPrefs.Save();
        return true;
    }

    /// <summary>Stars earned across a sector's five levels (0..15).</summary>
    public static int SectorStars(int sectorIndex)
    {
        int first = sectorIndex * GameConfig.LevelsPerSector + 1, total = 0;
        for (int i = 0; i < GameConfig.LevelsPerSector; i++) total += LevelGrade(first + i);
        return total;
    }

    /// <summary>
    /// Sectors whose finale the player has beaten. Read off the resume point rather than stored,
    /// because CurrentLevel only ever climbs: past level 5k means sector k was finished.
    /// </summary>
    public static int SectorsCleared => Mathf.Max(0, (CurrentLevel - 1) / GameConfig.LevelsPerSector);

    // ---- Descent ---------------------------------------------------------------------------
    public static int DescentBest => PlayerPrefs.GetInt(KDescentBest, 0);
    public static int DescentRuns => PlayerPrefs.GetInt(KDescentRuns, 0);

    /// <summary>Bank a finished dive. Returns true on a new deepest run.</summary>
    public static bool RecordDescent(int mazesCleared)
    {
        PlayerPrefs.SetInt(KDescentRuns, DescentRuns + 1);
        bool best = mazesCleared > DescentBest;
        if (best) PlayerPrefs.SetInt(KDescentBest, mazesCleared);
        PlayerPrefs.Save();
        return best;
    }

    public static bool DescentUnlocked => CurrentLevel >= GameConfig.DescentUnlockLevel;

    /// <summary>
    /// True once the player has seen a normal level through to an ending — cleared it OR run out.
    /// The Daily Maze stays locked until then: it is a single high-pressure attempt with no
    /// retries, which is a terrible first experience for someone who has never pinged a wall.
    ///
    /// The name is a leftover from the score-run era, when "the run finished" meant the player had
    /// died and there was exactly one way to get here. On a level ladder both outcomes qualify, and
    /// for a while only the losing one was wired up — so clearing level 1 left the Daily locked
    /// while failing it opened the door.
    /// </summary>
    public static bool RunFinished => PlayerPrefs.GetInt(KRunFinished, 0) == 1;

    public static void MarkRunFinished()
    {
        if (RunFinished) return;
        PlayerPrefs.SetInt(KRunFinished, 1);
        PlayerPrefs.Save();
    }

#if UNITY_EDITOR
    /// <summary>
    /// Editor-only: wipe onboarding so the next play is treated as a brand new install — the
    /// tutorial replays and every explainer fires again. The only way to re-test onboarding
    /// without hand-editing PlayerPrefs. Compiled out of player builds.
    /// </summary>
    public static void ResetOnboarding()
    {
        PlayerPrefs.SetInt(KHintSeen, 0);
        PlayerPrefs.SetInt(KRunFinished, 0);
        PlayerPrefs.SetInt(KTaughtOrb, 0);
        PlayerPrefs.SetInt(KTaughtDecoy, 0);
        PlayerPrefs.SetInt(KTaughtExit, 0);
        PlayerPrefs.SetInt(KTaughtGate, 0);
        PlayerPrefs.SetInt(KNotifAsked, 0);
        foreach (var item in new[] { "clock", "reveals", "hull", "grade" }) PlayerPrefs.DeleteKey(KHudTaught + item);
        foreach (LevelRule r in System.Enum.GetValues(typeof(LevelRule))) PlayerPrefs.DeleteKey(KTaughtRule + (int)r);
        PlayerPrefs.SetInt(KCurLevel, 1);
        PlayerPrefs.Save();
    }
#endif

    // ---- One-time mechanic explainers -------------------------------------------------
    // Playtesters met the gold orb and the decoys with no idea what either was: the orb was
    // ignored as decoration and the decoys read as a bug ("something threw me backwards").
    // Each is explained once, on the first level that actually contains one, and never again.

    /// <summary>Has the bonus-echo explainer been shown?</summary>
    public static bool TaughtOrb => PlayerPrefs.GetInt(KTaughtOrb, 0) == 1;
    public static void MarkTaughtOrb() { PlayerPrefs.SetInt(KTaughtOrb, 1); PlayerPrefs.Save(); }

    /// <summary>Has the decoy explainer been shown?</summary>
    public static bool TaughtDecoy => PlayerPrefs.GetInt(KTaughtDecoy, 0) == 1;
    public static void MarkTaughtDecoy() { PlayerPrefs.SetInt(KTaughtDecoy, 1); PlayerPrefs.Save(); }

    /// <summary>Has the moving-exit explainer been shown?</summary>
    public static bool TaughtMovingExit => PlayerPrefs.GetInt(KTaughtExit, 0) == 1;
    public static void MarkTaughtMovingExit() { PlayerPrefs.SetInt(KTaughtExit, 1); PlayerPrefs.Save(); }

    /// <summary>Has the paired-decoy (gate) explainer been shown?</summary>
    public static bool TaughtGate => PlayerPrefs.GetInt(KTaughtGate, 0) == 1;
    public static void MarkTaughtGate() { PlayerPrefs.SetInt(KTaughtGate, 1); PlayerPrefs.Save(); }

    /// <summary>Has this sector rule's one-line callout been shown?</summary>
    public static bool TaughtRule(LevelRule r) => PlayerPrefs.GetInt(KTaughtRule + (int)r, 0) == 1;
    public static void MarkTaughtRule(LevelRule r) { PlayerPrefs.SetInt(KTaughtRule + (int)r, 1); PlayerPrefs.Save(); }

    /// <summary>
    /// Has a HUD element been introduced? Each readout pulses once, the first time it matters —
    /// "clock" when it first starts counting, "reveals" on the first ping, "hull" the first time a
    /// plate is lost or gained, "grade" on the first clear. Replaces the three tutorial cards that
    /// used to point at them before the player had any reason to care.
    /// </summary>
    public static bool HudTaught(string item) => PlayerPrefs.GetInt(KHudTaught + item, 0) == 1;
    public static void MarkHudTaught(string item) { PlayerPrefs.SetInt(KHudTaught + item, 1); PlayerPrefs.Save(); }

    public static bool HintSeen => PlayerPrefs.GetInt(KHintSeen, 0) == 1;
    public static void MarkHintSeen()
    {
        PlayerPrefs.SetInt(KHintSeen, 1);
        PlayerPrefs.Save();
    }

    // ---- Settings --------------------------------------------------------------------
    /// <summary>
    /// The old single SOUND switch. Kept only as the default for the two that replaced it, so a
    /// player who had muted the game before the split stays muted after it.
    /// </summary>
    private static bool LegacySoundOn => PlayerPrefs.GetInt(KSound, 1) == 1;

    /// <summary>The ambient score — drone and pad. Separate from effects, because a lot of players
    /// want their own music on and still need the pings, which carry information.</summary>
    public static bool MusicOn
    {
        get => PlayerPrefs.GetInt(KMusic, LegacySoundOn ? 1 : 0) == 1;
        set { PlayerPrefs.SetInt(KMusic, value ? 1 : 0); PlayerPrefs.Save(); }
    }

    /// <summary>Every gameplay and UI sound.</summary>
    public static bool SfxOn
    {
        get => PlayerPrefs.GetInt(KSfx, LegacySoundOn ? 1 : 0) == 1;
        set { PlayerPrefs.SetInt(KSfx, value ? 1 : 0); PlayerPrefs.Save(); }
    }

    /// <summary>0..2 — index into GameConfig.BrightnessGain.</summary>
    public static int Brightness
    {
        get => Mathf.Clamp(PlayerPrefs.GetInt(KBright, 0), 0, GameConfig.BrightnessGain.Length - 1);
        set { PlayerPrefs.SetInt(KBright, Mathf.Clamp(value, 0, GameConfig.BrightnessGain.Length - 1)); PlayerPrefs.Save(); }
    }

    /// <summary>Chosen sonar colour. Clamped to what has been earned, so a colour can't outlive
    /// the progress that unlocked it (e.g. an editor save reset).</summary>
    public static int SonarColour
    {
        get => Mathf.Clamp(PlayerPrefs.GetInt(KSonarCol, 0), 0, SonarColoursUnlocked - 1);
        set { PlayerPrefs.SetInt(KSonarCol, Mathf.Max(0, value)); PlayerPrefs.Save(); }
    }

    /// <summary>How many sonar colours are available: the default plus one per sector cleared.</summary>
    public static int SonarColoursUnlocked => 1 + Mathf.Min(SectorsCleared, GameConfig.SectorNames.Length);

    public static bool HapticsOn
    {
        get => PlayerPrefs.GetInt(KHaptics, 1) == 1;
        set { PlayerPrefs.SetInt(KHaptics, value ? 1 : 0); PlayerPrefs.Save(); }
    }

    /// <summary>
    /// Local reminders. Defaults ON, but nothing is ever scheduled until the OS permission is
    /// granted, and the player can turn it off in Settings without touching system settings.
    /// </summary>
    public static bool NotificationsOn
    {
        get => PlayerPrefs.GetInt(KNotifs, 1) == 1;
        set { PlayerPrefs.SetInt(KNotifs, value ? 1 : 0); PlayerPrefs.Save(); }
    }

    /// <summary>
    /// Has the first-PLAY notification prompt already run? Its own flag rather than piggybacking
    /// on HintSeen or RunFinished: testers who installed before the prompt existed have both of
    /// those set and would otherwise never be asked at all — which is exactly why "notifications
    /// don't work" reports came from phones that had simply never granted the permission.
    /// </summary>
    public static bool NotifAsked => PlayerPrefs.GetInt(KNotifAsked, 0) == 1;
    public static void MarkNotifAsked() { PlayerPrefs.SetInt(KNotifAsked, 1); PlayerPrefs.Save(); }

    /// <summary>Push saved settings into the systems that consume them.</summary>
    public static void ApplySettings()
    {
        // Music and effects are routed per source by ProceduralAudio; the listener stays at full
        // volume so one can be off without silencing the other.
        AudioListener.volume = 1f;
        Haptics.Enabled = HapticsOn;
        Shader.SetGlobalFloat(SonarGainId, GameConfig.BrightnessGain[Brightness]);
    }

    private static readonly int SonarGainId = Shader.PropertyToID("_SonarGain");

    // ResetProgress() was removed along with the settings button that called it. In a level-based
    // game the level you reached IS the progress, so a "wipe my save" action has no upside left to
    // offer — it can only take away the one thing the player is accumulating.
}
