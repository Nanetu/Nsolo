using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace NsoloGame.EditorTools
{
    public static class NsoloBatchCheck
    {
        public static void Play()
        {
            EditorSceneManager.OpenScene("Assets/Prefabs/Scenes/SampleScene.unity", OpenSceneMode.Single);
            EditorApplication.EnterPlaymode();
        }

        public static void Run()
        {
            EditorSceneManager.OpenScene("Assets/Prefabs/Scenes/SampleScene.unity", OpenSceneMode.Single);
            NsoloWiringCheck.Check();
            Debug.Log("BATCHCHECK: done");
            EditorApplication.Exit(0);
        }
    }
}
