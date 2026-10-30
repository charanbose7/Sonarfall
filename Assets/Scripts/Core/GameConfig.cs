using UnityEngine;

/// <summary>
/// Per-level difficulty, computed by <see cref="GameConfig.GetDifficulty"/>.
/// Everything that changes as you progress lives here so the ramp is data-driven.
/// </summary>
public struct Difficulty
{
    public int mazeSize;
    public int pings;
    public float fade;        // seconds a revealed wall stays lit
    public float ringSpeed;   // sonar expansion speed
    public float band;        // front white-flash band (seconds) — thinner = harder to read
    public float timeLimit;   // fail timer
    public bool movingExit;
    public float exitMoveInterval;
    public int decoyCount;
    public float decoyFadeSpeed;   // how fast decoys blink — faster is harder to time a crossing

    // ---- Sector rules (see GameConfig.RulesFor) ----
    public LevelRule rules;
    public int   hunterCount;
    public float hunterSpeed;      // cells per second
    public int   gateCount;
    public float gatePeriod;       // seconds for one full open+closed cycle
    public int   stillZones;
    public int   currentCount;
    public float currentSpeed;     // world units per second of push
}

/// <summary>
/// The one new idea each sector after DRIFT adds. Flags, because past the first lap a level can
/// carry two of them at once.
/// </summary>
[System.Flags]
public enum LevelRule
{
    None       = 0,
    Hunter     = 1,    // THE HOLLOW — something follows your pings
    Gates      = 2,    // ECHO CORE  — passages open and shut on the core's beat
    FastFade   = 4,    // LONG DARK  — echoes die almost as soon as they land
    StillWater = 8,    // STILLWATER — patches where a ping shows nothing
    Currents   = 16,   // UNDERTOW   — corridors that push you along
}

/// <summary>
/// Central tuning block. Every knob for maze size, movement, sonar, juice, stars-as-lives
/// and the difficulty sawtooth lives here. No ScriptableObjects.
/// </summary>
public static class GameConfig
{
    // ---- Maze / session length (aim: 20-60s per level) ----
    public const int   StartMazeSize     = 6;    // level 1 grid
    public const int   MaxMazeSize        = 14;  // cap — we grow difficulty other ways past this
    public const int   LevelsPerSizeStep  = 2;   // grid grows +1 every N levels
    public const float CellSize           = 1.0f;
    public const float WallThickness      = 0.12f;

    // ---- Player ----
    public const float PlayerMoveSpeed    = 1.2f;
    public const float PlayerRadius       = 0.24f; // keep < CellSize/2
    public const float PlayerGlowScale    = 0.7f;
    public const float SquashAmount       = 0.28f; // stretch toward motion at full speed
    public const float SquashLerp         = 12f;
    public const float BreathAmplitude    = 0.06f; // idle "breathing" pulse
    public const float BreathSpeed        = 2.4f;
    public const float TrailTime          = 0.12f; // short so fast moves don't leave a long sharp streak
    public const float TrailWidth         = 0.11f;
    public const float SpawnPopTime       = 0.4f;  // dot "materializes" instead of snapping in

    // ---- Touch / input (direct finger-drag, frame-rate, collide-and-slide) ----
    public const float MoveEngagePx      = 8f;    // finger travel before the dot starts following (small = responsive)
    public const float MoveSensitivity   = 1f;  // 1.0 = the dot tracks the finger 1:1 in world space
    public const float PlayerCollideSkin = 0.02f; // gap kept from walls when sliding
    public const float TapMaxDuration    = 0.22f; // quick touch that never engaged movement = a ping
    public const float MoveAudioMaxVol   = 0.16f; // loudest the movement drone gets

    // ---- Rewind penalty (touching a decoy) ----
    public const float RewindSeconds   = 2f;    // how far back the dot is sent
    public const float RewindDuration  = 1.0f;  // unscaled length of the whole rewind sequence
    public const float PathSampleStep  = 0.05f; // how often the dot's position is recorded

    // ---- Timer audio cues ----
    public const int   TimerWarnAt     = 10;    // one warning cue when this many seconds remain
    public const int   TimerTickFrom   = 5;     // tick every second at/under this many remaining

    // ---- Sonar (ranges lerped across the ramp) ----
    public const int   MaxPings           = 4;     // must match MAX_PINGS in SonarWall.shader
    public const float PingCooldown       = 1.1f;  // can't fire another ping until the last reveal finishes
    public const float RingSpeedStart     = 6.5f;
    public const float RingSpeedEnd       = 8.0f;
    public const float FadeStart          = 2.2f;  // easy: walls linger
    public const float FadeEnd            = 1.0f;  // hard: blink and it's gone
    public const float BandStart          = 0.18f; // easy: fat readable flash
    public const float BandEnd            = 0.09f; // hard: thin flash
    public const float FlashBoost         = 1.7f;  // white bloom strength at the ring front
    public const float FadeRampLevels     = 10f;   // levels to reach the hardest settings
    public const float RingLife           = 1.1f;  // ring visual lifetime
    public const float OriginFlashTime    = 0.22f; // bright burst at the ping origin
    public const float OriginFlashScale   = 1.5f;
    public const float PingPitchJitter    = 0.06f;
    public const float TickThrottle       = 0.028f;
    public const float TickPitchJitter    = 0.18f;
    public const float NearRevealRadius   = 1.8f;  // wall revealed within this of player = medium haptic
    public const float NearHapticThrottle = 0.09f;
    public const float PingDarkenAmount   = 0.14f; // brief screen darken as the ring bursts
    public const float PingDarkenTime     = 0.18f;

