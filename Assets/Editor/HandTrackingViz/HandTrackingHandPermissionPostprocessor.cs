using System.IO;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

namespace HandTrackingViz.EditorTools
{
    /// <summary>
    /// Injects the Meta Quest hand-tracking permission and feature into the generated
    /// AndroidManifest.xml at build time, so it does not have to be maintained by hand.
    ///
    /// Adds:
    ///   &lt;uses-permission android:name="com.oculus.permission.HAND_TRACKING" /&gt;
    ///   &lt;uses-feature android:name="oculus.software.handtracking" android:required="false" /&gt;
    ///
    /// (The OpenXR hand-tracking feature also injects the permission; this is a belt-and-suspenders
    /// safeguard and is idempotent — it will not create duplicates.)
    /// </summary>
    public sealed class HandTrackingHandPermissionPostprocessor : IPostGenerateGradleAndroidProject
    {
        const string AndroidNs = "http://schemas.android.com/apk/res/android";
        const string HandTrackingPermission = "com.oculus.permission.HAND_TRACKING";
        const string HandTrackingFeature = "oculus.software.handtracking";
        const string InternetPermission = "android.permission.INTERNET"; // required by PoseFramePublisher's UDP sockets

        public int callbackOrder => 1;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            string manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            if (!File.Exists(manifestPath))
            {
                Debug.LogWarning($"[HandTrackingViz] AndroidManifest not found at {manifestPath}; skipping permission injection.");
                return;
            }

            var doc = new XmlDocument();
            doc.Load(manifestPath);

            var manifest = doc.DocumentElement;
            if (manifest == null || manifest.Name != "manifest")
                return;

            AddUsesPermission(doc, manifest, HandTrackingPermission);
            AddUsesPermission(doc, manifest, InternetPermission);
            AddUsesFeature(doc, manifest, HandTrackingFeature, required: false);

            doc.Save(manifestPath);
            Debug.Log("[HandTrackingViz] Hand-tracking permission/feature ensured in AndroidManifest.xml.");
        }

        static void AddUsesPermission(XmlDocument doc, XmlElement manifest, string permission)
        {
            foreach (XmlElement e in manifest.GetElementsByTagName("uses-permission"))
            {
                if (e.GetAttribute("name", AndroidNs) == permission)
                    return; // already present
            }

            var el = doc.CreateElement("uses-permission");
            el.SetAttribute("name", AndroidNs, permission);
            manifest.AppendChild(el);
        }

        static void AddUsesFeature(XmlDocument doc, XmlElement manifest, string feature, bool required)
        {
            foreach (XmlElement e in manifest.GetElementsByTagName("uses-feature"))
            {
                if (e.GetAttribute("name", AndroidNs) == feature)
                    return; // already present
            }

            var el = doc.CreateElement("uses-feature");
            el.SetAttribute("name", AndroidNs, feature);
            el.SetAttribute("required", AndroidNs, required ? "true" : "false");
            manifest.AppendChild(el);
        }
    }
}
