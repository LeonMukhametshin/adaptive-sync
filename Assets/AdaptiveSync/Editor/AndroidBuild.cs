using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace AdaptiveSync.Demo.Editor
{
    public static class AndroidBuild
    {
        public const string OutputPath = "Builds/Android/AdaptiveSyncDemo.apk";
        public const string ApplicationId = "ru.dms.adaptivesync";

        [MenuItem("Adaptive Sync/Build Android APK")]
        public static void BuildFromMenu()
        {
            var report = Build();
            if (report.summary.result == BuildResult.Succeeded)
                EditorUtility.RevealInFinder(OutputPath);
        }

        public static void BuildFromCommandLine()
        {
            var report = Build();
            EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
        }

        [MenuItem("Adaptive Sync/Apply Android Player Settings")]
        public static void Configure()
        {
            var target = NamedBuildTarget.Android;
            PlayerSettings.productName = "Adaptive Sync Demo";
            PlayerSettings.SetApplicationIdentifier(target, ApplicationId);
            PlayerSettings.SetScriptingBackend(target, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetManagedStrippingLevel(target, ManagedStrippingLevel.Minimal);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
            PlayerSettings.Android.forceInternetPermission = true;
            PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
            PlayerSettings.allowedAutorotateToLandscapeLeft = false;
            PlayerSettings.allowedAutorotateToLandscapeRight = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.Android.renderOutsideSafeArea = true;
            AssetDatabase.SaveAssets();
        }

        public static BuildReport Build()
        {
            Configure();
            AppScenesBuilder.Build();
            EditorUserBuildSettings.buildAppBundle = false;
            Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = AppScenesBuilder.AppScenes,
                locationPathName = OutputPath,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                options = BuildOptions.None,
            });

            Debug.Log("Android build " + report.summary.result + ": " + Path.GetFullPath(OutputPath) +
                      " (" + report.summary.totalSize / (1024 * 1024) + " MB, errors " + report.summary.totalErrors + ")");
            return report;
        }
    }
}
