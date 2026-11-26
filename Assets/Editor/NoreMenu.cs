#if UNITY_EDITOR
using System.Collections.Generic;
using Nore.Core;
using Nore.Data;
using Nore.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Nore.EditorTools
{
    /// <summary>Editor tooling: data validation, headless self-tests and a demo scene.</summary>
    public static class NoreMenu
    {
        [MenuItem("Nore/Validate Data")]
        public static void ValidateData()
        {
            DataRegistry registry = DataRegistry.LoadDefault();
            List<string> errors = registry.Validate();
            if (errors.Count == 0) Debug.Log("[Nore] Data valid — " + registry.Summary());
            else Debug.LogError("[Nore] " + errors.Count + " data errors:\n" + string.Join("\n", errors.ToArray()));
        }

        [MenuItem("Nore/Run Self Tests")]
        public static void RunSelfTests()
        {
            DataRegistry registry = DataRegistry.LoadDefault();
            List<SelfTests.Result> results = SelfTests.RunAll(registry);
            SelfTests.LogToConsole(results);
        }

        [MenuItem("Nore/Create Demo Scene")]
        public static void CreateDemoScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var camera = new GameObject("Main Camera");
            camera.tag = "MainCamera";
            var cam = camera.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.05f, 0.07f, 0.10f, 1f);
            cam.orthographic = true;
            camera.transform.position = new Vector3(0, 0, -10);

            var light = new GameObject("Directional Light");
            var l = light.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = 1f;
            light.transform.rotation = Quaternion.Euler(50, -30, 0);

            var game = new GameObject("Nore Game");
            game.AddComponent<GameController>();
            game.AddComponent<RuntimeUI>();

            if (!AssetDatabase.IsValidFolder("Assets/Scenes")) AssetDatabase.CreateFolder("Assets", "Scenes");
            // Keep the committed, empty NoreDemo.unity as the cloud build scene; this menu
            // creates a richer optional scene with authored camera/light/game objects.
            EditorSceneManager.SaveScene(scene, "Assets/Scenes/NoreDemoFull.unity");
            Debug.Log("[Nore] Optional full demo scene created at Assets/Scenes/NoreDemoFull.unity — press Play. " +
                      "Assets/Scenes/NoreDemo.unity remains the cloud build scene.");
        }
    }
}
#endif
