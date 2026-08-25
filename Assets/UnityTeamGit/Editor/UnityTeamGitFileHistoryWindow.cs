using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Blackbox.UnityTeamGit.Editor
{
    internal sealed class UnityTeamGitFileHistoryWindow : EditorWindow
    {
        private string assetPath;
        private readonly List<GitFileRevision> revisions = new List<GitFileRevision>();
        private Vector2 scroll;
        private int selectedRevision = -1;
        private bool loading;
        private bool restoring;
        private string status = "等待加载";

        public static void ShowWindow(string assetPath)
        {
            var window = CreateInstance<UnityTeamGitFileHistoryWindow>();
            window.titleContent = new GUIContent("文件 Commit 历史");
            window.assetPath = assetPath;
            window.minSize = new Vector2(720f, 460f);
            window.ShowUtility();
            window.LoadHistory();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("从指定 Commit 恢复文件", EditorStyles.largeLabel);
            EditorGUILayout.LabelField(assetPath ?? string.Empty, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField(status, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(6f);

            scroll = EditorGUILayout.BeginScrollView(scroll, EditorStyles.helpBox);
            for (var index = 0; index < revisions.Count; index++)
            {
                var revision = revisions[index];
                EditorGUILayout.BeginVertical(index == selectedRevision ? "SelectionRect" : EditorStyles.helpBox);
                var selected = GUILayout.Toggle(
                    index == selectedRevision,
                    revision.ShortHash + "  " + revision.Date + "  " + revision.Subject,
                    "Radio");
                if (selected)
                    selectedRevision = index;
                EditorGUILayout.LabelField(revision.Author + " · " + revision.Hash, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();

            using (new EditorGUI.DisabledScope(loading || restoring || selectedRevision < 0 || selectedRevision >= revisions.Count))
            {
                if (GUILayout.Button(restoring ? "正在恢复……" : "恢复到选中的 Commit 版本", GUILayout.Height(36f)))
                    RestoreSelected();
            }
        }

        private async void LoadHistory()
        {
            if (loading)
                return;
            loading = true;
            status = "正在读取文件历史……";
            Repaint();
            try
            {
                var result = await UnityTeamGitFileRestore.LoadHistoryAsync(assetPath);
                revisions.Clear();
                revisions.AddRange(result);
                selectedRevision = revisions.Count > 0 ? 0 : -1;
                status = revisions.Count > 0 ? "找到 " + revisions.Count + " 个版本。" : "该文件没有可用的 Commit 历史。";
            }
            catch (Exception exception)
            {
                status = "读取失败：" + exception.Message;
                EditorUtility.DisplayDialog("无法读取文件历史", exception.Message, "确定");
            }
            finally
            {
                loading = false;
                Repaint();
            }
        }

        private async void RestoreSelected()
        {
            if (restoring || selectedRevision < 0 || selectedRevision >= revisions.Count)
                return;
            restoring = true;
            var revision = revisions[selectedRevision];
            status = "正在恢复 " + revision.ShortHash + "……";
            Repaint();
            try
            {
                await UnityTeamGitFileRestore.RestoreToCommitAsync(
                    assetPath,
                    revision.Hash,
                    revision.ShortHash + " · " + revision.Date + " · " + revision.Subject);
                status = "恢复操作已结束，请检查文件差异。";
            }
            catch (Exception exception)
            {
                status = "恢复失败：" + exception.Message;
                EditorUtility.DisplayDialog("文件恢复失败", exception.Message, "确定");
            }
            finally
            {
                restoring = false;
                Repaint();
            }
        }
    }
}