    // ---- Reveals budget ----
    // Reveals are the scarce resource now that score is gone. They were generous enough that
    // players were clearing levels with pings still in hand, which made the whole sonar premise
    // optional — the squeeze below is what puts the "spend one to see, or trust your memory"
    // decision back in every level.
    public const int   TutorialPingBonus  = 3;     // +3,+2,+1 on levels 1..3
    public const int   RubberBandFails    = 2;     // after this many fails on a level...
    public const int   RubberBandPings    = 2;     // ...quietly grant this many extra pings
    public const float PingBudgetScale    = 0.72f; // fraction of grid size handed out as reveals

    // ---- Hull = lives ----
    // These were called stars, and in a puzzle game "stars" means a level rating — so players
    // read the row as a score they were losing rather than as lives. They are HULL now: three
    // plates per level, one cracked per hazard hit, one repaired by a bonus echo. Stars are
    // back to meaning what everyone expects: the 0-3 grade you earn for a clear.
    public const int   StartHull          = 3;
    public const int   MaxHull            = 3;     // the normal ceiling
    public const int   DecoyHullCost      = 1;
    public const float DecoyTimePenalty   = 2f;    // seconds burned off the clock per hit
    public const int   BonusOrbHull       = 1;

    /// <summary>
    /// A bonus echo collected with the hull already full adds a fourth, gold REINFORCED plate
    /// instead of being wasted. A reward that sometimes pays nothing trains players to ignore it.
    /// The plate carries into the next level, so it is something worth protecting.
    /// </summary>
    public const int   ReinforcedHull     = 4;

    // ---- Loop timing ----
    // 45s was comfortable enough that testers were finishing with time to spare on levels the
    // curve considered hard. The clock is the pressure; it needs to bite.
    public const float LevelTimeLimit     = 34f;   // <=0 disables the fail timer
    public const float CelebrationTime    = 1.5f;  // auto-advance delay after a clear

    // ---- The clock, sector by sector ----
    // A flat 34s stopped mattering after the first sector: a recorded run cleared 10 of 12 levels
    // with 24-31s left, and both of its fails were hull damage, never time. Drag movement is fast
    // (that run covered ~60-cell routes in under ten seconds), so the clock was never limiting the
    // moving — only the reading and deciding, and it gave far too much of that. Each sector now
    // takes a share of the base clock away. SHALLOWS keeps all of it: it is where people learn.
    // Tune here; one entry per sector of the first lap, the last entry holds from then on.
    public static readonly float[] SectorClock = { 1.00f, 0.86f, 0.76f, 0.70f, 0.66f, 0.64f, 0.64f, 0.64f };

    /// <summary>The base clock never drops below this, whatever the sector and the deep squeeze
    /// compound to — rule bonuses still go on top.</summary>
    public const float MinTimeLimit       = 22f;

    /// <summary>Extra clock after RubberBandFails straight fails on one level, alongside the
    /// extra reveals: a tighter clock must not become a wall.</summary>
    public const float RubberBandTimeScale = 1.2f;

    public static float SectorClockScale(int level) =>
        SectorClock[Mathf.Min(SectorIndex(level), SectorClock.Length - 1)];
    public const float HitstopTime        = 0.15f; // freeze on exit reached

    // ---- Twists (introduced as you climb) ----
    // Pulled forward deliberately. Under the old numbers nothing new happened on levels 3, 4, 6
    // or 7 — the maze just gained a row — and that dead stretch is exactly where playtesters
    // said the game "got uninteresting". A twist now lands on level 3, and the next opens the
    // second sector, so no player sits through two identical levels in a row.
    public const int   DecoyStartLevel    = 3;
    public const int   DecoyEveryLevels   = 3;   // was 4 — decoys stack up faster
    public const int   MaxDecoys          = 5;   // was 3; now the main source of difficulty

    /// <summary>
    /// From this level, decoys are placed in PAIRS that blink half a cycle apart instead of on
    /// independent random offsets.
    ///
    /// A decoy is only solid for about a quarter of its cycle, so two random ones frequently
    /// clear at the same time and hand the player one long, lazy window — crossing becomes a
    /// matter of waiting rather than timing. Pinning a pair in antiphase guarantees their danger
    /// never coincides, which spreads it across the cycle and halves the safe window into two
    /// shorter, *regular* gaps. The corridor stays passable and the answer stays learnable; it
    /// just has to be read rather than waited out.
    ///
    /// Deliberately NOT applied to every decoy at once: five decoys spread evenly would put
    /// something solid at almost every instant. Pairs gate a stretch of corridor; the rest stay
    /// independent.
    /// </summary>
    public const int   DecoyGateLevel     = 12;
    // Level 6, not 5: the sector-1 finale is already the difficulty peak, and stacking a brand
    // new mechanic on top of the hardest level a new player has seen is how you lose them. It
    // debuts on the RELIEF level right after instead, where there is room to learn it.
    public const int   MovingExitLevel    = 6;
    public const float ExitMoveInterval   = 2.6f;
    public const float ExitMoveLerp       = 2.2f;
    public const float DecoyFadeSpeed     = 2.1f;  // fade in/out rate; slower = easier to time a crossing
    public const float DecoyMaxAlpha      = 0.95f; // peak visibility
    public const float DecoyVisibleHit    = 0.5f;  // only collidable once this visible (time your run!)
    // Hit radius and drawn size, as a fraction of a cell.
    //
    // Both raised: at 0.40 a player could hug the outer wall through a corner and slip past a
    // decoy sitting in the middle of the cell, so the hazard was dodgeable by hugging geometry
    // rather than by timing the fade — which is the decision it exists to create.
    public const float DecoyHitRadius     = 0.52f; // was 0.40
    public const float DecoyDrawScale     = 0.72f; // base drawn size (was 0.55)
    public const float DecoyDrawPulse     = 0.22f; // extra size at full visibility
    public const float DecoyHitBlackout   = 1.0f;  // after taking a ping it blinks out so you can pass
    public const int   DecoyPingCost      = 1;     // "search taps" lost on a visible hit

