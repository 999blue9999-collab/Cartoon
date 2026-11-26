#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Nore.Data;

namespace Nore.EditorTools
{
    /// <summary>
    /// Makes a clean checkout buildable by Unity Cloud / Build Automation.
    ///
    /// The prototype bootstraps itself with [RuntimeInitializeOnLoadMethod], so the
    /// committed scene is intentionally EMPTY — a Player build only needs one enabled
    /// scene in Build Settings, not any authored GameObjects or script references.
    /// This script also repairs the scene if a hand-authored .unity file fails to import.
    /// </summary>
    [InitializeOnLoad]
    public static class CloudBuildSetup
    {
        public const string ScenePath = "Assets/Scenes/NoreDemo.unity";
        public const string ProductName = "Nore Prototype";
        private static string FullScenePath => Path.Combine(Application.dataPath, "Scenes", "NoreDemo.unity");

        static CloudBuildSetup()
        {
            // delayCall avoids touching the AssetDatabase while scripts are still loading.
            EditorApplication.delayCall += EnsureSceneAndBuildSettings;
        }

        [MenuItem("Nore/Cloud Build/Ensure Build Scene")]
        public static void EnsureSceneAndBuildSettings()
        {
            try
            {
                if (!File.Exists(FullScenePath) || AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
                    CreateEmptyScene();

                var scenes = new List<EditorBuildSettingsScene>();
                bool found = false, needsWrite = false;
                foreach (EditorBuildSettingsScene existing in EditorBuildSettings.scenes)
                {
                    if (existing.path == ScenePath)
                    {
                        scenes.Add(new EditorBuildSettingsScene(ScenePath, true));
                        found = true;
                        if (!existing.enabled) needsWrite = true;
                    }
                    else scenes.Add(existing);
                }
                if (!found)
                {
                    scenes.Add(new EditorBuildSettingsScene(ScenePath, true));
                    needsWrite = true;
                }
                if (needsWrite) EditorBuildSettings.scenes = scenes.ToArray();
                Debug.Log("[Nore] Cloud build scene ready: " + ScenePath +
                          " (bootstrap is runtime-only, no GameObjects required)");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Nore] Cloud build setup skipped: " + e.Message);
            }
        }

        private static void CreateEmptyScene()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                AssetDatabase.CreateFolder("Assets", "Scenes");
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorSceneManager.CloseScene(scene, true);
            AssetDatabase.Refresh();
            Debug.Log("[Nore] Regenerated empty build scene at " + ScenePath);
        }

        /// <summary>
        /// Local smoke build, also usable as a cloud build entry point:
        ///   Unity -batchmode -quit -executeMethod Nore.EditorTools.CloudBuildSetup.Build
        /// </summary>
        [MenuItem("Nore/Cloud Build/Build Player (local smoke)")]
        public static void Build()
        {
            EnsureSceneAndBuildSettings();
            var paths = new List<string>();
            foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
                if (s.enabled && !string.IsNullOrEmpty(s.path)) paths.Add(s.path);
            if (paths.Count == 0) throw new Exception("No enabled scenes in EditorBuildSettings.");

            BuildTarget target = DefaultTarget();
            string output = Path.Combine("Builds", "Nore", "Nore" + ExtensionFor(target));
            string directory = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            PlayerSettings.productName = ProductName;
            PlayerSettings.companyName = ProductName;
            PlayerSettings.bundleVersion = "0.1.0";

            var options = new BuildPlayerOptions
            {
                scenes = paths.ToArray(),
                locationPathName = output,
                target = target,
                options = BuildOptions.None
            };
            BuildReport report = BuildPipeline.BuildPlayer(options);
            if (report == null || report.summary.result != BuildResult.Succeeded)
                throw new Exception("Build failed: " +
                    (report == null ? "no report" : report.summary.result + " (" + report.summary.totalErrors + " errors)"));
            Debug.Log("[Nore] Build succeeded: " + output + " (" + report.summary.totalSize + " bytes)");
        }

        private static BuildTarget DefaultTarget()
        {
#if UNITY_EDITOR_WIN
            return BuildTarget.StandaloneWindows64;
#elif UNITY_EDITOR_OSX
            return BuildTarget.StandaloneOSX;
#else
            return BuildTarget.StandaloneLinux64;
#endif
        }

        private static string ExtensionFor(BuildTarget target) =>
            target == BuildTarget.StandaloneWindows64 ? ".exe" : "";

        /// <summary>Headless pre-build gate for CI: data + self-tests before a build starts.</summary>
        [MenuItem("Nore/Cloud Build/Verify Project")]
        public static void Verify()
        {
            DataRegistry registry = DataRegistry.LoadDefault();
            var errors = registry.Validate();
            if (errors.Count > 0) throw new Exception("Data errors: " + string.Join(" | ", errors.ToArray()));
            var results = Nore.Core.SelfTests.RunAll(registry);
            Nore.Core.SelfTests.LogToConsole(results);
            foreach (var r in results) if (!r.passed) throw new Exception("Self-test failed: " + r.name + " — " + r.detail);
            Debug.Log("[Nore] Project verified for cloud build — " + registry.Summary());
        }
    }
}
#endif
