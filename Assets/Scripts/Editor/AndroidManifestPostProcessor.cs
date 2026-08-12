#if UNITY_ANDROID
using System.IO;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

/// <summary>
/// Injects android.permission.VIBRATE into the generated manifest.
///
/// This is not optional. Unity decides which permissions to add by scanning the built assemblies
/// for known API calls (Handheld.Vibrate, Microphone, etc.). Haptics reaches the vibrator through
/// AndroidJavaObject/JNI, which that scanner cannot see, so without this the APK ships with no
/// VIBRATE permission and every vibrate() call is dropped by the framework — silently, with no
/// exception and nothing in logcat.
///
/// Verify on a built APK with:  aapt dump permissions &lt;apk&gt;
/// </summary>
public class AndroidManifestPostProcessor : IPostGenerateGradleAndroidProject
{
    // VIBRATE is a NORMAL permission: granted at install, never prompted, and it does not appear
    // under Settings -> App permissions (that screen only lists runtime groups). POST_NOTIFICATIONS
    // is a RUNTIME permission on API 33+, so that one does prompt and does show up there.
    private static readonly string[] Permissions =
    {
        "android.permission.VIBRATE",
        "android.permission.POST_NOTIFICATIONS",
    };

    // After Unity's own manifest generation, so the file exists and nothing overwrites us.
    public int callbackOrder => 1;

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        string manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
        if (!File.Exists(manifestPath))
        {
            Debug.LogError("[Sonarfall] AndroidManifest.xml not found at " + manifestPath +
                           " — VIBRATE permission NOT added, haptics will be dead in this build.");
            return;
        }

        var doc = new XmlDocument();
        doc.Load(manifestPath);

        var manifest = doc.SelectSingleNode("/manifest") as XmlElement;
        if (manifest == null) { Debug.LogError("[Sonarfall] Malformed AndroidManifest.xml."); return; }

        const string ns = "http://schemas.android.com/apk/res/android";

        bool changed = false;
        foreach (string permission in Permissions)
        {
            bool present = false;
            foreach (XmlNode node in manifest.SelectNodes("uses-permission"))
            {
                var el = node as XmlElement;
                if (el != null && el.GetAttribute("name", ns) == permission) { present = true; break; }
            }
            if (present)
            {
                Debug.Log("[Sonarfall] " + permission + " already present.");
                continue;
            }

            var added = doc.CreateElement("uses-permission");
            added.SetAttribute("name", ns, permission);
            manifest.AppendChild(added);
            changed = true;
            Debug.Log("[Sonarfall] Added " + permission);
        }

        if (changed) doc.Save(manifestPath);
    }
}
#endif