    // ---- Camera / presentation ----
    public const float CameraPadding      = 0.9f;
    public const float ShakeMagnitude     = 0.28f;
    public const float ShakeDuration      = 0.35f;
    public const float PunchZoom          = 0.12f; // fraction to zoom in on clear
    public const float PunchZoomTime      = 0.45f;

    // ---- Exit near-miss glow ----
    public const float ExitPulseBaseAlpha = 0.24f;
    public const float ExitPulseAmp       = 0.28f;
    public const float ExitPulseSpeed     = 2.0f;
    public const float ExitNearRadius     = 4.0f;  // brighten the exit as player gets within this
    public const float ExitNearMaxBoost   = 0.55f;

    // ---- Sectors (chapters) ----
    // Progression needs a big beat every ~10-15 min, not just the per-level one. Levels are grouped
    // into named sectors that re-tint the whole maze and pay a bonus on completion.
    public const int   LevelsPerSector    = 5;
    public static readonly string[] SectorNames =
        { "SHALLOWS", "THE DEEP", "DRIFT", "THE HOLLOW", "ECHO CORE",
          "LONG DARK", "STILLWATER", "UNDERTOW" };
    // UNDERTOW was THE LATTICE: the one name with no water in it, and the sector whose new rule
    // is a current — a lattice of currents is a stretch, an undertow is the thing itself.
    public static readonly Color[] SectorWallColors =
    {
        new Color(0.55f, 0.80f, 1.00f, 1f), // blue-white
        new Color(0.45f, 1.00f, 0.85f, 1f), // teal
        new Color(0.80f, 0.65f, 1.00f, 1f), // violet
        new Color(1.00f, 0.70f, 0.45f, 1f), // amber
        new Color(1.00f, 0.45f, 0.55f, 1f), // rose
        new Color(0.60f, 0.70f, 1.00f, 1f), // indigo
        new Color(0.70f, 0.95f, 0.80f, 1f), // sea green
        new Color(0.95f, 0.85f, 0.60f, 1f), // sand
    };

    // ---- Deep progression (past the authored ramp) ----
    // The run is endless, so difficulty must be too. Levels 1..DeepStartLevel keep the hand-tuned
    // ramp untouched; past it every knob keeps moving but converges on a floor, so the game gets
    // relentlessly harder without ever becoming unwinnable.
    public const int   DeepStartLevel     = 13;   // where the authored ramp tops out
    // Difficulty deliberately CONVERGES rather than rising forever — past this ramp every knob has
    // reached its floor and the run stays hard-but-winnable indefinitely. Escalating without limit
    // would just produce an unwinnable wall. What stays endless is the run itself: fresh mazes,
    // climbing level count, and sector names that keep advancing (SHALLOWS -> SHALLOWS II -> ...).
    public const float DeepRampLevels     = 60f;  // levels taken to reach full deep pressure
    public const int   DeepMaxMazeSize    = 16;   // hard cap: past this, cells are too small to read
    public const int   DeepSizeStep       = 8;    // +1 grid row every N levels in the deep phase
    public const float SecondsPerExtraRow = 3.5f; // bigger grids buy time so they stay fair...
    public const float DeepTimeSqueeze    = 0.72f;// ...and the deep ramp claws it back
    public const float DeepPingSqueeze    = 0.78f;
    public const int   MinPings           = 5;    // never leave the player without a way to look
    public const float DeepExitInterval   = 1.7f; // moving exit gets twitchier
    public const float DeepDecoyFade      = 3.0f; // decoys blink faster = harder to time

    // ---- Bonus Echo orb (the variable reward) ----
    // Uncertain rewards hit harder than guaranteed ones, so this only appears some of the time and
    // only the sonar can find it — every ping becomes a small "did I hit gold?" pull.
    //
    // Now that it restores a life rather than paying points it has to be genuinely scarce: a
    // reliable extra star every other level would just cancel the decoys out.
    public const float BonusOrbChance     = 0.18f;  // fraction of levels that contain one
    public const float BonusOrbRadius     = 0.34f;  // pickup radius
    public const float BonusOrbPulseSpeed = 3.2f;

    // ---- Near-miss / clutch ----
    public const float FailRevealTime     = 1.8f;   // maze lights up on a timeout so you see how close
    public const float ClutchSeconds      = 3f;     // clearing with less than this left = CLUTCH

    // ---- Daily streak ----
    public const int   DailyBonusPingsMax = 3;      // extra pings on your first level of the day

    // ---- Tutorial ----
    public const float TutorialDragDistance = 1.6f; // world units the player must drag to pass step 1

