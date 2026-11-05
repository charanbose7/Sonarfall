using System.Collections;
using UnityEngine;

/// <summary>
/// DESCENT — the endless mode.
///
/// The trailer says "endless mazes", and the ladder technically never ends, but past level 40 it
/// is the same eight sectors with numerals on. Descent is the honest version of that promise:
/// one hull for the whole dive, maze after maze, each a little deeper and a little harder, with
/// every sector rule mixed in, until something breaks the hull. Depth is the score — 100 m a maze.
///
///   * the hull carries from maze to maze; one plate is repaired every DescentRepairEvery mazes
///   * running out of time costs a plate and re-rolls the maze at the same depth
///     (GameConfig.DescentTimeoutCost) — the hull is the one thing that ends a dive
///   * no RETRY: a free re-roll would let a dive skip any maze it didn't like
///   * the result card shares as one line, like the Daily's
///
/// Leaderboards would need Play Games, i.e. network code and an account; for now the record is
/// the player's own deepest dive.
/// </summary>
public partial class GameManager
{
    private bool _isDescent;
    private int _descentIndex;     // mazes cleared so far this dive (also the depth, in 100 m)
    private int _descentSeed;      // varies the rule draw from dive to dive

    private void BeginDescent()
    {
        if (!SaveData.DescentUnlocked) return;
        StopAllCoroutines();
        Time.timeScale = 1f;
        _ui.HideFailPanel();
        _ui.HideResult();
        _ui.HideTeachCard();
        _ui.SetCover(0f);
        _levelBusy = false;
        _ui.SetResetInteractable(true);

        _isDaily = false;
        _isDescent = true;
        _descentIndex = 0;
        _descentSeed = Random.Range(1, 1 << 20);
        _failStreak = 0;
        _hull = GameConfig.StartHull;
        _dayStreak = SaveData.RegisterDailyVisit();
        _dailyBonusPending = false;    // the streak's bonus reveals belong to the ladder

        SetGameplayVisuals(true);
        _level = GameConfig.DescentLevel(_descentIndex);
        BuildLevel(_level);
        _ui.HideStart();
        _ui.SetRetryVisible(false);
        _ui.ShowInGameGear(true);
        State = GameState.Playing;
        _ui.ShowBanner("DESCENT\n<size=55%>one hull   ·   how deep can you go</size>", UIManager.Abyss, 1.4f);
        _audio.PlayRiser();
    }

    /// <summary>The dive's HUD: depth for the level number, the maze's rules under it.</summary>
    private void SetDescentHud(Color sectorColor)
    {
        string rules = GameConfig.RuleSummary(_profile.rules);
        string sub = "MAZE " + (_descentIndex + 1) + (rules.Length > 0 ? "   ·   " + rules : "");
        _ui.SetDescentHeader(_descentIndex, sub, sectorColor);
    }

    /// <summary>
    /// A maze cleared mid-dive: a short beat, not the ladder's full ceremony — the next maze is
    /// the reward. Every few mazes a plate is repaired, said in the same breath as the depth.
    /// </summary>
    private IEnumerator DescentClearRoutine()
    {
        _descentIndex++;
        bool repair = _descentIndex % GameConfig.DescentRepairEvery == 0 && _hull < GameConfig.MaxHull;
        if (repair)
        {
            _hull++;
            _ui.SetHull(_hull, GameConfig.MaxHull);
            _audio.PlayHullRepaired();
        }
        _ui.ShowBanner(GameConfig.DepthText(_descentIndex).ToUpperInvariant()
                       + (repair ? "\n<size=55%>HULL REPAIRED</size>" : ""), UIManager.Abyss, 0.6f);
        Haptics.Success();

        yield return new WaitForSecondsRealtime(0.85f);
        _ui.SetCover(1f);
        yield return new WaitForSecondsRealtime(0.22f);

        _level = GameConfig.DescentLevel(_descentIndex);
        BuildLevel(_level);
        State = GameState.Playing;
        _ui.SetCover(0f);
        if (_descentIndex % GameConfig.LevelsPerSector == 0) _audio.PlayRiser();   // the colour just changed
    }

    /// <summary>The clock ran out mid-dive: a plate for a fresh maze at the same depth.</summary>
    private void DescentTimeout()
    {
        if (DamageHull(UIManager.Abyss, GameConfig.DescentTimeoutCost)) return;   // last plate: the dive is over
        StartCoroutine(DescentRerollRoutine());
    }

    private IEnumerator DescentRerollRoutine()
    {
        State = GameState.Celebrating;
        _audio.SilenceWorld();
        _ui.ShowBanner("PRESSURE SURGE\n<size=55%>-1 plate   ·   new maze, same depth</size>", UIManager.Danger, 1.0f);
        yield return new WaitForSecondsRealtime(0.9f);
        _ui.SetCover(1f);
        yield return new WaitForSecondsRealtime(0.22f);
        BuildLevel(_level);
        State = GameState.Playing;
        _ui.SetCover(0f);
    }

    /// <summary>The end-of-dive card, shareable as one line.</summary>
    private void ShowDescentCard()
    {
        int mazes = _descentIndex;
        bool best = SaveData.RecordDescent(mazes) && mazes > 0;
        string depth = GameConfig.DepthText(mazes);
        string detail = (best ? "<color=#FFD15A>DEEPEST YET</color>\n" : "")
                      + mazes + " maze" + (mazes == 1 ? "" : "s") + " cleared"
                      + "      deepest  " + GameConfig.DepthText(SaveData.DescentBest);
        string share = "Sonarfall Descent · " + depth + " · " + mazes + " maze" + (mazes == 1 ? "" : "s")
                     + (best ? " · deepest yet" : "") + "\n" + GameConfig.StoreUrl;
        _ui.ShowResult("DIVE OVER", UIManager.Abyss, -1, depth, detail, share,
                       "DIVE AGAIN", () => AfterFirstRunPrompt(BeginDescent),
                       "MENU", ReturnToMenu);
    }
}
