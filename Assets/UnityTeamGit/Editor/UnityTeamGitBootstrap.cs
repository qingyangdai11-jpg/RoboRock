using UnityEditor;
using UnityEngine;

namespace Blackbox.UnityTeamGit.Editor
{
    [InitializeOnLoad]
    internal static class UnityTeamGitBootstrap
    {
        private const string PluginVersion = "1.0.0-preview.1";

        static UnityTeamGitBootstrap()
        {
            EditorApplication.delayCall += OpenOnFirstImport;
        }

        private static void OpenOnFirstImport()
        {
            if (Application.isBatchMode)
                return;

            var projectId = Hash128.Compute(Application.dataPath).ToString();
            var key = "Blackbox.UnityTeamGit.FirstOpen." + PluginVersion + "." + projectId;
            if (EditorPrefs.GetBool(key, false))
                return;

            EditorPrefs.SetBool(key, true);
            UnityTeamGitWindow.ShowWindow();
        }
    }
}