    /// <summary>
    /// Seconds after the tutorial ends before a player who still hasn't pinged is told to. Long
    /// enough that anyone who was about to ping anyway does it first and never sees the card.
    /// </summary>
    public const float PingNudgeDelay = 5f;

    // ---- Callouts (teach by doing) ----
    // The old explainers were cards with an OK button that stayed locked for three seconds, and a
    // first-time player sat through six of them before level 1 began. Now the game pauses only at
    // the moment something new actually turns up, says one line about it, and any tap carries on.
    // The guard is not a countdown: it only stops the same touch that was steering the dot from
    // dismissing the line before it has been drawn.
    public const float CalloutGuard        = 0.35f;
    public const float DecoyCalloutRadius  = 2.4f;   // a decoy this close (and lit) is "met"
    public const float GateCalloutRadius   = 2.6f;
    public const float FastFadeCalloutLag  = 0.25f;  // after the first reveal has visibly died
    public const float HudPulseSeconds     = 2.4f;

    // ---- Exit beacon (sound as information) ----
    // The exit ticks like a parking sensor: panned toward it, faster and brighter as you close in,
    // so a player who has lost the picture can still steer by ear.
    public const float BeaconIntervalFar   = 1.9f;
    public const float BeaconIntervalNear  = 0.75f;
    public const float BeaconRange         = 7f;     // world units over which it ramps

    // ---- Performance ----
    public const int   TargetFrameRate    = 60;

    // ---- Colors ----
    public static readonly Color BackgroundColor = new Color(0.015f, 0.02f, 0.035f, 1f);
    public static readonly Color WallGlowColor   = new Color(0.55f, 0.8f, 1.0f, 1f);
    public static readonly Color PlayerColor     = new Color(0.6f, 0.9f, 1.0f, 1f);
    public static readonly Color ExitColor       = new Color(0.4f, 1.0f, 0.7f, 1f);
    public static readonly Color RingColor       = new Color(0.6f, 0.85f, 1.0f, 1f);
    public static readonly Color DecoyColor      = new Color(1.0f, 0.5f, 0.35f, 1f); // warm = "not the exit"
    public static readonly Color DecoyRingColor  = new Color(1.0f, 0.22f, 0.22f, 1f); // red highlight outline
    public static readonly Color StreakColor     = new Color(1.0f, 0.6f, 0.15f, 1f); // flame
    public static readonly Color BonusOrbColor   = new Color(1.0f, 0.85f, 0.25f, 1f); // gold reward
    public static readonly Color HunterColor     = new Color(1.0f, 0.18f, 0.32f, 1f); // crimson eyes
    public static readonly Color GateColor       = new Color(0.55f, 0.95f, 1.0f, 1f); // ice-white bars (open)
    public static readonly Color GateShutColor   = new Color(1.0f, 0.32f, 0.36f, 1f); // red outline (shut)
    public static readonly Color StillHazeColor  = new Color(0.010f, 0.025f, 0.060f, 1f); // murk
    public static readonly Color StillEdgeColor  = new Color(0.45f, 0.62f, 0.85f, 1f);
    public static readonly Color CurrentColor    = new Color(0.55f, 0.90f, 0.95f, 1f);

    // ---- Level grade ----
    // Fourteen rotating praise words told the player nothing: "SLICK" and "SUPERB" were the same
    // outcome, and there was no way to know what would have earned "FLAWLESS". A clear now earns
    // up to three stars, one per stated rule, and the rules are printed under the stars every
    // time so the player always knows what the missing one was for.
    //
    //   ★  EXIT          reach the exit
    //   ★  NO DAMAGE     no hull plate cracked on the way
    //   ★  ≤ N REVEALS   used no more than half the level's reveals
    //
    // Half the budget is the par because the budget is what the level is tuned around: a player
    // who needs every reveal finished, one who needed half of them read the maze.
    public static readonly string[] GradeWords = { "", "CLEARED", "SHARP", "PERFECT" };
    public const float RevealParFraction = 0.5f;

    /// <summary>Most reveals a clear may spend and still earn the reveal star.</summary>
    public static int RevealPar(int basePings) => Mathf.Max(2, Mathf.CeilToInt(basePings * RevealParFraction));

    /// <summary>0..3. Exit is always earned on a clear; the other two are independent.</summary>
    public static int GradeFor(bool cleared, bool noDamage, int revealsUsed, int par)
    {
        if (!cleared) return 0;
        return 1 + (noDamage ? 1 : 0) + (revealsUsed <= par ? 1 : 0);
    }

    // ---- Sector helpers ----
    public static int SectorIndex(int level) => Mathf.Max(0, (level - 1) / LevelsPerSector);

    /// <summary>
    /// Sector label. The name list cycles, but each full lap appends a numeral ("SHALLOWS II"),
    /// so a player 200 levels deep still sees a name they have never seen before rather than
    /// silently looping back to level 1's.
    /// </summary>
    public static string SectorName(int level)
    {
        int idx = SectorIndex(level);
        string name = SectorNames[idx % SectorNames.Length];
        int lap = idx / SectorNames.Length;
        return lap == 0 ? name : name + " " + Numeral(lap + 1);
    }

    /// <summary>Roman numerals for the sector lap; falls back to digits well past any real run.</summary>
    private static string Numeral(int n)
    {
        switch (n)
        {
            case 2:  return "II";
            case 3:  return "III";
            case 4:  return "IV";
            case 5:  return "V";
            case 6:  return "VI";
            case 7:  return "VII";
            case 8:  return "VIII";
            case 9:  return "IX";
            case 10: return "X";
            default: return n.ToString();
        }
    }

