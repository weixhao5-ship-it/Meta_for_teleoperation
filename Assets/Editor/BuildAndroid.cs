using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Batch-mode Android build entry point, so the APK can be produced without
// opening the editor UI:
//
//   Unity -quit -batchmode -nographics -projectPath <proj> \
//         -buildTarget Android -executeMethod BuildAndroid.Build
//
// Scenes come from the Build Settings list (EditorBuildSettings), so this never
// disagrees with what a manual File > Build does. Output goes to
// Build/<productName>.apk unless -output <path> is passed.
//
// Optional per-build identity overrides let ONE project (one branch, one
// ProjectSettings.asset) emit several differently-identified APKs without
// editing the working tree — e.g. the hand and controller builds:
//
//   ... -executeMethod BuildAndroid.Build -output Build/Meta_hand.apk
//   ... -executeMethod BuildAndroid.Build -output Build/Meta_controller.apk \
//         -productName Meta_controller \
//         -applicationIdentifier com.dongridong.metacontroller
//
// The committed identity is restored after the build, so an overridden build
// never leaves ProjectSettings.asset dirty.
public static class BuildAndroid
{
    [MenuItem("Build/Android APK")]
    public static void Build()
    {
        string[] scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled)
            .Select(s => s.path)
            .ToArray();

        if (scenes.Length == 0)
            throw new Exception("[BuildAndroid] no enabled scenes in Build Settings.");

        // Per-build identity overrides (optional). Capture the committed values
        // first so they can be restored after the build — the overrides are for
        // this one APK only and must never be serialized back to disk.
        string origProductName = PlayerSettings.productName;
        string origAppId = PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android);

        string productName = ArgValue("-productName");
        if (!string.IsNullOrEmpty(productName))
            PlayerSettings.productName = productName;

        string appId = ArgValue("-applicationIdentifier");
        if (!string.IsNullOrEmpty(appId))
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, appId);

        // Default output name follows productName, so it reflects an override.
        string output = ArgValue("-output")
                        ?? Path.Combine("Build", PlayerSettings.productName + ".apk");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));

        Debug.Log($"[BuildAndroid] scenes: {string.Join(", ", scenes)}");
        Debug.Log($"[BuildAndroid] output: {output}");
        Debug.Log($"[BuildAndroid] productName: {PlayerSettings.productName} " +
                  $"applicationIdentifier: {PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android)}");

        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = output,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = BuildOptions.None,
        };

        BuildSummary summary;
        try
        {
            BuildReport report = BuildPipeline.BuildPlayer(options);
            summary = report.summary;
        }
        finally
        {
            // Restore the committed identity even if the build throws, so the
            // working tree stays clean between builds.
            PlayerSettings.productName = origProductName;
            PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, origAppId);
        }

        Debug.Log($"[BuildAndroid] result={summary.result} " +
                  $"size={summary.totalSize / (1024 * 1024)}MB " +
                  $"errors={summary.totalErrors} warnings={summary.totalWarnings}");

        if (summary.result != BuildResult.Succeeded)
        {
            // Batch mode ignores a non-zero exit unless we force it, and a silent
            // "failed" build that still exits 0 is worse than a loud crash.
            EditorApplication.Exit(1);
        }
    }

    private static string ArgValue(string flag)
    {
        string[] args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, flag);
        return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
    }
}
