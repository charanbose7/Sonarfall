using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

/// <summary>
/// Hand a line of text to the platform's share mechanism and report what happened.
///
///   Android  a plain ACTION_SEND intent wrapped in a chooser — the same thing every app's
///            "Share" button does. The game sends nothing anywhere and needs no INTERNET
///            permission: the app the player picks does the sending.
///   WebGL    Share.jslib: the Web Share sheet on mobile browsers, the async clipboard elsewhere,
///            and an execCommand('copy') fallback for pages not served over HTTPS. The result
///            arrives from a browser promise, which is why this API takes a callback.
///   Editor / desktop   the clipboard, via GUIUtility.systemCopyBuffer.
///
/// GUIUtility.systemCopyBuffer used to be the WebGL path too, and it never reaches the browser's
/// clipboard there: the Dive Over card said "COPIED" and the clipboard was empty.
/// </summary>
public static class ShareUtil
{
    public enum Result { Shared, Copied, Cancelled, Failed }

    /// <summary>
    /// Share <paramref name="text"/>; <paramref name="done"/> is called once with the outcome —
    /// synchronously everywhere except WebGL. Call it from a button press: browsers only allow
    /// sharing and clipboard writes during a user gesture.
    /// </summary>
    public static void ShareText(string text, System.Action<Result> done)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var intentClass = new AndroidJavaClass("android.content.Intent"))
            using (var intent = new AndroidJavaObject("android.content.Intent"))
            {
                intent.Call<AndroidJavaObject>("setAction", intentClass.GetStatic<string>("ACTION_SEND"));
                intent.Call<AndroidJavaObject>("setType", "text/plain");
                intent.Call<AndroidJavaObject>("putExtra", intentClass.GetStatic<string>("EXTRA_TEXT"), text);

                using (var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unity.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var chooser = intentClass.CallStatic<AndroidJavaObject>("createChooser", intent, "Share your result"))
                {
                    activity.Call("startActivity", chooser);
                }
            }
            if (done != null) done(Result.Shared);
            return;
        }
        catch (System.Exception e)
        {
            // A phone with no app able to take text (rare, but kiosk builds exist) must not crash
            // the result screen. Fall through to the clipboard.
            Debug.LogWarning("[Sonarfall] Share sheet failed, copying instead: " + e.Message);
        }
#elif UNITY_WEBGL && !UNITY_EDITOR
        _pending = done;
        SonarfallShareText(text, OnWebResult);
        return;
#endif
        GUIUtility.systemCopyBuffer = text;
        if (done != null) done(Result.Copied);
    }

#if UNITY_WEBGL && !UNITY_EDITOR
    [DllImport("__Internal")]
    private static extern void SonarfallShareText(string text, System.Action<int> callback);

    private static System.Action<Result> _pending;

    /// <summary>Codes from Share.jslib: 1 shared, 2 copied, 3 cancelled, anything else failed.</summary>
    [AOT.MonoPInvokeCallback(typeof(System.Action<int>))]
    private static void OnWebResult(int code)
    {
        var cb = _pending;
        _pending = null;
        if (cb == null) return;
        cb(code == 1 ? Result.Shared : code == 2 ? Result.Copied : code == 3 ? Result.Cancelled : Result.Failed);
    }
#endif
}