    // ---- Difficulty sawtooth ----
    // Pressure rises across a sector, spikes on its finale, then drops at the start of the next
    // one — two steps forward, one back — instead of climbing in a single unbroken line.
    //
    // A monotone ramp reads as "the same level, slightly worse" and gives the player no moment of
    // feeling strong. Dropping back after a peak does two things: the opener plays as a reward
    // (the thing that beat you last level is now easy), and the contrast makes the NEXT climb
    // legible as a climb. Every trough still sits above the previous trough, so nothing is
    // actually lost — the trend is up, the shape is a sawtooth.
    //
    //   level  1  2  3  4  5 |  6  7  8  9 10 | 11 12 13 14 15
    //   eff    1  1  2  4  6 |  3  5  7  9 11 |  8 10 12 14 16
    public const float SectorRelief     = 3f;   // levels of pressure refunded at a sector opener
    public const float SectorFinaleBump = 1f;   // ...and borrowed back on its finale

    /// <summary>
    /// The level number the PRESSURE knobs should behave as, once the sawtooth is applied.
    ///
    /// Used only for how hard the game reads — fade, ring speed, flash band, twist intensity.
    /// Structural growth (grid size, ping budget) and the level at which a mechanic first appears
    /// deliberately keep using the real level: a maze that shrank back, or a decoy that vanished
    /// for a level after being introduced, would read as the game glitching rather than easing off.
    /// </summary>
    public static float EffectiveLevel(int level)
    {
        // 0 at a sector opener, 1 on its finale.
        float s = LevelsPerSector > 1
            ? (LevelInSector(level) - 1) / (float)(LevelsPerSector - 1)
            : 1f;

        // The first sector gets no relief. There is nothing behind it to be relieved FROM, and
        // subtracting three levels of pressure from levels that are already the gentlest in the
        // game just flattens them into each other — levels 1 and 2 came out bit-for-bit identical,
        // which is the very sameness this curve exists to break up. Sector 1 keeps its authored
        // ramp; the sawtooth starts biting once there is a peak to fall from.
        float relief = SectorIndex(level) == 0 ? 0f : SectorRelief;

        return Mathf.Max(1f, level - relief * (1f - s) + SectorFinaleBump * s);
    }

    public static Color SectorWallColor(int level) => SectorWallColors[SectorIndex(level) % SectorWallColors.Length];
    /// <summary>True on the last level of a sector (i.e. clearing it completes the sector).</summary>
    public static bool IsSectorFinale(int level) => level % LevelsPerSector == 0;
    /// <summary>1-based position within the current sector, for "3/5" style readouts.</summary>
    public static int LevelInSector(int level) => ((level - 1) % LevelsPerSector) + 1;

    /// <summary>
    /// How many levels must still be CLEARED, counting this one, before a new sector begins.
    ///
    /// This is the ladder's anticipation hook. "You are on level 23" is a status; "THE HOLLOW in
    /// 3" is a reason to keep playing right now. All the data was already here — it was simply
    /// never shown until the moment of arrival, which spends the interest instead of building it.
    /// </summary>
    public static int LevelsToNextSector(int level) => LevelsPerSector - LevelInSector(level) + 1;

    /// <summary>The name of the sector the player is working toward.</summary>
    public static string NextSectorName(int level) => SectorName(level + LevelsToNextSector(level));

    /// <summary>
    /// Build the difficulty profile for a given level. <paramref name="failStreak"/> is the
    /// number of consecutive fails on THIS level, used for rubber-banding.
    /// </summary>
    public static Difficulty GetDifficulty(int level, int failStreak)
    {
        var d = GetBaseDifficulty(level, failStreak);
        ApplyRules(ref d, RulesFor(level), level);
        return d;
    }

