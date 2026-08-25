using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Blackbox.UnityTeamGit.Editor
{
    internal sealed class UnityTeamGitBranchWindow : EditorWindow
    {
        private UnityTeamGitSettings settings;
        private Action<string> log;
        private Action onRepositoryChanged;
        private readonly List<string> branches = new List<string>();
        private int selectedBranch;
        private string currentBranch = string.Empty;
        private string newBranchName = string.Empty;
        private string status = "等待加载";
        private bool working;

        public static void ShowWindow(UnityTeamGitSettings settings, Action<string> log, Action onRepositoryChanged)
        {
            var window = CreateInstance<UnityTeamGitBranchWindow>();
            window.titleContent = new GUIContent("Branch 管理");
            window.settings = settings;
            window.log = log;
            window.onRepositoryChanged = onRepositoryChanged;
            window.minSize = new Vector2(500f, 300f);
            window.ShowUtility();
            window.RefreshBranches();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Branch 管理", EditorStyles.largeLabel);
            EditorGUILayout.LabelField("当前 Branch：" + (string.IsNullOrWhiteSpace(currentBranch) ? "未知" : currentBranch), EditorStyles.boldLabel);
            EditorGUILayout.LabelField(status, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("连接 Gitea 已有 Branch", EditorStyles.boldLabel);
            if (branches.Count > 0)
                selectedBranch = EditorGUILayout.Popup("远端 Branch", selectedBranch, branches.ToArray());
            else
                EditorGUILayout.HelpBox("尚未取得远端 Branch。", MessageType.Info);

            using (new EditorGUI.DisabledScope(working || branches.Count == 0 || EditorUnsafe()))
            {
                if (GUILayout.Button("切换到选中的 Branch", GUILayout.Height(32f)))
                    SwitchSelectedBranch();
            }

            EditorGUILayout.Space(12f);
            EditorGUILayout.LabelField("新建 Branch", EditorStyles.boldLabel);
            newBranchName = EditorGUILayout.TextField("Branch 名称", newBranchName);
            EditorGUILayout.LabelField("新 Branch 将从当前提交创建，并立即发布到 Gitea。", EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUI.DisabledScope(working || string.IsNullOrWhiteSpace(newBranchName) || EditorUnsafe()))
            {
                if (GUILayout.Button("新建并发布 Branch", GUILayout.Height(32f)))
                    CreateBranch();
            }

            EditorGUILayout.Space(8f);
            using (new EditorGUI.DisabledScope(working || EditorUnsafe()))
            {
                if (GUILayout.Button("刷新 Gitea Branch 列表"))
                    RefreshBranches();
            }
        }

        private async void RefreshBranches()
        {
            if (working)
                return;
            if (!UnityTeamGitOperationGate.TryEnter())
            {
                status = "另一个 Git 操作正在执行，请稍后重试。";
                Repaint();
                return;
            }

            working = true;
            status = "正在从 Gitea 获取 Branch……";
            Repaint();
            try
            {
                var initializer = new UnityTeamGitInitializer(settings);
                var snapshot = await initializer.GetRemoteBranchesAsync(log);
                branches.Clear();
                branches.AddRange(snapshot.RemoteBranches);
                currentBranch = snapshot.CurrentBranch;
                selectedBranch = Math.Max(0, branches.IndexOf(currentBranch));
                status = "已取得 " + branches.Count + " 个远端 Branch。";
            }
            catch (Exception exception)
            {
                status = "加载失败：" + exception.Message;
                WriteLog(exception.ToString());
            }
            finally
            {
                working = false;
                UnityTeamGitOperationGate.Exit();
                Repaint();
            }
        }

        private async void SwitchSelectedBranch()
        {
            if (working || branches.Count == 0)
                return;
            if (selectedBranch < 0 || selectedBranch >= branches.Count)
            {
                status = "Branch 选择无效，请刷新列表。";
                return;
            }

            var branchName = branches[selectedBranch];
            if (!EditorUtility.DisplayDialog(
                    "确认切换 Branch",
                    "当前：" + currentBranch + "\n目标：" + branchName + "\n\n仅在工作区干净时执行，并使用 fast-forward Pull。",
                    "确认切换",
                    "取消"))
                return;
            if (!PrepareForBranchChange() || !UnityTeamGitOperationGate.TryEnter())
                return;

            working = true;
            status = "正在切换到 " + branchName + "……";
            Repaint();
            try
            {
                var initializer = new UnityTeamGitInitializer(settings);
                status = await initializer.SwitchToRemoteBranchAsync(log, branchName);
                currentBranch = branchName;
                AssetDatabase.Refresh();
                NotifyRepositoryChanged();
            }
            catch (Exception exception)
            {
                status = "切换失败：" + exception.Message;
                WriteLog(exception.ToString());
                EditorUtility.DisplayDialog("Branch 切换失败", exception.Message, "确定");
            }
            finally
            {
                working = false;
                UnityTeamGitOperationGate.Exit();
                Repaint();
            }
        }

        private async void CreateBranch()
        {
            if (working)
                return;

            var branchName = newBranchName == null ? string.Empty : newBranchName.Trim();
            if (branchName.Length == 0)
            {
                status = "Branch 名称不能为空；没有执行任何 Git 命令。";
                EditorUtility.DisplayDialog("无法新建 Branch", status, "确定");
                Repaint();
                return;
            }
            if (!EditorUtility.DisplayDialog(
                    "确认新建 Branch",
                    "将从当前 Branch “" + currentBranch + "” 创建并发布：\n\n" + branchName,
                    "新建并发布",
                    "取消"))
                return;
            if (!PrepareForBranchChange() || !UnityTeamGitOperationGate.TryEnter())
                return;

            working = true;
            status = "正在新建并发布 " + branchName + "……";
            Repaint();
            try
            {
                var initializer = new UnityTeamGitInitializer(settings);
                status = await initializer.CreateAndPublishBranchAsync(log, branchName);
                currentBranch = branchName;
                newBranchName = string.Empty;
                if (!branches.Contains(branchName))
                {
                    branches.Add(branchName);
                    branches.Sort(StringComparer.Ordinal);
                }
                selectedBranch = Math.Max(0, branches.IndexOf(branchName));
                AssetDatabase.Refresh();
                NotifyRepositoryChanged();
            }
            catch (Exception exception)
            {
                status = "新建失败：" + exception.Message;
                WriteLog(exception.ToString());
                EditorUtility.DisplayDialog("新建 Branch 失败", exception.Message, "确定");
            }
            finally
            {
                working = false;
                UnityTeamGitOperationGate.Exit();
                Repaint();
            }
        }

        private bool PrepareForBranchChange()
        {
            if (EditorUnsafe())
            {
                status = "Unity 正在编译、导入或进入 Play Mode，Branch 操作已停止。";
                return false;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                status = "用户取消保存场景。";
                return false;
            }
            AssetDatabase.SaveAssets();
            return true;
        }

        private static bool EditorUnsafe()
        {
            return EditorApplication.isCompiling ||
                   EditorApplication.isUpdating ||
                   EditorApplication.isPlayingOrWillChangePlaymode;
        }

        private void WriteLog(string message)
        {
            if (log != null)
                log(message);
        }

        private void NotifyRepositoryChanged()
        {
            if (onRepositoryChanged != null)
                onRepositoryChanged();
        }
    }
}
