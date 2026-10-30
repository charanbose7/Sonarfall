using UnityEngine;

/// <summary>
/// Hand a line of text to the phone's share sheet.
///
/// On Android this is a plain ACTION_SEND intent wrapped in a chooser — the same thing every app's
/// "Share" button does. The game itself sends nothing anywhere and needs no INTERNET permission:
/// whichever app the player picks (WhatsApp, Discord, X...) does the sending. That keeps the
/// manifest and the Data safety form exactly as they are.
///
/// Everywhere else (the editor, desktop builds) the text goes to the clipboard and the caller is
/// told so, which is what makes the button testable without a phone.
/// </summary>
public static class ShareUtil
{
    /// <summary>
    /// Share <paramref name="text"/>. Returns true if a system share sheet was opened, false if
    /// the text was copied to the clipboard instead.
    /// </summary>
    public static bool ShareText(string text)
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
            return true;
        }
        catch (System.Exception e)
        {
            // A phone with no app able to take text (rare, but kiosk builds exist) must not crash
            // the result screen. Fall through to the clipboard.
            Debug.LogWarning("[Sonarfall] Share sheet failed, copying instead: " + e.Message);
        }
#endif
        GUIUtility.systemCopyBuffer = text;
        return false;
    }
}