    /// <summary>The level's tuning before any sector rule is applied.</summary>
    private static Difficulty GetBaseDifficulty(int level, int failStreak)
    {
        var d = new Difficulty();

        // How hard this level should FEEL, after the sector sawtooth. Everything that governs
        // pressure reads from this; everything structural still reads from `level`.
        float eff = EffectiveLevel(level);

        // 0 until the authored ramp is done, then 0..1 across DeepRampLevels. Every deep knob
        // below is a Lerp on this, so they all converge instead of running away.
        float deepT = Mathf.Clamp01((eff - DeepStartLevel) / DeepRampLevels);

        // ---- Grid ----
        d.mazeSize = Mathf.Clamp(StartMazeSize + (level - 1) / LevelsPerSizeStep,
                                 StartMazeSize, MaxMazeSize);
        if (level > DeepStartLevel)
        {
            // Keeps growing past the authored cap, but far more slowly and to a hard ceiling —
            // beyond DeepMaxMazeSize the cells are too small to read on a phone.
            d.mazeSize = Mathf.Min(DeepMaxMazeSize,
                                   d.mazeSize + (level - DeepStartLevel) / DeepSizeStep);
        }

        // ---- Reveals ----
        // Reveals scale with grid size but no longer one-for-one: at PingBudgetScale a 12x12 maze
        // buys 9 looks rather than 12, so a bigger maze means proportionally LESS of it you can
        // afford to light up. This is the "reveals should be less" lever.
        int earlyBonus = Mathf.Max(0, TutorialPingBonus - (level - 1));
        int rubber = failStreak >= RubberBandFails ? RubberBandPings : 0;
        d.pings = Mathf.RoundToInt(d.mazeSize * PingBudgetScale) + earlyBonus + rubber;
        if (level > DeepStartLevel)
        {
            // Squeezed relative to grid size, so a bigger maze does NOT hand out proportionally
            // more sight — the deeper you go, the less of the maze one run can afford to light up.
            d.pings = Mathf.Max(MinPings, Mathf.RoundToInt(d.pings * Mathf.Lerp(1f, DeepPingSqueeze, deepT)));
        }

        // 0 at level 1, 1 at the top of the authored ramp. The single biggest lever on how hard
        // the game feels, so this is where the sawtooth is felt most: walls linger noticeably
        // longer on a sector opener than they did on the finale the player just survived.
        float ramp = Mathf.Clamp01((eff - 1f) / FadeRampLevels);
        d.fade      = Mathf.Lerp(FadeStart, FadeEnd, ramp);
        d.ringSpeed = Mathf.Lerp(RingSpeedStart, RingSpeedEnd, ramp);
        d.band      = Mathf.Lerp(BandStart, BandEnd, ramp);

        // ---- Clock ----
        d.timeLimit = LevelTimeLimit;
        if (level > DeepStartLevel)
        {
            // Extra grid rows buy seconds (a 16x16 in 45s would be unwinnable, not hard), and the
            // deep ramp then takes a cut back off the top.
            // Clamped at zero: this is a bonus for oversized grids, never a penalty for small
            // ones. Unclamped it went negative the moment MaxMazeSize was raised above the grid
            // this level actually gets, which quietly docked level 14 seven seconds.
            float sizeBonus = Mathf.Max(0f, (d.mazeSize - MaxMazeSize) * SecondsPerExtraRow);
            d.timeLimit = (LevelTimeLimit + sizeBonus) * Mathf.Lerp(1f, DeepTimeSqueeze, deepT);
        }
        // Per-sector tightening, floored, and loosened again for a player who keeps failing the
        // same level. Applied to the base clock only: the sector rules add their own seconds on
        // top (ApplyRules), because a gate or a hunter costs time that skill can't buy back.
        d.timeLimit = Mathf.Max(MinTimeLimit, d.timeLimit * SectorClockScale(level));
        if (failStreak >= RubberBandFails) d.timeLimit *= RubberBandTimeScale;

        // ---- Twists ----
        // Whether a twist EXISTS keys off the real level; how much of it there is follows the
        // sawtooth. A mechanic that was introduced on level 3 and then quietly disappeared on
        // level 6 would read as the game breaking, not as it easing off — so the count is only
        // ever relieved down to one, never to none.
        d.movingExit = level >= MovingExitLevel;
        d.exitMoveInterval = Mathf.Lerp(ExitMoveInterval, DeepExitInterval, deepT);

        d.decoyCount = level >= DecoyStartLevel
            ? Mathf.Clamp(1 + Mathf.FloorToInt((eff - DecoyStartLevel) / DecoyEveryLevels), 1, MaxDecoys)
            : 0;
        d.decoyFadeSpeed = Mathf.Lerp(DecoyFadeSpeed, DeepDecoyFade, deepT);

        return d;
    }

    // =========================================================================================
    //  Sector rules
    // =========================================================================================
    // A playtester cleared 34 levels in ten minutes with one fail: past DRIFT nothing new
    // happened, the knobs just kept turning. Each sector from THE HOLLOW on now brings one rule
    // the player has to think about differently, introduced alone on its own five levels and
    // mixed back in afterwards so the earlier ideas don't simply vanish.

    /// <summary>First sector (0-based) that carries a rule of its own — THE HOLLOW.</summary>
    public const int FirstRuleSector = 3;

    /// <summary>The rule each sector introduces, by 0-based sector index within a lap.</summary>
    public static LevelRule SectorRule(int sectorInLap)
    {
        switch (sectorInLap)
        {
            case 3: return LevelRule.Hunter;
            case 4: return LevelRule.Gates;
            case 5: return LevelRule.FastFade;
            case 6: return LevelRule.StillWater;
            case 7: return LevelRule.Currents;
            default: return LevelRule.None;
        }
    }

    /// <summary>
    /// Which rules a level carries. Deterministic in the level number, so a retry of level 27 is
    /// the same kind of level with a new layout rather than a different game.
    ///
    ///   first lap  : a sector's own rule on all five of its levels, plus — once a later sector is
    ///                reached — a one-in-four chance of ONE earlier rule returning (never on the
    ///                sector opener, which is where the new rule is met on its own).
    ///   later laps : every level draws one rule, and a second one a third of the time.
    /// </summary>
    public static LevelRule RulesFor(int level)
    {
        int sector = SectorIndex(level);
        int lap = sector / SectorNames.Length;
        int inLap = sector % SectorNames.Length;

        if (lap == 0)
        {
            if (inLap < FirstRuleSector) return LevelRule.None;
            LevelRule own = SectorRule(inLap);
            if (inLap > FirstRuleSector && LevelInSector(level) > 1 && Hash01(level, 11) < 0.25f)
            {
                int pick = FirstRuleSector + Mathf.FloorToInt(Hash01(level, 23) * (inLap - FirstRuleSector));
                own |= SectorRule(pick);
            }
            return own;
        }

        LevelRule r = SectorRule(inLap);
        if (r == LevelRule.None)
            r = SectorRule(FirstRuleSector + Mathf.FloorToInt(Hash01(level, 31) * 5f));
        if (Hash01(level, 47) < 0.34f)
            r |= SectorRule(FirstRuleSector + Mathf.FloorToInt(Hash01(level, 59) * 5f));
        return r;
    }

