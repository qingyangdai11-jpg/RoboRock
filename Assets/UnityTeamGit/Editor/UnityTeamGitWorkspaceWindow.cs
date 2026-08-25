using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Blackbox.UnityTeamGit.Editor
{
    internal sealed class UnityTeamGitWorkspaceWindow : EditorWindow
    {
        private const double StatusCheckWatchdogSeconds = 30d;

        private UnityTeamGitSettings settings;
        private List<PreflightCheck> checks = new List<PreflightCheck>();
        private RepositoryProbe repositoryProbe = new RepositoryProbe { CloudState = CloudRepositoryState.Unknown };
        private readonly List<string> logs = new List<string>();
        private Vector2 mainScroll;
        private Vector2 logScroll;
        private bool checking;
        private bool syncing;
        private double checkStartedAt;
        private int checkGeneration;
        private string statusMessage = "等待检查";

        [MenuItem("Tools/Unity Team Git/Workspace", priority = 20)]
        public static void ShowWindow()
        {
            var window = GetWindow<UnityTeamGitWorkspaceWindow>();
            window.titleContent = new GUIContent("Unity Git Workspace");
            window.minSize = new Vector2(680f, 430f);
            window.Show();
        }

        private void OnEnable()
        {
            // A script/domain reload can abandon an awaited preflight task while Unity keeps
            // the EditorWindow instance. Never carry that stale busy state into the new domain.
            checking = false;
            checkStartedAt = 0d;
            checkGeneration++;
            settings = UnityTeamGitSettings.Load();
            RefreshStatus();
        }

        private void Update()
        {
            if (!checking || checkStartedAt <= 0d ||
                EditorApplication.timeSinceStartup - checkStartedAt < StatusCheckWatchdogSeconds)
                return;

            // Preflight is read-only. If its continuation is lost, invalidate it and unblock the
            // window; any late continuation is ignored by the generation check below.
            checking = false;
            checkStartedAt = 0d;
            checkGeneration++;
            statusMessage = "状态检查超时，已自动解除锁定。请关闭并重新打开 Workspace 后重试。";
            AddLog(statusMessage);
        }

        private void OnGUI()
        {
            if (settings == null)
                settings = UnityTeamGitSettings.Load();

            mainScroll = EditorGUILayout.BeginScrollView(mainScroll);
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Unity Team Git · 日常同步", EditorStyles.largeLabel);
            EditorGUILayout.LabelField("工程：" + settings.RepositoryName, EditorStyles.miniLabel);
            EditorGUILayout.LabelField(
                "当前 Branch：" + (string.IsNullOrWhiteSpace(repositoryProbe.CurrentBranch) ? "未知" : repositoryProbe.CurrentBranch),
                EditorStyles.boldLabel);
            EditorGUILayout.LabelField(statusMessage, EditorStyles.wordWrappedMiniLabel);

            DrawBlockingIssues();
            DrawActions();
            DrawLinks();
            DrawLogs();
            EditorGUILayout.EndScrollView();
        }

        private void DrawBlockingIssues()
        {
            var errors = checks.Where(check => check.IsBlocking).Select(check => check.Name + "：" + check.Detail).ToList();
            if (errors.Count > 0)
                EditorGUILayout.HelpBox(string.Join("\n", errors), MessageType.Error);
            if (EditorUnsafe())
                EditorGUILayout.HelpBox("Unity 正在编译、导入或进入 Play Mode，Git 写操作已暂停。", MessageType.Warning);
        }

        private void DrawActions()
        {
            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("同步操作", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("不执行 force、自动 stash、reset 或历史重写。", EditorStyles.wordWrappedMiniLabel);

            var repositoryReady = repositoryProbe.LocalRepositoryExists &&
                                  repositoryProbe.OriginConfigured &&
                                  repositoryProbe.OriginMatches &&
                                  repositoryProbe.CloudState == CloudRepositoryState.Exists &&
                                  !string.IsNullOrWhiteSpace(repositoryProbe.CurrentBranch);
            var blocked = checking || syncing || UnityTeamGitOperationGate.IsBusy || EditorUnsafe() ||
                          checks.Count == 0 || checks.Any(check => check.IsBlocking) || !repositoryReady;

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(blocked))
            {
                if (GUILayout.Button(new GUIContent("Pull", "要求工作区干净；执行 pull --ff-only。"), GUILayout.Height(38f)))
                    BeginPull();

                if (GUILayout.Button(new GUIContent("Push_NoCommit", "以当前时间作为 Commit 说明，然后 Push。"), GUILayout.Height(38f)))
                    BeginPush(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), repositoryProbe.CurrentBranch);

                if (GUILayout.Button(new GUIContent("Push_Commit", "填写 Commit 说明，然后 Push。"), GUILayout.Height(38f)))
                {
                    var targetBranch = repositoryProbe.CurrentBranch;
                    UnityTeamGitCommitWindow.ShowWindow(targetBranch, message => BeginPush(message, targetBranch));
                }
            }
            EditorGUILayout.EndHorizontal();

            using (new EditorGUI.DisabledScope(blocked))
            {
                if (GUILayout.Button("切换 / 新建 Branch……", GUILayout.Height(34f)))
                {
                    settings.Save();
                    UnityTeamGitBranchWindow.ShowWindow(settings, AddLog, RefreshStatus);
                }
            }

            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("版本恢复", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "选择当前 Branch 的历史 Commit，将整个项目恢复为该版本并创建新的本地回退 Commit；不会 reset、改写历史或自动 Push。",
                EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUI.DisabledScope(blocked))
            {
                if (GUILayout.Button("按 Commit 回退整个项目……", GUILayout.Height(36f)))
                {
                    settings.Save();
                    UnityTeamGitProjectRollbackWindow.ShowWindow(settings, AddLog, RefreshStatus);
                }
            }
        }

        private void DrawLinks()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("打开工程目录"))
                EditorUtility.RevealInFinder(settings.ProjectRoot);
            if (GUILayout.Button("打开 Gitea 仓库"))
                Application.OpenURL(settings.LanWebRepositoryUrl);
            if (GUILayout.Button("通过 Tailscale 打开"))
                Application.OpenURL(settings.TailnetWebRepositoryUrl);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawLogs()
        {
            EditorGUILayout.Space(10f);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("执行日志", EditorStyles.boldLabel);
            if (GUILayout.Button("清空", GUILayout.Width(55f)))
                logs.Clear();
            EditorGUILayout.EndHorizontal();

            logScroll = EditorGUILayout.BeginScrollView(logScroll, EditorStyles.helpBox, GUILayout.MinHeight(140f));
            if (logs.Count == 0)
                EditorGUILayout.LabelField("尚无日志。", EditorStyles.miniLabel);
            else
                EditorGUILayout.SelectableLabel(string.Join(Environment.NewLine, logs), EditorStyles.wordWrappedMiniLabel, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        private async void RefreshStatus()
        {
            if (checking || syncing || UnityTeamGitOperationGate.IsBusy || settings == null)
                return;

            var generation = ++checkGeneration;
            checking = true;
            checkStartedAt = EditorApplication.timeSinceStartup;
            statusMessage = "正在检查本地与 Gitea……";
            Repaint();
            try
            {
                settings = UnityTeamGitSettings.Load();
                var initializer = new UnityTeamGitInitializer(settings);
                var refreshedChecks = await initializer.RunPreflightAsync();
                if (generation != checkGeneration)
                    return;

                checks = refreshedChecks;
                repositoryProbe = initializer.LastRepositoryProbe;
                var errors = checks.Count(check => check.IsBlocking);
                statusMessage = errors == 0 ? "仓库状态正常" : errors + " 个问题需要处理";
            }
            catch (Exception exception)
            {
                if (generation != checkGeneration)
                    return;

                statusMessage = "状态检查失败：" + exception.Message;
                AddLog(exception.ToString());
            }
            finally
            {
                if (generation == checkGeneration)
                {
                    checking = false;
                    checkStartedAt = 0d;
                    Repaint();
                }
            }
        }

        private async void BeginPull()
        {
            if (!PrepareForOperation("Pull") || !UnityTeamGitOperationGate.TryEnter())
                return;

            syncing = true;
            statusMessage = "正在 Pull……";
            AddLog("开始安全 Pull");
            Repaint();
            try
            {
                var initializer = new UnityTeamGitInitializer(settings);
                var message = await initializer.PullAsync(AddLog);
                AssetDatabase.Refresh();
                statusMessage = message;
                AddLog(message);
            }
            catch (Exception exception)
            {
                statusMessage = "Pull 失败；未重写历史";
                AddLog(exception.ToString());
                EditorUtility.DisplayDialog("Unity Team Git Pull 失败", exception.Message, "确定");
            }
            finally
            {
                syncing = false;
                UnityTeamGitOperationGate.Exit();
                Repaint();
                RefreshStatus();
            }
        }

        private async void BeginPush(string commitMessage, string expectedBranch)
        {
            if (!PrepareForOperation("Push") || !UnityTeamGitOperationGate.TryEnter())
                return;

            syncing = true;
            statusMessage = "正在 Commit 并 Push 到 origin/" + expectedBranch + "……";
            AddLog("开始安全 Push：origin/" + expectedBranch);
            Repaint();
            try
            {
                var initializer = new UnityTeamGitInitializer(settings);
                var message = await initializer.CommitAndPushAsync(AddLog, commitMessage, expectedBranch);
                statusMessage = message;
                AddLog(message);
            }
            catch (Exception exception)
            {
                statusMessage = "Push 失败；没有执行强制 Push";
                AddLog(exception.ToString());
                EditorUtility.DisplayDialog("Unity Team Git Push 失败", exception.Message, "确定");
            }
            finally
            {
                syncing = false;
                UnityTeamGitOperationGate.Exit();
                Repaint();
                RefreshStatus();
            }
        }

        private bool PrepareForOperation(string operationName)
        {
            if (checking || syncing || UnityTeamGitOperationGate.IsBusy)
            {
                AddLog("另一个 Git 操作正在执行，" + operationName + " 已取消。");
                return false;
            }
            if (EditorUnsafe())
            {
                AddLog("Unity 正在编译、导入或进入 Play Mode，" + operationName + " 已取消。");
                return false;
            }
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                AddLog("用户取消保存场景，" + operationName + " 未开始。");
                return false;
            }

            settings.Save();
            AssetDatabase.SaveAssets();
            return true;
        }

        private void AddLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;
            logs.Add("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message.Trim());
            logScroll.y = float.MaxValue;
            Repaint();
        }

        private static bool EditorUnsafe()
        {
            return EditorApplication.isCompiling ||
                   EditorApplication.isUpdating ||
                   EditorApplication.isPlayingOrWillChangePlaymode;
        }
    }
}
