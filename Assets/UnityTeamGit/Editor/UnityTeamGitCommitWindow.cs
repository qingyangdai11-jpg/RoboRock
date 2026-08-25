using System;
using UnityEditor;
using UnityEngine;

namespace Blackbox.UnityTeamGit.Editor
{
    internal sealed class UnityTeamGitCommitWindow : EditorWindow
    {
        private Action<string> onSubmit;
        private string commitMessage = string.Empty;
        private string targetBranch = string.Empty;

        public static void ShowWindow(string targetBranch, Action<string> onSubmit)
        {
            var window = CreateInstance<UnityTeamGitCommitWindow>();
            window.titleContent = new GUIContent("Commit & Push");
            window.onSubmit = onSubmit;
            window.targetBranch = targetBranch;
            window.minSize = new Vector2(480f, 180f);
            window.maxSize = new Vector2(680f, 260f);
            window.ShowUtility();
            window.Focus();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("Commit 说明", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("目标 Branch：origin/" + targetBranch, EditorStyles.boldLabel);
            EditorGUILayout.LabelField("填写本次修改内容。提交成功后将立即 Push 到上方 Branch。", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(6f);
            GUI.SetNextControlName("CommitMessage");
            commitMessage = EditorGUILayout.TextArea(commitMessage, GUILayout.MinHeight(70f));

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("取消", GUILayout.Height(30f)))
                Close();

            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(commitMessage)))
            {
                if (GUILayout.Button("Commit 并 Push", GUILayout.Height(30f)))
                {
                    var callback = onSubmit;
                    onSubmit = null;
                    var message = commitMessage.Trim();
                    if (callback != null)
                        callback(message);
                    // Begin the operation before closing this utility window. Closing first
                    // returns focus to Workspace and its OnFocus refresh can set `checking`
                    // before BeginPush runs, making Push_Commit reject its own status check as
                    // "another Git operation". BeginPush acquires the shared gate synchronously,
                    // so the subsequent focus event safely skips RefreshStatus.
                    Close();
                    GUIUtility.ExitGUI();
                }
            }
            EditorGUILayout.EndHorizontal();

            if (Event.current.type == EventType.Repaint)
                EditorGUI.FocusTextInControl("CommitMessage");
        }
    }
}
