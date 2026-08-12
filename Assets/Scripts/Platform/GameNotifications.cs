using System;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using Unity.Notifications.Android;
#endif
#if UNITY_IOS && !UNITY_EDITOR
using Unity.Notifications.iOS;
#endif

/// <summary>
/// Local notifications. Entirely on-device: nothing is registered with a push service, no token is
/// generated, no network call is made, and nothing about the player leaves the phone. That matters
/// because the store listing and privacy policy both state the app collects and transmits nothing,
/// and this must not quietly make either untrue.
///
/// Three messages, and never more than three outstanding:
///
///   1. DAILY      — tomorrow morning, "today's maze is live".
///   2. STREAK     — tomorrow evening, and ONLY if a streak is actually at risk. This is the one
///                   that works, because it is the only one telling the player they are about to
///                   lose something they already own.
///   3. WIN-BACK   — three days out, naming the exact level and sector they stopped on.
///
/// Everything is cancelled the moment the app opens and rescheduled when it goes to the background,
/// so a notification can never fire at someone who is already playing, and they can never stack up
/// from repeated sessions.
///
/// Design note on restraint: it would be easy to add "you were 2 tiles away!" and a nudge every
/// evening. Three well-timed messages that each say something true is the difference between a
/// reminder and a nuisance, and a player who mutes the app is worth less than one who uninstalls
/// it — at least the uninstall is honest feedback.
/// </summary>
public static class GameNotifications
{
    private const string ChannelId = "sonarfall_default";

    // Local hours-of-day the two daily messages aim for.
    private const int MorningHour = 10;   // "today's maze is live"
    private const int EveningHour = 20;   // "your streak ends tonight" — late enough to be urgent
    private const int WinBackDays = 3;

    private static bool _channelReady;

    /// <summary>Player-facing switch, mirrored in Settings alongside sound and haptics.</summary>
    public static bool Enabled
    {
        get => SaveData.NotificationsOn;
        set
        {
            SaveData.NotificationsOn = value;
            if (!value) CancelAll();
        }
    }

    // ---------------------------------------------------------------- permission

    /// <summary>
    /// Ask for notification permission. Deliberately NOT called at first launch: a permission
    /// dialog thrown at someone who has not yet played is the most common way to get a permanent
    /// "deny", and on Android 13+ a denial is sticky. GameManager calls this after the player's
    /// first level clear, when the app has earned the right to ask.
    /// </summary>
    public static void RequestPermission()
    {
        if (!Enabled) return;
#if UNITY_ANDROID && !UNITY_EDITOR
        // POST_NOTIFICATIONS only exists on API 33+. Below that, notifications are granted at
        // install and asking would throw.
        if (GetSdkInt() >= 33 &&
            !UnityEngine.Android.Permission.HasUserAuthorizedPermission("android.permission.POST_NOTIFICATIONS"))
        {
            UnityEngine.Android.Permission.RequestUserPermission("android.permission.POST_NOTIFICATIONS");
        }
#elif UNITY_IOS && !UNITY_EDITOR
        iOSNotificationCenter.RequestAuthorization(
            AuthorizationOption.Alert | AuthorizationOption.Badge | AuthorizationOption.Sound, true);
#endif
    }

    // ---------------------------------------------------------------- lifecycle

    /// <summary>Call when the app becomes active. Clears anything pending so nothing fires mid-play.</summary>
    public static void OnAppForeground()
    {
        CancelAll();
    }

    /// <summary>
    /// Call when the app goes to the background or quits. Rebuilds the whole schedule from current
    /// save state, so the messages always describe where the player actually is.
    /// </summary>
    public static void OnAppBackground()
    {
        CancelAll();
        if (!Enabled) return;

        EnsureChannel();

        int level = SaveData.CurrentLevel;
        int streak = SaveData.DayStreak;
        bool playedToday = SaveData.PlayedToday;

        // 1. Daily maze. Only worth sending if they have unlocked it.
        if (SaveData.RunFinished)
        {
            Schedule("Today's maze is live",
                     "One layout, one attempt, and every player in the world gets the same dark.",
                     NextLocal(MorningHour, playedToday ? 1 : 0));
        }

        // 2. Streak at risk — the only message with real stakes, so it gets the prime slot.
        //    Fires the evening of the day the streak would lapse. If they already played today the
        //    streak is safe until tomorrow night; if they have not, it is tonight.
        if (streak >= 2)
        {
            Schedule(streak + "-day streak ends tonight",
                     "Open Sonarfall before midnight to keep it alive.",
                     NextLocal(EveningHour, playedToday ? 1 : 0));
        }

        // 3. Win-back. Names the exact spot they left, which beats any generic "we miss you".
        string sector = GameConfig.SectorName(level);
        Schedule("Level " + level + "  ·  " + sector,
                 "The dark hasn't moved. Neither have you.",
                 DateTime.Now.AddDays(WinBackDays).Date.AddHours(MorningHour));
    }

    // ---------------------------------------------------------------- internals

    /// <summary>Next local occurrence of <paramref name="hour"/>, at least <paramref name="minDaysAhead"/> away.</summary>
    private static DateTime NextLocal(int hour, int minDaysAhead)
    {
        DateTime t = DateTime.Now.Date.AddDays(minDaysAhead).AddHours(hour);
        if (t <= DateTime.Now.AddMinutes(1)) t = t.AddDays(1);   // never schedule into the past
        return t;
    }

    private static void EnsureChannel()
    {
        if (_channelReady) return;
        _channelReady = true;
#if UNITY_ANDROID && !UNITY_EDITOR
        var channel = new AndroidNotificationChannel
        {
            Id = ChannelId,
            Name = "Sonarfall",
            Importance = Importance.Default,   // not High: this is a reminder, not an alarm
            Description = "Daily maze and streak reminders",
        };
        AndroidNotificationCenter.RegisterNotificationChannel(channel);
#endif
    }

    private static void Schedule(string title, string body, DateTime when)
    {
        if (when <= DateTime.Now) return;

#if UNITY_ANDROID && !UNITY_EDITOR
        var n = new AndroidNotification
        {
            Title = title,
            Text = body,
            FireTime = when,
            SmallIcon = "",           // falls back to the app icon when no custom icon is set
            LargeIcon = "",
        };
        AndroidNotificationCenter.SendNotification(n, ChannelId);
#elif UNITY_IOS && !UNITY_EDITOR
        var interval = when - DateTime.Now;
        if (interval.TotalSeconds < 1) return;
        var n = new iOSNotification
        {
            Title = title,
            Body = body,
            ShowInForeground = false,
            Trigger = new iOSNotificationTimeIntervalTrigger
            {
                TimeInterval = interval,
                Repeats = false,
            },
        };
        iOSNotificationCenter.ScheduleNotification(n);
#else
        // Editor and desktop: log it so the schedule can be inspected without a device.
        Debug.Log("[Sonarfall] (notification) " + when.ToString("ddd HH:mm") + "  " + title + " — " + body);
#endif
    }

    private static void CancelAll()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidNotificationCenter.CancelAllScheduledNotifications();
        AndroidNotificationCenter.CancelAllDisplayedNotifications();
#elif UNITY_IOS && !UNITY_EDITOR
        iOSNotificationCenter.RemoveAllScheduledNotifications();
        iOSNotificationCenter.RemoveAllDeliveredNotifications();
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    private static int _sdkInt = -1;
    private static int GetSdkInt()
    {
        if (_sdkInt > 0) return _sdkInt;
        try
        {
            using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                _sdkInt = version.GetStatic<int>("SDK_INT");
        }
        catch { _sdkInt = 0; }
        return _sdkInt;
    }
#endif
}
