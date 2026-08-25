using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Blackbox.UnityTeamGit.Editor
{
    internal sealed class UnityTeamGitWindow : EditorWindow
    {
        private UnityTeamGitSettings settings;
        private List<PreflightCheck> checks = new List<PreflightCheck>();
        private readonly List<string> logs = new List<string>();
        private Vector2 mainScroll;
        private Vector2 logScroll;
        private bool checking;
        private bool initializing;
        private bool rebinding;
        private bool firstPushing;
        private RepositoryProbe repositoryProbe = new RepositoryProbe { CloudState = CloudRepositoryState.Unknown };
        private string statusMessage = "等待检查";

        [MenuItem("Tools/Unity Team Git/Setup", priority = 10)]
        public static void ShowWindow()
        {
            var window = GetWindow<UnityTeamGitWindow>();
            window.titleContent = new GUIContent("Unity Team Git");
            window.minSize = new Vector2(680f, 700f);
            window.Show();
        }

        private void OnEnable()
        {
            settings = UnityTeamGitSettings.Load();
            RefreshPreflight();
        }

        private void OnGUI()
        {
            if (settings == null)
                settings = UnityTeamGitSettings.Load();

            mainScroll = EditorGUILayout.BeginScrollView(mainScroll);
            DrawHeader();
            DrawConfiguration();
            DrawPreflight();
            DrawActions();
            DrawLogs();
            EditorGUILayout.EndScrollView();
        }

        private void DrawHeader()
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("Unity Team Git", EditorStyles.largeLabel);
            EditorGUILayout.LabelField("将当前 Unity 工程安全地初始化到团队 NAS Gitea。", EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(6f);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("工程目录", settings.ProjectRoot);
                EditorGUILayout.TextField("仓库名称", settings.RepositoryName);
            }
        }

        private void DrawConfiguration()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("连接配置", EditorStyles.boldLabel);

            settings.owner = EditorGUILayout.TextField(new GUIContent("Gitea 用户/组织", "推荐使用六人团队共享的 Gitea 组织。"), settings.owner);
            settings.lanHost = EditorGUILayout.TextField("NAS 局域网地址", settings.lanHost);
            settings.sshPort = EditorGUILayout.IntField("SSH 端口", settings.sshPort);
            settings.tailnetHost = EditorGUILayout.TextField("Tailnet 主机", settings.tailnetHost);
            settings.lanWebBaseUrl = EditorGUILayout.TextField("Gitea 局域网 Web 地址", settings.lanWebBaseUrl);
            settings.webBaseUrl = EditorGUILayout.TextField("Gitea Tailscale Web 地址", settings.webBaseUrl);
            settings.defaultBranch = EditorGUILayout.TextField("默认分支", settings.defaultBranch);
            settings.initialCommitMessage = EditorGUILayout.TextField("首次提交说明", settings.initialCommitMessage);

            EditorGUILayout.BeginHorizontal();
            settings.sshPrivateKeyPath = EditorGUILayout.TextField("SSH 私钥", settings.sshPrivateKeyPath);
            if (GUILayout.Button("选择", GUILayout.Width(55f)))
            {
                var initialDirectory = Path.GetDirectoryName(settings.sshPrivateKeyPath);
                var selected = EditorUtility.OpenFilePanel("选择 Gitea SSH 私钥", initialDirectory, string.Empty);
                if (!string.IsNullOrWhiteSpace(selected))
                    settings.sshPrivateKeyPath = selected;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox(
                "初始化只处理本地仓库，不会自动 Push。确认云端不存在同名仓库后，可用独立的“首次 Push”按钮通过 SSH Push To Create 创建公开仓库。插件不会保存 Gitea 密码或 API Token。",
                MessageType.Info);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("保存配置", GUILayout.Height(26f)))
            {
                settings.Save();
                AddLog("配置已保存到本机 EditorPrefs。工程中不保存密码或 Token。");
                RefreshPreflight();
            }

            using (new EditorGUI.DisabledScope(checking || initializing || rebinding || firstPushing || UnityTeamGitOperationGate.IsBusy))
            {
                if (GUILayout.Button("重新检查", GUILayout.Height(26f)))
                    RefreshPreflight();
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawPreflight()
        {
            EditorGUILayout.Space(10f);
            EditorGUILayout.LabelField("初始化检查", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(statusMessage, EditorStyles.miniLabel);

            if (checks.Count == 0)
            {
                EditorGUILayout.HelpBox(checking ? "正在检查环境……" : "尚未执行检查。", MessageType.Info);
                return;
            }

            foreach (var check in checks)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                var previousColor = GUI.color;
                GUI.color = StateColor(check.State);
                GUILayout.Label(StateIcon(check.State), GUILayout.Width(20f));
                GUI.color = previousColor;
                EditorGUILayout.LabelField(check.Name, EditorStyles.boldLabel);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.LabelField(check.Detail, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.EndVertical();
            }
        }

        private void DrawActions()
        {
            EditorGUILayout.Space(10f);
            var hasRepository = Directory.Exists(Path.Combine(settings.ProjectRoot, ".git")) ||
                                File.Exists(Path.Combine(settings.ProjectRoot, ".git"));
            var buttonLabel = hasRepository ? "检查并修复本地仓库配置" : "初始化本地仓库（不 Push）";
            var busy = checking || initializing || rebinding || firstPushing || UnityTeamGitOperationGate.IsBusy;
            var blocked = busy || checks.Count == 0 || checks.Any(check => check.IsBlocking);

            using (new EditorGUI.DisabledScope(blocked))
            {
                if (GUILayout.Button(initializing ? "正在执行……" : buttonLabel, GUILayout.Height(38f)))
                    BeginInitialization();
            }

            using (new EditorGUI.DisabledScope(busy || !hasRepository))
            {
                if (GUILayout.Button(rebinding ? "正在重新绑定……" : "重新绑定仓库", GUILayout.Height(32f)))
                    BeginRebind();
            }

            var firstPushBlocked = busy || checks.Count == 0 || checks.Any(check => check.IsBlocking) ||
                                   repositoryProbe == null || !repositoryProbe.CanFirstPush;
            using (new EditorGUI.DisabledScope(firstPushBlocked))
            {
                if (GUILayout.Button(firstPushing ? "正在首次 Push……" : "首次 Push（创建公开 Gitea 仓库）", GUILayout.Height(38f)))
                    BeginFirstPush();
            }

        }

        private void DrawLogs()
        {
            EditorGUILayout.Space(10f);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("执行日志", EditorStyles.boldLabel);
            if (GUILayout.Button("清空", GUILayout.Width(55f)))
                logs.Clear();
            EditorGUILayout.EndHorizontal();

            logScroll = EditorGUILayout.BeginScrollView(logScroll, EditorStyles.helpBox, GUILayout.MinHeight(150f));
            if (logs.Count == 0)
                EditorGUILayout.LabelField("尚无日志。", EditorStyles.miniLabel);
            else
                EditorGUILayout.SelectableLabel(string.Join(Environment.NewLine, logs), EditorStyles.wordWrappedMiniLabel, GUILayout.ExpandHeight(true));
            EditorGUILayout.EndScrollView();
        }

        private async void RefreshPreflight()
        {
            if (checking || initializing || rebinding || firstPushing || UnityTeamGitOperationGate.IsBusy || settings == null)
                return;

            checking = true;
            statusMessage = "正在检查……";
            Repaint();

            try
            {
                settings.Save();
                var initializer = new UnityTeamGitInitializer(settings);
                checks = await initializer.RunPreflightAsync();
                repositoryProbe = initializer.LastRepositoryProbe;
                var errors = checks.Count(check => check.State == PreflightState.Error);
                var warnings = checks.Count(check => check.State == PreflightState.Warning);
                statusMessage = errors == 0
                    ? "检查完成：" + warnings + " 个提示，可以继续"
                    : "检查完成：" + errors + " 个问题需要处理";
            }
            catch (Exception exception)
            {
                checks = new List<PreflightCheck>
                {
                    new PreflightCheck
                    {
                        Name = "环境检查异常",
                        Detail = exception.Message,
                        State = PreflightState.Error
                    }
                };
                statusMessage = "检查失败";
                AddLog(exception.ToString());
            }
            finally
            {
                checking = false;
                Repaint();
            }
        }

        private async void BeginInitialization()
        {
            if (initializing || rebinding || firstPushing || UnityTeamGitOperationGate.IsBusy || EditorUnsafe())
                return;

            settings.Save();
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                AddLog("用户取消保存场景，初始化未开始。");
                return;
            }

            AssetDatabase.SaveAssets();
            if (!UnityTeamGitOperationGate.TryEnter())
            {
                AddLog("另一个 Git 操作正在执行，初始化已取消。");
                return;
            }
            initializing = true;
            statusMessage = "正在初始化……";
            AddLog("开始处理工程 " + settings.RepositoryName);
            Repaint();

            try
            {
                var initializer = new UnityTeamGitInitializer(settings);
                var result = await initializer.InitializeAsync(AddLog);
                AssetDatabase.Refresh();
                statusMessage = result.Success ? "完成" : "失败";
                AddLog(result.Message);
                EditorUtility.DisplayDialog("Unity Team Git", result.Message, "确定");
            }
            catch (Exception exception)
            {
                statusMessage = "初始化失败；本地文件和提交已保留，可修正后重试";
                AddLog(exception.ToString());
                EditorUtility.DisplayDialog(
                    "Unity Team Git 初始化失败",
                    exception.Message + "\n\n本地仓库不会被删除，也不会执行强制 Push。详情请查看窗口日志。",
                    "确定");
            }
            finally
            {
                initializing = false;
                UnityTeamGitOperationGate.Exit();
                Repaint();
                RefreshPreflight();
            }
        }

        private async void BeginRebind()
        {
            if (rebinding || checking || initializing || firstPushing || UnityTeamGitOperationGate.IsBusy || EditorUnsafe())
                return;

            var validationErrors = settings.Validate().ToList();
            if (validationErrors.Count > 0)
            {
                EditorUtility.DisplayDialog("无法重新绑定仓库", string.Join(Environment.NewLine, validationErrors), "确定");
                return;
            }

            settings.Save();
            var existingOrigin = repositoryProbe != null && repositoryProbe.OriginConfigured
                ? repositoryProbe.OriginUrl
                : "（未配置）";
            var message =
                "该操作只覆盖当前工程的本地远程地址，不会删除 .git、本地 Commit 或工作区文件，也不会删除/修改旧 Gitea 仓库。" +
                Environment.NewLine + Environment.NewLine +
                "现有 origin：" + existingOrigin + Environment.NewLine + Environment.NewLine +
                "新 origin：" + settings.OriginUrl + Environment.NewLine +
                "新 tailnet：" + settings.TailnetUrl + Environment.NewLine + Environment.NewLine +
                "目标仓库不存在时，重新检查后将允许再次“首次 Push”。是否继续？";
            if (!EditorUtility.DisplayDialog("确认重新绑定仓库", message, "重新绑定", "取消"))
                return;

            if (!UnityTeamGitOperationGate.TryEnter())
            {
                AddLog("另一个 Git 操作正在执行，重新绑定已取消。");
                return;
            }

            rebinding = true;
            statusMessage = "正在重新绑定仓库……";
            AddLog("开始重新绑定仓库：" + settings.OriginUrl);
            Repaint();

            try
            {
                var initializer = new UnityTeamGitInitializer(settings);
                var result = await initializer.RebindRemotesAsync(AddLog);
                statusMessage = "重新绑定完成";
                AddLog(result);
                EditorUtility.DisplayDialog("Unity Team Git", result, "确定");
            }
            catch (Exception exception)
            {
                statusMessage = "重新绑定失败；本地文件和 Commit 未受影响";
                AddLog(exception.ToString());
                EditorUtility.DisplayDialog(
                    "重新绑定仓库失败",
                    exception.Message + Environment.NewLine + Environment.NewLine +
                    "插件没有执行 Push，也没有操作旧云端仓库。详情请查看窗口日志。",
                    "确定");
            }
            finally
            {
                rebinding = false;
                UnityTeamGitOperationGate.Exit();
                Repaint();
                RefreshPreflight();
            }
        }

        private async void BeginFirstPush()
        {
            if (firstPushing || checking || initializing || rebinding || UnityTeamGitOperationGate.IsBusy || EditorUnsafe())
                return;

            settings.Save();
            if (!UnityTeamGitOperationGate.TryEnter())
            {
                AddLog("另一个 Git 操作正在执行，首次 Push 已取消。");
                return;
            }
            firstPushing = true;
            statusMessage = "正在首次 Push……";
            AddLog("开始首次 Push：" + settings.OriginUrl);
            Repaint();

            try
            {
                var initializer = new UnityTeamGitInitializer(settings);
                var result = await initializer.FirstPushAsync(AddLog);
                statusMessage = result.Success ? "首次 Push 完成" : "首次 Push 失败";
                AddLog(result.Message);
                EditorUtility.DisplayDialog("Unity Team Git", result.Message, "确定");
            }
            catch (Exception exception)
            {
                statusMessage = "首次 Push 失败；没有执行强制 Push";
                AddLog(exception.ToString());
                EditorUtility.DisplayDialog(
                    "Unity Team Git 首次 Push 失败",
                    exception.Message + "\n\n插件不会覆盖已有云端仓库，也不会执行强制 Push。详情请查看窗口日志。",
                    "确定");
            }
            finally
            {
                firstPushing = false;
                UnityTeamGitOperationGate.Exit();
                Repaint();
                RefreshPreflight();
            }
        }

        private static bool EditorUnsafe()
        {
            return EditorApplication.isCompiling ||
                   EditorApplication.isUpdating ||
                   EditorApplication.isPlayingOrWillChangePlaymode;
        }

        private void AddLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            logs.Add("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message.Trim());
            logScroll.y = float.MaxValue;
            Repaint();
        }

        private static string StateIcon(PreflightState state)
        {
            switch (state)
            {
                case PreflightState.Pass: return "●";
                case PreflightState.Warning: return "▲";
                default: return "■";
            }
        }

        private static Color StateColor(PreflightState state)
        {
            switch (state)
            {
                case PreflightState.Pass: return new Color(0.25f, 0.75f, 0.35f);
                case PreflightState.Warning: return new Color(1f, 0.7f, 0.2f);
                default: return new Color(1f, 0.3f, 0.3f);
            }
        }
    }
}