    /// <summary>Stable pseudo-random 0..1 from (key, salt). No System.Random, no state.</summary>
    public static float Hash01(int key, int salt)
    {
        unchecked
        {
            uint h = (uint)(key * 374761393 + salt * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (float)0x1000000;
        }
    }

    // ---- per-rule tuning ----
    public const float HunterSpeedStart   = 0.85f;  // cells / second at its debut
    public const float HunterSpeedEnd     = 1.2f;   // ...and deep in the run (player is ~1.2 u/s)
    public const float HunterHitRadius    = 0.42f;  // fraction of a cell
    public const int   HunterLairMinSteps = 6;      // spawns at least this far (by route) from you
    public const float HunterTimeBonus    = 3f;     // detours cost time
    public const float HunterGlowRadius   = 2.6f;   // eyes show faintly within this, lit or not
    public const float HunterStunSeconds  = 1.4f;   // back in its lair, still, after a bite
    public const float HunterGraceSeconds = 1.2f;   // no second bite while the first is landing

    public const float GatePeriodStart    = 4.4f;   // seconds per open+shut cycle
    public const float GatePeriodEnd      = 3.2f;
    public const float GateOpenFraction   = 0.5f;
    public const float GateTimeBonus      = 5f;     // waiting on a gate is the point, not a tax
    public const float GateTelegraph      = 0.5f;   // seconds of flicker before a gate shuts
    public const float GateIdleGlow       = 0.22f;  // a gate is never fully dark: the beat must be readable

    public const float FastFadeScale      = 0.42f;  // LONG DARK: walls keep 42% of their linger
    public const float FastFadeBandScale  = 0.8f;
    public const int   FastFadePingBonus  = 1;

    public const float StillZoneRadius    = 1.55f;  // world units (cells)
    public const float StillTimeBonus     = 4f;
    public const float StillLightRadius   = 0.95f;  // inside still water you see only this far

    public const float CurrentSpeedStart  = 0.42f;  // world units / s — always slower than you
    public const float CurrentSpeedEnd    = 0.62f;
    public const int   CurrentMinRun      = 3;      // shortest straight corridor that gets one
    public const float CurrentTimeBonus   = 3f;     // swimming upstream costs time skill can't buy back

    /// <summary>Fill in the rule payload. Counts and speeds follow the level's place in its sector,
    /// so a sector opener meets its rule gently and the finale meets it hard.</summary>
    private static void ApplyRules(ref Difficulty d, LevelRule rules, int level)
    {
        d.rules = rules;
        if (rules == LevelRule.None) return;

        float s = LevelsPerSector > 1 ? (LevelInSector(level) - 1) / (float)(LevelsPerSector - 1) : 1f;
        float deep = Mathf.Clamp01((level - 16) / 60f);    // keeps tightening on later laps

        if ((rules & LevelRule.Hunter) != 0)
        {
            d.hunterCount = level >= 60 && s > 0.6f ? 2 : 1;
            d.hunterSpeed = Mathf.Lerp(HunterSpeedStart, HunterSpeedEnd, Mathf.Max(s * 0.6f, deep));
            d.timeLimit += HunterTimeBonus;
        }
        if ((rules & LevelRule.Gates) != 0)
        {
            d.gateCount  = 2 + Mathf.RoundToInt(s * 2f);  // 2..4 on the route
            d.gatePeriod = Mathf.Lerp(GatePeriodStart, GatePeriodEnd, Mathf.Max(s * 0.7f, deep));
            d.timeLimit += GateTimeBonus;
        }
        if ((rules & LevelRule.FastFade) != 0)
        {
            d.fade *= FastFadeScale;
            d.band *= FastFadeBandScale;
            d.pings += FastFadePingBonus;
        }
        if ((rules & LevelRule.StillWater) != 0)
        {
            d.stillZones = 1 + Mathf.RoundToInt(s * 1.5f);   // 1..3 (the shader holds 4)
            d.timeLimit += StillTimeBonus;
        }
        if ((rules & LevelRule.Currents) != 0)
        {
            d.currentCount = 2 + Mathf.RoundToInt(s * 2f);
            d.currentSpeed = Mathf.Lerp(CurrentSpeedStart, CurrentSpeedEnd, Mathf.Max(s, deep));
            d.timeLimit += CurrentTimeBonus;
        }

        // The rule IS the difficulty in these sectors. Stacking the full decoy count on top of a
        // hunter or a gate turns a new idea into noise, so make room for it.
        if (d.decoyCount > 2) d.decoyCount -= 1;
    }

    /// <summary>The level's rules as one caption line, for the level select. Empty if none.</summary>
    public static string RuleSummary(LevelRule r)
    {
        if (r == LevelRule.None) return "";
        var sb = new System.Text.StringBuilder();
        if ((r & LevelRule.Hunter) != 0)     Append(sb, "HUNTER");
        if ((r & LevelRule.Gates) != 0)      Append(sb, "GATES");
        if ((r & LevelRule.FastFade) != 0)   Append(sb, "FAST FADE");
        if ((r & LevelRule.StillWater) != 0) Append(sb, "STILL WATER");
        if ((r & LevelRule.Currents) != 0)   Append(sb, "CURRENTS");
        return sb.ToString();
    }

    private static void Append(System.Text.StringBuilder sb, string t)
    {
        if (sb.Length > 0) sb.Append("  +  ");
        sb.Append(t);
    }

    // =========================================================================================
    //  Descent — the endless mode
    // =========================================================================================
    // The trailer promises endless mazes; the ladder is technically unbounded, but past level 40 it
    // is the same eight sectors with numerals on. Descent is the honest version: one hull for the
    // whole dive, maze after maze, deeper until something breaks it. Depth is the score.
    public const int   DescentMetresPerMaze = 100;
    public const int   DescentStartLevel    = 4;   // skips the tutorial-gentle opening mazes
    public const int   DescentLevelStep     = 2;   // every maze dives this many "levels" deeper
    public const int   DescentRuleFromMaze  = 3;   // rules start mixing in from this maze
    public const int   DescentRepairEvery   = 5;   // one hull plate back every N mazes survived
    public const int   DescentUnlockLevel   = LevelsPerSector + 1;  // clear sector 1 first
    /// <summary>
    /// Running out of time in Descent costs a plate and re-rolls the maze at the same depth rather
    /// than ending the dive. The hull is the one thing a dive spends, so it should be the one thing
    /// that ends it — a single slow maze wiping out a 2,000 m run would read as a cheap shot.
    /// </summary>
    public const int   DescentTimeoutCost   = 1;

    /// <summary>The ladder level whose tuning Descent maze <paramref name="index"/> (0-based) uses.</summary>
    public static int DescentLevel(int index) => DescentStartLevel + index * DescentLevelStep;

    /// <summary>"1,400 m" — always with a comma, whatever the phone's locale, so a shared result
    /// reads the same for everyone who sees it.</summary>
    public static string DepthText(int mazesCleared) =>
        (mazesCleared * DescentMetresPerMaze).ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " m";

    /// <summary>Rules for a Descent maze: none for the first few, then one, later sometimes two.</summary>
    public static LevelRule DescentRules(int index, int seed)
    {
        if (index < DescentRuleFromMaze) return LevelRule.None;
        LevelRule r = SectorRule(FirstRuleSector + Mathf.FloorToInt(Hash01(seed + index, 71) * 5f));
        if (index >= 8 && Hash01(seed + index, 83) < 0.4f)
            r |= SectorRule(FirstRuleSector + Mathf.FloorToInt(Hash01(seed + index, 97) * 5f));
        return r;
    }

    /// <summary>
    /// Difficulty for a Descent maze: the ladder's tuning for the equivalent level, with Descent's
    /// own rule draw in place of the ladder's sector rule — the dive mixes every idea rather than
    /// marching through them in sector order.
    /// </summary>
    public static Difficulty GetDescentDifficulty(int index, int seed)
    {
        int lvl = DescentLevel(index);
        var d = GetBaseDifficulty(lvl, 0);
        ApplyRules(ref d, DescentRules(index, seed), lvl);
        return d;
    }

    // =========================================================================================
    //  Accessibility / presentation settings
    // =========================================================================================
    /// <summary>Glow multipliers for the BRIGHTNESS setting. Outdoors the default is close to
    /// invisible on a phone; the top step is deliberately loud.</summary>
    public static readonly float[] BrightnessGain  = { 1.0f, 1.35f, 1.8f };
    public static readonly string[] BrightnessName = { "NORMAL", "BRIGHT", "MAX" };

    /// <summary>
    /// Sonar colours: the dot, the ping ring and the walls a ping reveals. Index 0 (STANDARD) keeps
    /// each sector's own palette; the rest are earned one per sector cleared — the reward for
    /// finishing a sector is a colour named after the place you got through.
    /// </summary>
    public static Color SonarColour(int index)
    {
        if (index <= 0) return PlayerColor;
        return EarnedSonarColours[(index - 1) % EarnedSonarColours.Length];
    }

    /// <summary>
    /// One per sector, in sector order, spread round the hue wheel. Deliberately NOT the sector
    /// wall tints: several of those are near neighbours (SHALLOWS and LONG DARK are both pale
    /// blue), which made two "different" rewards look the same on the next ping.
    /// </summary>
    private static readonly Color[] EarnedSonarColours =
    {
        new Color(0.35f, 0.75f, 1.00f, 1f), // SHALLOWS   — sky
        new Color(0.20f, 1.00f, 0.78f, 1f), // THE DEEP   — aqua
        new Color(0.76f, 0.50f, 1.00f, 1f), // DRIFT      — violet
        new Color(1.00f, 0.62f, 0.25f, 1f), // THE HOLLOW — amber
        new Color(1.00f, 0.38f, 0.58f, 1f), // ECHO CORE  — rose
        new Color(0.46f, 0.46f, 1.00f, 1f), // LONG DARK  — indigo
        new Color(0.68f, 1.00f, 0.42f, 1f), // STILLWATER — lime
        new Color(1.00f, 0.92f, 0.55f, 1f), // UNDERTOW   — sand
    };
    public static string SonarColourName(int index) =>
        index <= 0 ? "STANDARD" : SectorNames[(index - 1) % SectorNames.Length];

    // =========================================================================================
    //  Sharing
    // =========================================================================================
    /// <summary>Where a shared result points. Swap for a short link once one exists.</summary>
    public const string StoreUrl = "https://play.google.com/store/apps/details?id=com.vortexforgestudios.sonarfall";

    /// <summary>Daily #1 is 1 Sep 2026 — UTC day number 20697.</summary>
    public const int DailyEpochDay = 20697;
}

