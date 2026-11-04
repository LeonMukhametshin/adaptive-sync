using System;
using System.Linq;
using AdaptiveSync.Demo.App;
using AdaptiveSync.Demo.Views;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace AdaptiveSync.Demo.Editor
{
    public static class AppScenesBuilder
    {
        public const string BootScenePath = "Assets/Scenes/" + SceneNames.Boot + ".unity";
        public const string MainScenePath = "Assets/Scenes/" + SceneNames.Main + ".unity";
        public const string LegacyScenePath = "Assets/Scenes/SyncDemo.unity";
        public const string PreloaderLayoutPath = "Assets/AdaptiveSync/UI/Preloader.uxml";
        public const string MainLayoutPath = "Assets/AdaptiveSync/UI/MainScreen.uxml";
        public const string ThemePath = "Assets/AdaptiveSync/UI/AdaptiveSyncTheme.tss";
        public const string PanelSettingsPath = "Assets/AdaptiveSync/UI/AppPanelSettings.asset";

        public static readonly string[] AppScenes = { BootScenePath, MainScenePath };

        [MenuItem("Adaptive Sync/Build App Scenes")]
        public static void Build()
        {
            EnsurePanelSettings();

            CreateScene(BootScenePath, PreloaderLayoutPath, "Preloader", ui => ui.AddComponent<PreloaderView>(),
                () => new GameObject("App").AddComponent<AppEntryPoint>());
            CreateScene(MainScenePath, MainLayoutPath, "MainScreen", ui => ui.AddComponent<MainView>(), null);

            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(LegacyScenePath) != null)
                AssetDatabase.DeleteAsset(LegacyScenePath);

            var others = EditorBuildSettings.scenes
                .Where(s => !AppScenes.Contains(s.path) && s.path != LegacyScenePath)
                .Select(s => new EditorBuildSettingsScene(s.path, false));
            EditorBuildSettings.scenes = AppScenes.Select(p => new EditorBuildSettingsScene(p, true)).Concat(others).ToArray();

            Verify<PreloaderView>(BootScenePath);
            Verify<MainView>(MainScenePath);
            EditorSceneManager.OpenScene(BootScenePath, OpenSceneMode.Single);
            Debug.Log("App scenes saved: " + string.Join(", ", AppScenes));
        }

        private static void CreateScene(
            string path, string layoutPath, string uiName, Action<GameObject> addScreen, Action extra)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            var layout = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(layoutPath);
            if (panelSettings == null || panelSettings.themeStyleSheet == null || layout == null)
                throw new BuildFailedException("UI assets are missing for " + path + ": PanelSettings, theme or " + layoutPath + ".");

            var camera = new GameObject("Main Camera").AddComponent<Camera>();
            camera.tag = "MainCamera";
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color32(11, 17, 32, 255);

            extra?.Invoke();

            var ui = new GameObject(uiName);
            var document = ui.AddComponent<UIDocument>();
            var serialized = new SerializedObject(document);
            serialized.FindProperty("m_PanelSettings").objectReferenceValue = panelSettings;
            serialized.FindProperty("sourceAsset").objectReferenceValue = layout;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            addScreen(ui);

            EditorSceneManager.SaveScene(scene, path);
        }

        private static void Verify<TScreen>(string path) where TScreen : MonoBehaviour
        {
            EditorSceneManager.OpenScene(path, OpenSceneMode.Single);
            var document = UnityEngine.Object.FindAnyObjectByType<UIDocument>();
            if (document == null || document.panelSettings == null || document.visualTreeAsset == null ||
                document.GetComponent<TScreen>() == null)
            {
                throw new BuildFailedException(path + " was saved without a complete " + typeof(TScreen).Name + " UIDocument.");
            }
        }

        private static void EnsurePanelSettings()
        {
            var settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(settings, PanelSettingsPath);
            }

            settings.themeStyleSheet = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.referenceResolution = new Vector2Int(412, 892);
            settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            settings.match = 0f;
            settings.clearColor = true;
            settings.colorClearValue = new Color32(11, 17, 32, 255);
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
        }
    }
}
