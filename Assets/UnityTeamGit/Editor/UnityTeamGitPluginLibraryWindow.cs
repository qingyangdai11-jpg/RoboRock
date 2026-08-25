using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Blackbox.UnityTeamGit.Editor
{
    internal sealed class UnityTeamGitPluginLibraryWindow : EditorWindow
    {
        private const string LibraryRootKey = "Blackbox.UnityTeamGit.PluginLibrary.Root";
        private const string DestinationKeyPrefix = "Blackbox.UnityTeamGit.PluginLibrary.Destination.";
        private const string DefaultLibraryRoot = @"\\Dxp-gt-blackbox\公司技术部资料\unity plugin";

        private readonly List<NasPluginDescriptor> plugins = new List<NasPluginDescriptor>();
        private Vector2 installedPluginScroll;
        private Vector2 availablePluginScroll;
        private string libraryRootInput = DefaultLibraryRoot;
        private string libraryRoot = string.Empty;
        private string destinationAssetPath = "Assets";
        private string searchText = string.Empty;
        private string statusMessage = "请配置 NAS 插件库共享文件夹。";

        [MenuItem("Tools/Unity Team Git/NAS Plugin Library", priority = 30)]
        public static void ShowWindow()
        {
            var window = GetWindow<UnityTeamGitPluginLibraryWindow>("NAS Plugin Library");
            window.minSize = new Vector2(720f, 520f);
            window.Show();
        }

        private void OnEnable()
        {
            NasPluginLibraryService.EnsureInitialized();
            libraryRootInput = EditorPrefs.GetString(LibraryRootKey, DefaultLibraryRoot);
            destinationAssetPath = EditorPrefs.GetString(ProjectDestinationKey(), "Assets");
            if (!string.IsNullOrWhiteSpace(libraryRootInput))
                ConnectToLibrary(false);
        }

        private void OnFocus()
        {
            if (!string.IsNullOrWhiteSpace(libraryRoot))
                RefreshPlugins();
            else
                Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("Unity Team Git · NAS 插件库", EditorStyles.largeLabel);
            EditorGUILayout.LabelField(
                "NAS 根目录下的每个一级文件夹视为一个插件。文件夹内放置一个 .unitypackage，或直接放置完整代码/资源。",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(8f);
            DrawConfiguration();
            EditorGUILayout.Space(8f);
            DrawToolbar();
            EditorGUILayout.Space(4f);
            DrawPluginLists();
        }

        private void DrawConfiguration()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.LabelField("插件库配置", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            libraryRootInput = EditorGUILayout.TextField(
                new GUIContent("NAS 插件库路径", "默认：" + DefaultLibraryRoot),
                libraryRootInput);
            if (GUILayout.Button("选择", GUILayout.Width(55f)))
            {
                var selected = EditorUtility.OpenFolderPanel("选择 NAS 插件库文件夹", libraryRootInput, string.Empty);
                if (!string.IsNullOrWhiteSpace(selected))
                    libraryRootInput = selected;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            destinationAssetPath = EditorGUILayout.TextField(
                new GUIContent("代码安装目录", "纯代码插件会安装到此 Assets 目录下，并保留插件文件夹名。UnityPackage 自带导入路径，不使用此设置。"),
                destinationAssetPath);
            if (GUILayout.Button("使用 Project 选中目录", GUILayout.Width(155f)))
                UseSelectedProjectDirectory();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("保存并连接", GUILayout.Height(26f)))
                ConnectToLibrary(true);
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(libraryRoot)))
            {
                if (GUILayout.Button("刷新", GUILayout.Height(26f), GUILayout.Width(80f)))
                    RefreshPlugins();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.HelpBox(
                "此面板不保存 NAS 用户名或密码。请先在 Windows 中取得 SMB 共享目录访问权限。插件安装信息保存在当前工程的 ProjectSettings 中。",
                MessageType.Info);
            EditorGUILayout.EndVertical();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            searchText = EditorGUILayout.TextField(new GUIContent("筛选", "按插件名称筛选。"), searchText);
            EditorGUILayout.LabelField(statusMessage, EditorStyles.wordWrappedMiniLabel);

            if (NasPluginLibraryService.HasPendingPackageImport)
            {
                EditorGUILayout.HelpBox(
                    "UnityPackage 导入界面已打开。请在原生导入窗口中完成或取消导入；列表会在导入结束后自动更新。",
                    MessageType.Info);
                using (new EditorGUI.DisabledScope(EditorApplication.isCompiling || EditorApplication.isUpdating))
                {
                    if (GUILayout.Button("导入窗口已不存在？清理挂起状态"))
                        ResetPendingPackageImport();
                }
            }
            else if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorGUILayout.HelpBox("Unity 正在编译、导入或切换 Play Mode，安装与卸载暂时禁用。", MessageType.Warning);
            }

            EditorGUILayout.EndVertical();
        }

        private void DrawPluginLists()
        {
            var filter = (searchText ?? string.Empty).Trim();
            var visible = plugins
                .Where(plugin => MatchesFilter(plugin, filter))
                .OrderBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var installedDescriptors = visible
                .Where(plugin => NasPluginLibraryService.IsInstalled(plugin.Id))
                .ToList();
            var availableDescriptors = visible
                .Where(plugin => !NasPluginLibraryService.IsInstalled(plugin.Id))
                .ToList();
            var descriptorIds = new HashSet<string>(plugins.Select(plugin => plugin.Id), StringComparer.OrdinalIgnoreCase);
            var orphanedInstallations = NasPluginLibraryService.GetInstallations()
                .Where(installation => !descriptorIds.Contains(installation.PluginId) &&
                                       (string.IsNullOrWhiteSpace(filter) ||
                                        installation.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(installation => installation.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var columnWidth = Mathf.Max(320f, (position.width - 18f) * 0.5f);
            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical(GUILayout.Width(columnWidth), GUILayout.ExpandHeight(true));
            DrawSectionHeader("已安装", installedDescriptors.Count + orphanedInstallations.Count);
            installedPluginScroll = EditorGUILayout.BeginScrollView(installedPluginScroll, EditorStyles.helpBox, GUILayout.ExpandHeight(true));
            if (installedDescriptors.Count == 0 && orphanedInstallations.Count == 0)
                EditorGUILayout.LabelField("当前工程尚未通过此面板安装插件。", EditorStyles.wordWrappedMiniLabel);
            foreach (var plugin in installedDescriptors)
                DrawPluginCard(plugin, NasPluginLibraryService.GetInstallation(plugin.Id));
            foreach (var installation in orphanedInstallations)
                DrawOrphanedInstallationCard(installation);
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            GUILayout.Space(6f);

            EditorGUILayout.BeginVertical(GUILayout.Width(columnWidth), GUILayout.ExpandHeight(true));
            DrawSectionHeader("未安装", availableDescriptors.Count);
            availablePluginScroll = EditorGUILayout.BeginScrollView(availablePluginScroll, EditorStyles.helpBox, GUILayout.ExpandHeight(true));
            if (availableDescriptors.Count == 0)
            {
                EditorGUILayout.LabelField(
                    string.IsNullOrWhiteSpace(libraryRoot) ? "连接 NAS 插件库后将在此显示可安装插件。" : "没有匹配的未安装插件。",
                    EditorStyles.wordWrappedMiniLabel);
            }
            foreach (var plugin in availableDescriptors)
                DrawPluginCard(plugin, null);
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
        }

        private static void DrawSectionHeader(string title, int count)
        {
            EditorGUILayout.LabelField(title + "（" + count + "）", EditorStyles.boldLabel);
        }

        private void DrawPluginCard(NasPluginDescriptor plugin, NasPluginInstallation installation)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(plugin.Name, EditorStyles.boldLabel, GUILayout.ExpandWidth(true));
            GUILayout.Label(plugin.KindLabel, EditorStyles.miniLabel, GUILayout.Width(95f));
            GUILayout.Label(installation == null ? "未安装" : "已安装", EditorStyles.miniBoldLabel, GUILayout.Width(52f));
            EditorGUILayout.EndHorizontal();

            if (!string.IsNullOrWhiteSpace(plugin.DetailLabel))
                EditorGUILayout.LabelField(plugin.DetailLabel, EditorStyles.wordWrappedMiniLabel);

            if (!plugin.IsValid)
                EditorGUILayout.HelpBox(plugin.ValidationError, MessageType.Error);

            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (installation == null)
            {
                var blocked = !plugin.IsValid || IsOperationBlocked() || NasPluginLibraryService.HasPendingPackageImport;
                using (new EditorGUI.DisabledScope(blocked))
                {
                    if (GUILayout.Button(plugin.Kind == NasPluginKind.UnityPackage ? "Download / 打开导入" : "Download / 安装", GUILayout.Width(155f)))
                        InstallPlugin(plugin);
                }
            }
            else
            {
                using (new EditorGUI.DisabledScope(IsOperationBlocked() || NasPluginLibraryService.HasPendingPackageImport))
                {
                    if (GUILayout.Button("Remove / 卸载", GUILayout.Width(120f)))
                        RemovePlugin(installation);
                }
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void DrawOrphanedInstallationCard(NasPluginInstallation installation)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(installation.Name, EditorStyles.boldLabel, GUILayout.ExpandWidth(true));
            GUILayout.Label(installation.InstallKind == NasPluginKind.UnityPackage.ToString() ? "UnityPackage" : "代码目录", EditorStyles.miniLabel, GUILayout.Width(95f));
            GUILayout.Label("已安装", EditorStyles.miniBoldLabel, GUILayout.Width(52f));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.HelpBox("NAS 中未找到对应插件文件夹，但仍可按本地安装清单卸载。", MessageType.Warning);
            EditorGUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            using (new EditorGUI.DisabledScope(IsOperationBlocked() || NasPluginLibraryService.HasPendingPackageImport))
            {
                if (GUILayout.Button("Remove / 卸载", GUILayout.Width(120f)))
                    RemovePlugin(installation);
            }
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
        }

        private void ConnectToLibrary(bool persist)
        {
            try
            {
                var candidate = NasPluginLibraryPaths.NormalizeExistingDirectory(libraryRootInput);
                NasPluginLibraryPaths.EnsureNotReparsePoint(candidate, "插件库根目录");
                libraryRoot = candidate;
                libraryRootInput = candidate;
                if (persist)
                    EditorPrefs.SetString(LibraryRootKey, candidate);
                RefreshPlugins();
            }
            catch (Exception exception)
            {
                libraryRoot = string.Empty;
                plugins.Clear();
                statusMessage = "连接失败：" + exception.Message;
                if (persist)
                    EditorUtility.DisplayDialog("无法连接 NAS 插件库", exception.Message, "确定");
            }
        }

        private void RefreshPlugins()
        {
            plugins.Clear();
            try
            {
                if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
                    throw new DirectoryNotFoundException("NAS 插件库不可访问，请检查共享目录或网络连接。");

                NasPluginLibraryPaths.EnsureNotReparsePoint(libraryRoot, "插件库根目录");
                foreach (var directory in new DirectoryInfo(libraryRoot).GetDirectories()
                             .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
                {
                    plugins.Add(NasPluginDescriptor.Read(directory));
                }

                var validCount = plugins.Count(plugin => plugin.IsValid);
                var invalidCount = plugins.Count - validCount;
                statusMessage = "已读取 " + plugins.Count + " 个插件文件夹，其中 " + validCount + " 个可安装" +
                                (invalidCount > 0 ? "，" + invalidCount + " 个需要修正。" : "。");
            }
            catch (Exception exception)
            {
                statusMessage = "读取失败：" + exception.Message;
            }

            Repaint();
        }

        private void InstallPlugin(NasPluginDescriptor plugin)
        {
            if (!UnityTeamGitOperationGate.TryEnter())
            {
                EditorUtility.DisplayDialog("无法安装", "另一个 Unity Team Git 操作正在执行。", "确定");
                return;
            }

            try
            {
                EditorPrefs.SetString(ProjectDestinationKey(), destinationAssetPath);
                if (plugin.Kind == NasPluginKind.UnityPackage)
                {
                    NasPluginLibraryService.BeginPackageInstall(plugin);
                    statusMessage = "已打开 " + plugin.Name + " 的 UnityPackage 导入界面。";
                }
                else
                {
                    var installedPath = NasPluginLibraryService.InstallCodePlugin(plugin, destinationAssetPath);
                    statusMessage = "安装完成：" + installedPath;
                    EditorUtility.DisplayDialog("NAS 插件库", plugin.Name + " 安装完成。\n\n" + installedPath, "确定");
                }
            }
            catch (OperationCanceledException)
            {
                statusMessage = "安装已取消。";
            }
            catch (Exception exception)
            {
                statusMessage = "安装失败：" + exception.Message;
                EditorUtility.DisplayDialog("插件安装失败", exception.Message, "确定");
            }
            finally
            {
                UnityTeamGitOperationGate.Exit();
                Repaint();
            }
        }

        private void ResetPendingPackageImport()
        {
            if (!EditorUtility.DisplayDialog(
                    "清理挂起的导入",
                    "仅当 Unity 原生导入窗口已经关闭、但列表仍显示正在导入时使用。清理会回滚该安装包可能留下的新资源。",
                    "清理并回滚",
                    "取消"))
                return;

            try
            {
                NasPluginLibraryService.ResetPendingPackageImport();
                statusMessage = "已清理挂起的 UnityPackage 导入状态。";
            }
            catch (Exception exception)
            {
                statusMessage = "清理失败：" + exception.Message;
                EditorUtility.DisplayDialog("清理失败", exception.Message, "确定");
            }
        }

        private void RemovePlugin(NasPluginInstallation installation)
        {
            if (!UnityTeamGitOperationGate.TryEnter())
            {
                EditorUtility.DisplayDialog("无法卸载", "另一个 Unity Team Git 操作正在执行。", "确定");
                return;
            }

            try
            {
                var result = NasPluginLibraryService.Remove(installation);
                if (!result.Removed)
                {
                    statusMessage = "卸载已取消。";
                    return;
                }

                statusMessage = result.RetainedDirectories > 0
                    ? "卸载完成；有 " + result.RetainedDirectories + " 个包含其他内容的目录被保留。"
                    : "卸载完成：" + installation.Name;
                EditorUtility.DisplayDialog("NAS 插件库", statusMessage, "确定");
                RefreshPlugins();
            }
            catch (Exception exception)
            {
                statusMessage = "卸载失败：" + exception.Message;
                EditorUtility.DisplayDialog("插件卸载失败", exception.Message, "确定");
            }
            finally
            {
                UnityTeamGitOperationGate.Exit();
                Repaint();
            }
        }

        private void UseSelectedProjectDirectory()
        {
            var assetPath = AssetDatabase.GetAssetPath(Selection.activeObject);
            if (string.IsNullOrWhiteSpace(assetPath))
                destinationAssetPath = "Assets";
            else if (AssetDatabase.IsValidFolder(assetPath))
                destinationAssetPath = assetPath.Replace('\\', '/');
            else
            {
                var parent = Path.GetDirectoryName(assetPath);
                destinationAssetPath = string.IsNullOrWhiteSpace(parent) ? "Assets" : parent.Replace('\\', '/');
            }

            EditorPrefs.SetString(ProjectDestinationKey(), destinationAssetPath);
        }

        private static bool MatchesFilter(NasPluginDescriptor plugin, string filter)
        {
            return string.IsNullOrWhiteSpace(filter) ||
                   plugin.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsOperationBlocked()
        {
            return EditorApplication.isCompiling ||
                   EditorApplication.isUpdating ||
                   EditorApplication.isPlayingOrWillChangePlaymode ||
                   UnityTeamGitOperationGate.IsBusy;
        }

        private static string ProjectDestinationKey()
        {
            return DestinationKeyPrefix + Hash128.Compute(Application.dataPath);
        }

        internal static void NotifyLibraryChanged(string message)
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<UnityTeamGitPluginLibraryWindow>())
            {
                window.statusMessage = message;
                if (!string.IsNullOrWhiteSpace(window.libraryRoot))
                    window.RefreshPlugins();
                else
                    window.Repaint();
            }
        }
    }

    internal enum NasPluginKind
    {
        CodeFolder,
        UnityPackage
    }

    internal sealed class NasPluginDescriptor
    {
        public string Id;
        public string Name;
        public string FolderPath;
        public string PackagePath;
        public NasPluginKind Kind;
        public bool IsValid;
        public string ValidationError;

        public string KindLabel
        {
            get { return Kind == NasPluginKind.UnityPackage ? "UnityPackage" : "代码目录"; }
        }

        public string DetailLabel
        {
            get
            {
                if (Kind == NasPluginKind.UnityPackage && !string.IsNullOrWhiteSpace(PackagePath))
                    return "安装包：" + Path.GetFileName(PackagePath) + "（" + NasPluginLibraryPaths.FormatBytes(new FileInfo(PackagePath).Length) + "）";
                return Kind == NasPluginKind.CodeFolder ? "代码将作为独立文件夹复制到当前工程。" : string.Empty;
            }
        }

        public static NasPluginDescriptor Read(DirectoryInfo directory)
        {
            var descriptor = new NasPluginDescriptor
            {
                Name = directory.Name,
                FolderPath = directory.FullName,
                Id = NasPluginLibraryPaths.CreatePluginId(directory.Name),
                Kind = NasPluginKind.CodeFolder
            };

            try
            {
                NasPluginLibraryPaths.EnsureNotReparsePoint(directory.FullName, "插件文件夹");
                var packageFiles = directory.GetFiles("*.unitypackage", SearchOption.TopDirectoryOnly)
                    .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (packageFiles.Count == 1)
                {
                    descriptor.Kind = NasPluginKind.UnityPackage;
                    descriptor.PackagePath = packageFiles[0].FullName;
                }

                var errors = new List<string>();
                if (packageFiles.Count > 1)
                    errors.Add("检测到多个 .unitypackage；每个插件文件夹只能放置一个安装包。");
                if (packageFiles.Count == 0 && !HasCodePayload(directory))
                    errors.Add("未找到 .unitypackage 或可导入的代码/资源文件。");

                descriptor.IsValid = errors.Count == 0;
                descriptor.ValidationError = string.Join("\n", errors);
            }
            catch (Exception exception)
            {
                descriptor.IsValid = false;
                descriptor.ValidationError = "无法读取插件文件夹：" + exception.Message;
            }

            return descriptor;
        }

        private static bool HasCodePayload(DirectoryInfo directory)
        {
            foreach (var file in directory.GetFiles("*", SearchOption.TopDirectoryOnly))
            {
                if (file.Extension.Equals(".unitypackage", StringComparison.OrdinalIgnoreCase) ||
                    file.Extension.Equals(".meta", StringComparison.OrdinalIgnoreCase))
                    continue;
                return true;
            }

            return directory.GetDirectories().Length > 0;
        }
    }

    [InitializeOnLoad]
    internal static class NasPluginLibraryService
    {
        private static NasPluginLibraryState state;
        private static bool initialized;

        static NasPluginLibraryService()
        {
            EnsureInitialized();
        }

        public static bool HasPendingPackageImport
        {
            get
            {
                EnsureInitialized();
                return state.PendingPackage != null && !string.IsNullOrWhiteSpace(state.PendingPackage.PluginId);
            }
        }

        public static void EnsureInitialized()
        {
            if (initialized)
                return;

            state = NasPluginLibraryStateStore.Load();
            AssetDatabase.importPackageCompleted -= OnPackageImportCompleted;
            AssetDatabase.importPackageCompleted += OnPackageImportCompleted;
            AssetDatabase.importPackageCancelled -= OnPackageImportCancelled;
            AssetDatabase.importPackageCancelled += OnPackageImportCancelled;
            AssetDatabase.importPackageFailed -= OnPackageImportFailed;
            AssetDatabase.importPackageFailed += OnPackageImportFailed;
            initialized = true;
        }

        public static bool IsInstalled(string pluginId)
        {
            return GetInstallation(pluginId) != null;
        }

        public static NasPluginInstallation GetInstallation(string pluginId)
        {
            EnsureInitialized();
            return state.Plugins.FirstOrDefault(item => string.Equals(item.PluginId, pluginId, StringComparison.OrdinalIgnoreCase));
        }

        public static List<NasPluginInstallation> GetInstallations()
        {
            EnsureInitialized();
            return state.Plugins.ToList();
        }

        public static void BeginPackageInstall(NasPluginDescriptor plugin)
        {
            EnsureInitialized();
            if (HasPendingPackageImport)
                throw new InvalidOperationException("另一个 UnityPackage 正在等待导入完成。");
            if (IsInstalled(plugin.Id))
                throw new InvalidOperationException(plugin.Name + " 已安装。");
            if (string.IsNullOrWhiteSpace(plugin.PackagePath) || !File.Exists(plugin.PackagePath))
                throw new FileNotFoundException("找不到 UnityPackage。", plugin.PackagePath);

            NasPluginLibraryPaths.EnsureNotReparsePoint(plugin.PackagePath, "UnityPackage");
            var packageAssetPaths = UnityPackagePathReader.ReadAssetPaths(plugin.PackagePath);
            if (packageAssetPaths.Count == 0)
                throw new InvalidDataException("安装包中没有可识别的 Assets 资源路径。");

            var conflicts = new List<string>();
            var pendingAssets = new List<NasPendingAsset>();
            foreach (var assetPath in packageAssetPaths)
            {
                var fullPath = NasPluginLibraryPaths.AssetPathToFullPath(assetPath);
                var isFile = File.Exists(fullPath);
                var isDirectory = Directory.Exists(fullPath);
                var metaPath = fullPath + ".meta";
                if (isFile || (!isDirectory && File.Exists(metaPath)))
                {
                    conflicts.Add(assetPath);
                    continue;
                }

                pendingAssets.Add(new NasPendingAsset
                {
                    AssetPath = assetPath,
                    ExistedBefore = isDirectory,
                    WasDirectory = isDirectory,
                    OriginalMetaBase64 = File.Exists(metaPath) ? Convert.ToBase64String(File.ReadAllBytes(metaPath)) : string.Empty
                });
            }

            if (conflicts.Count > 0)
            {
                var preview = string.Join("\n", conflicts.Take(12).ToArray());
                if (conflicts.Count > 12)
                    preview += "\n…以及另外 " + (conflicts.Count - 12) + " 项";
                throw new InvalidOperationException(
                    "为保证 Remove 能干净且安全地卸载，安装包不能覆盖工程中已有文件。请先处理以下冲突：\n\n" + preview);
            }

            var projectRoot = NasPluginLibraryPaths.ProjectRoot;
            var stagingDirectory = Path.Combine(
                projectRoot,
                "Library",
                "UnityTeamGit",
                "NasPluginLibrary",
                "PackageStaging",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDirectory);
            var stagedPackage = Path.Combine(stagingDirectory, Path.GetFileName(plugin.PackagePath));
            File.Copy(plugin.PackagePath, stagedPackage, true);

            state.PendingPackage = new NasPendingPackage
            {
                PluginId = plugin.Id,
                Name = plugin.Name,
                SourceFolderName = Path.GetFileName(plugin.FolderPath),
                PackageFileName = Path.GetFileName(plugin.PackagePath),
                StagedPackagePath = stagedPackage,
                Assets = pendingAssets
            };
            NasPluginLibraryStateStore.Save(state);

            try
            {
                AssetDatabase.ImportPackage(stagedPackage, true);
            }
            catch
            {
                ClearPendingPackage();
                throw;
            }
        }

        public static string InstallCodePlugin(NasPluginDescriptor plugin, string destinationAssetPath)
        {
            EnsureInitialized();
            if (IsInstalled(plugin.Id))
                throw new InvalidOperationException(plugin.Name + " 已安装。");
            if (!Directory.Exists(plugin.FolderPath))
                throw new DirectoryNotFoundException("插件源文件夹不存在：" + plugin.FolderPath);

            var destinationAssetRoot = NasPluginLibraryPaths.NormalizeAssetDirectory(destinationAssetPath);
            var targetAssetPath = NasPluginLibraryPaths.CombineAssetPath(destinationAssetRoot, Path.GetFileName(plugin.FolderPath));
            var targetFullPath = NasPluginLibraryPaths.AssetPathToFullPath(targetAssetPath);
            if (Directory.Exists(targetFullPath) || File.Exists(targetFullPath) || File.Exists(targetFullPath + ".meta"))
                throw new IOException("目标已存在，无法保证后续干净卸载：" + targetAssetPath);

            var plan = NasCodeCopyPlan.Build(plugin);
            var message =
                "插件：" + plugin.Name + Environment.NewLine +
                "目标：" + targetAssetPath + Environment.NewLine +
                "文件：" + plan.Files.Count + " 个，" + NasPluginLibraryPaths.FormatBytes(plan.TotalBytes) + Environment.NewLine + Environment.NewLine +
                "导入脚本或 DLL 后，Unity 可能立即加载其中的 Editor 代码。请确认 NAS 来源可信。";
            if (!EditorUtility.DisplayDialog("确认安装代码插件", message, "Download / 安装", "取消"))
                throw new OperationCanceledException("安装已取消。");

            var stagingRoot = Path.Combine(
                NasPluginLibraryPaths.ProjectRoot,
                "Library",
                "UnityTeamGit",
                "NasPluginLibrary",
                "CodeStaging",
                Guid.NewGuid().ToString("N"));

            var installationSaved = false;
            try
            {
                plan.Stage(stagingRoot);
                plan.Commit(stagingRoot, NasPluginLibraryPaths.AssetPathToFullPath(destinationAssetRoot));

                var installation = plan.CreateInstallation(plugin, destinationAssetRoot);
                state.Plugins.RemoveAll(item => string.Equals(item.PluginId, plugin.Id, StringComparison.OrdinalIgnoreCase));
                state.Plugins.Add(installation);
                NasPluginLibraryStateStore.Save(state);
                installationSaved = true;

                AssetDatabase.Refresh();
                var installedAsset = AssetDatabase.LoadMainAssetAtPath(targetAssetPath);
                if (installedAsset != null)
                {
                    Selection.activeObject = installedAsset;
                    EditorGUIUtility.PingObject(installedAsset);
                }

                return targetAssetPath;
            }
            catch
            {
                if (installationSaved)
                {
                    state.Plugins.RemoveAll(item => string.Equals(item.PluginId, plugin.Id, StringComparison.OrdinalIgnoreCase));
                    NasPluginLibraryStateStore.Save(state);
                }
                if (Directory.Exists(targetFullPath))
                    Directory.Delete(targetFullPath, true);
                if (File.Exists(targetFullPath + ".meta"))
                    File.Delete(targetFullPath + ".meta");
                throw;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                NasPluginLibraryPaths.DeleteDirectoryBestEffort(stagingRoot);
            }
        }

        public static NasRemoveResult Remove(NasPluginInstallation installation)
        {
            EnsureInitialized();
            var tracked = GetInstallation(installation.PluginId);
            if (tracked == null)
                throw new InvalidOperationException("找不到该插件的本地安装清单。");

            var fileCount = tracked.Assets.Count(asset => !asset.IsDirectory && !asset.ExistedBefore);
            var confirmation =
                "插件：" + tracked.Name + Environment.NewLine +
                "将移除清单中的 " + fileCount + " 个文件，并清理空目录。" + Environment.NewLine + Environment.NewLine +
                "不在安装清单中的文件不会被删除。";
            if (!EditorUtility.DisplayDialog("确认卸载插件", confirmation, "Remove / 卸载", "取消"))
                return new NasRemoveResult(false, 0);

            var modified = FindModifiedAssets(tracked);
            if (modified.Count > 0)
            {
                var preview = string.Join("\n", modified.Take(10).ToArray());
                if (modified.Count > 10)
                    preview += "\n…以及另外 " + (modified.Count - 10) + " 项";
                if (!EditorUtility.DisplayDialog(
                        "检测到安装后修改",
                        "以下插件文件在安装后发生了变化：\n\n" + preview +
                        "\n\n继续卸载会删除这些修改。要继续吗？",
                        "仍然卸载",
                        "取消"))
                    return new NasRemoveResult(false, 0);
            }

            var retainedDirectories = 0;
            var assetEditingStarted = false;
            try
            {
                AssetDatabase.StartAssetEditing();
                assetEditingStarted = true;

                foreach (var asset in tracked.Assets
                             .Where(item => !item.IsDirectory && !item.ExistedBefore)
                             .OrderByDescending(item => item.AssetPath.Length))
                {
                    DeleteAssetFile(asset.AssetPath);
                }

                foreach (var asset in tracked.Assets
                             .Where(item => item.IsDirectory)
                             .OrderByDescending(item => item.AssetPath.Length))
                {
                    var fullPath = NasPluginLibraryPaths.AssetPathToFullPath(asset.AssetPath);
                    if (asset.ExistedBefore)
                    {
                        RestoreOriginalMeta(fullPath, asset.OriginalMetaBase64);
                        continue;
                    }

                    if (!Directory.Exists(fullPath))
                    {
                        if (File.Exists(fullPath + ".meta"))
                            File.Delete(fullPath + ".meta");
                        continue;
                    }

                    if (Directory.GetFileSystemEntries(fullPath).Length == 0)
                    {
                        Directory.Delete(fullPath);
                        if (File.Exists(fullPath + ".meta"))
                            File.Delete(fullPath + ".meta");
                    }
                    else
                    {
                        retainedDirectories++;
                    }
                }

                state.Plugins.RemoveAll(item => string.Equals(item.PluginId, tracked.PluginId, StringComparison.OrdinalIgnoreCase));
                NasPluginLibraryStateStore.Save(state);
            }
            finally
            {
                if (assetEditingStarted)
                    AssetDatabase.StopAssetEditing();
            }

            AssetDatabase.Refresh();
            return new NasRemoveResult(true, retainedDirectories);
        }

        private static List<string> FindModifiedAssets(NasPluginInstallation installation)
        {
            var modified = new List<string>();
            foreach (var asset in installation.Assets.Where(item => !item.IsDirectory && !item.ExistedBefore))
            {
                var fullPath = NasPluginLibraryPaths.AssetPathToFullPath(asset.AssetPath);
                if (File.Exists(fullPath) &&
                    !string.IsNullOrWhiteSpace(asset.InstalledContentHash) &&
                    !string.Equals(asset.InstalledContentHash, NasPluginLibraryPaths.ComputeFileHash(fullPath), StringComparison.OrdinalIgnoreCase))
                {
                    modified.Add(asset.AssetPath);
                    continue;
                }

                var metaPath = fullPath + ".meta";
                if (File.Exists(metaPath) &&
                    !string.IsNullOrWhiteSpace(asset.InstalledMetaHash) &&
                    !string.Equals(asset.InstalledMetaHash, NasPluginLibraryPaths.ComputeFileHash(metaPath), StringComparison.OrdinalIgnoreCase))
                    modified.Add(asset.AssetPath + ".meta");
            }

            foreach (var asset in installation.Assets.Where(item => item.IsDirectory && item.ExistedBefore))
            {
                var metaPath = NasPluginLibraryPaths.AssetPathToFullPath(asset.AssetPath) + ".meta";
                if (!string.IsNullOrWhiteSpace(asset.InstalledMetaHash) &&
                    (!File.Exists(metaPath) ||
                     !string.Equals(asset.InstalledMetaHash, NasPluginLibraryPaths.ComputeFileHash(metaPath), StringComparison.OrdinalIgnoreCase)))
                    modified.Add(asset.AssetPath + ".meta");
            }

            return modified;
        }

        private static void DeleteAssetFile(string assetPath)
        {
            var fullPath = NasPluginLibraryPaths.AssetPathToFullPath(assetPath);
            if (File.Exists(fullPath))
                File.Delete(fullPath);
            if (File.Exists(fullPath + ".meta"))
                File.Delete(fullPath + ".meta");
        }

        private static void RestoreOriginalMeta(string fullPath, string originalMetaBase64)
        {
            var metaPath = fullPath + ".meta";
            if (string.IsNullOrWhiteSpace(originalMetaBase64))
            {
                if (File.Exists(metaPath))
                    File.Delete(metaPath);
                return;
            }

            File.WriteAllBytes(metaPath, Convert.FromBase64String(originalMetaBase64));
        }

        private static void OnPackageImportCompleted(string packageName)
        {
            EnsureInitialized();
            if (!HasPendingPackageImport)
                return;

            var pending = state.PendingPackage;
            var installation = new NasPluginInstallation
            {
                PluginId = pending.PluginId,
                Name = pending.Name,
                InstallKind = NasPluginKind.UnityPackage.ToString(),
                SourceFolderName = pending.SourceFolderName,
                PackageFileName = pending.PackageFileName,
                InstalledAtUtc = DateTime.UtcNow.ToString("o")
            };

            foreach (var snapshot in pending.Assets)
            {
                var fullPath = NasPluginLibraryPaths.AssetPathToFullPath(snapshot.AssetPath);
                var existsAsFile = File.Exists(fullPath);
                var existsAsDirectory = Directory.Exists(fullPath);
                if (!existsAsFile && !existsAsDirectory)
                    continue;

                var metaPath = fullPath + ".meta";
                if (snapshot.ExistedBefore && existsAsDirectory)
                {
                    var currentMetaBase64 = File.Exists(metaPath)
                        ? Convert.ToBase64String(File.ReadAllBytes(metaPath))
                        : string.Empty;
                    if (string.Equals(currentMetaBase64, snapshot.OriginalMetaBase64, StringComparison.Ordinal))
                        continue;
                }

                installation.Assets.Add(new NasInstalledAsset
                {
                    AssetPath = snapshot.AssetPath,
                    ExistedBefore = snapshot.ExistedBefore,
                    IsDirectory = existsAsDirectory,
                    OriginalMetaBase64 = snapshot.OriginalMetaBase64,
                    InstalledContentHash = existsAsFile ? NasPluginLibraryPaths.ComputeFileHash(fullPath) : string.Empty,
                    InstalledMetaHash = File.Exists(metaPath) ? NasPluginLibraryPaths.ComputeFileHash(metaPath) : string.Empty
                });
            }

            if (installation.Assets.Count == 0)
            {
                ClearPendingPackage();
                UnityTeamGitPluginLibraryWindow.NotifyLibraryChanged("UnityPackage 未导入任何资源：" + pending.Name);
                return;
            }

            state.Plugins.RemoveAll(item => string.Equals(item.PluginId, pending.PluginId, StringComparison.OrdinalIgnoreCase));
            state.Plugins.Add(installation);
            var stagedDirectory = Path.GetDirectoryName(pending.StagedPackagePath);
            state.PendingPackage = null;
            NasPluginLibraryStateStore.Save(state);
            NasPluginLibraryPaths.DeleteDirectoryBestEffort(stagedDirectory);
            UnityTeamGitPluginLibraryWindow.NotifyLibraryChanged("UnityPackage 导入完成：" + installation.Name);
        }

        private static void OnPackageImportCancelled(string packageName)
        {
            EnsureInitialized();
            if (!HasPendingPackageImport)
                return;
            var name = state.PendingPackage.Name;
            ClearPendingPackage();
            UnityTeamGitPluginLibraryWindow.NotifyLibraryChanged("已取消导入：" + name);
        }

        private static void OnPackageImportFailed(string packageName, string errorMessage)
        {
            EnsureInitialized();
            if (!HasPendingPackageImport)
                return;
            var name = state.PendingPackage.Name;
            try
            {
                RollbackPendingPackage();
            }
            finally
            {
                ClearPendingPackage();
            }

            AssetDatabase.Refresh();
            UnityTeamGitPluginLibraryWindow.NotifyLibraryChanged("导入失败：" + name + "。" + errorMessage);
        }

        private static void RollbackPendingPackage()
        {
            var pending = state.PendingPackage;
            if (pending == null)
                return;

            foreach (var asset in pending.Assets
                         .Where(item => !item.ExistedBefore)
                         .OrderByDescending(item => item.AssetPath.Length))
            {
                var fullPath = NasPluginLibraryPaths.AssetPathToFullPath(asset.AssetPath);
                if (File.Exists(fullPath))
                    File.Delete(fullPath);
                if (Directory.Exists(fullPath) && Directory.GetFileSystemEntries(fullPath).Length == 0)
                    Directory.Delete(fullPath);
                if (!Directory.Exists(fullPath) && File.Exists(fullPath + ".meta"))
                    File.Delete(fullPath + ".meta");
            }

            foreach (var asset in pending.Assets.Where(item => item.ExistedBefore))
                RestoreOriginalMeta(NasPluginLibraryPaths.AssetPathToFullPath(asset.AssetPath), asset.OriginalMetaBase64);
        }

        public static void ResetPendingPackageImport()
        {
            EnsureInitialized();
            if (!HasPendingPackageImport)
                return;
            RollbackPendingPackage();
            ClearPendingPackage();
            AssetDatabase.Refresh();
        }

        private static void ClearPendingPackage()
        {
            var pending = state.PendingPackage;
            if (pending == null)
                return;
            var stagedDirectory = Path.GetDirectoryName(pending.StagedPackagePath);
            state.PendingPackage = null;
            NasPluginLibraryStateStore.Save(state);
            NasPluginLibraryPaths.DeleteDirectoryBestEffort(stagedDirectory);
        }
    }

    internal sealed class NasCodeCopyPlan
    {
        public string RootName;
        public long TotalBytes;
        public readonly List<string> Directories = new List<string>();
        public readonly List<NasCopyEntry> Files = new List<NasCopyEntry>();

        public static NasCodeCopyPlan Build(NasPluginDescriptor plugin)
        {
            var plan = new NasCodeCopyPlan { RootName = Path.GetFileName(plugin.FolderPath) };
            plan.Directories.Add(plan.RootName);
            AddDirectory(plan, plugin.FolderPath, plan.RootName, plugin);

            var rootMeta = plugin.FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".meta";
            if (File.Exists(rootMeta))
                plan.AddFile(rootMeta, plan.RootName + ".meta");
            return plan;
        }

        public void Stage(string stagingRoot)
        {
            var payloadRoot = Path.Combine(stagingRoot, "Payload");
            Directory.CreateDirectory(payloadRoot);
            foreach (var directory in Directories)
                Directory.CreateDirectory(Path.Combine(payloadRoot, directory));

            for (var index = 0; index < Files.Count; index++)
            {
                var entry = Files[index];
                if (EditorUtility.DisplayCancelableProgressBar(
                        "下载 NAS 代码插件",
                        "读取 " + entry.RelativePath,
                        Files.Count == 0 ? 0f : (float)index / Files.Count))
                    throw new OperationCanceledException();
                var staged = Path.Combine(payloadRoot, entry.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(staged));
                File.Copy(entry.SourcePath, staged, true);
            }
        }

        public void Commit(string stagingRoot, string destinationRoot)
        {
            var payloadRoot = Path.Combine(stagingRoot, "Payload");
            var createdDirectories = new List<string>();
            var writtenFiles = new List<string>();
            var assetEditingStarted = false;
            try
            {
                AssetDatabase.StartAssetEditing();
                assetEditingStarted = true;
                NasPluginLibraryPaths.EnsureDirectoryWithTracking(destinationRoot, createdDirectories);
                foreach (var directory in Directories)
                    NasPluginLibraryPaths.EnsureDirectoryWithTracking(Path.Combine(destinationRoot, directory), createdDirectories);

                for (var index = 0; index < Files.Count; index++)
                {
                    var entry = Files[index];
                    if (EditorUtility.DisplayCancelableProgressBar(
                            "安装 NAS 代码插件",
                            "写入 " + entry.RelativePath,
                            Files.Count == 0 ? 1f : (float)index / Files.Count))
                        throw new OperationCanceledException();

                    var destination = Path.Combine(destinationRoot, entry.RelativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    File.Copy(Path.Combine(payloadRoot, entry.RelativePath), destination, false);
                    writtenFiles.Add(destination);
                }
            }
            catch
            {
                foreach (var file in writtenFiles.OrderByDescending(path => path.Length))
                {
                    if (File.Exists(file))
                        File.Delete(file);
                }
                foreach (var directory in createdDirectories.OrderByDescending(path => path.Length))
                {
                    if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0)
                        Directory.Delete(directory);
                }
                throw;
            }
            finally
            {
                if (assetEditingStarted)
                    AssetDatabase.StopAssetEditing();
            }
        }

        public NasPluginInstallation CreateInstallation(NasPluginDescriptor plugin, string destinationAssetRoot)
        {
            var installation = new NasPluginInstallation
            {
                PluginId = plugin.Id,
                Name = plugin.Name,
                InstallKind = NasPluginKind.CodeFolder.ToString(),
                SourceFolderName = Path.GetFileName(plugin.FolderPath),
                RootAssetPath = NasPluginLibraryPaths.CombineAssetPath(destinationAssetRoot, RootName),
                InstalledAtUtc = DateTime.UtcNow.ToString("o")
            };

            foreach (var directory in Directories)
            {
                var assetPath = NasPluginLibraryPaths.CombineAssetPath(destinationAssetRoot, directory);
                var fullPath = NasPluginLibraryPaths.AssetPathToFullPath(assetPath);
                installation.Assets.Add(new NasInstalledAsset
                {
                    AssetPath = assetPath,
                    ExistedBefore = false,
                    IsDirectory = true,
                    InstalledMetaHash = File.Exists(fullPath + ".meta") ? NasPluginLibraryPaths.ComputeFileHash(fullPath + ".meta") : string.Empty
                });
            }

            foreach (var relativePath in Files.Select(item => item.RelativePath)
                         .Where(path => !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)))
            {
                var assetPath = NasPluginLibraryPaths.CombineAssetPath(destinationAssetRoot, relativePath.Replace('\\', '/'));
                var fullPath = NasPluginLibraryPaths.AssetPathToFullPath(assetPath);
                installation.Assets.Add(new NasInstalledAsset
                {
                    AssetPath = assetPath,
                    ExistedBefore = false,
                    IsDirectory = false,
                    InstalledContentHash = File.Exists(fullPath) ? NasPluginLibraryPaths.ComputeFileHash(fullPath) : string.Empty,
                    InstalledMetaHash = File.Exists(fullPath + ".meta") ? NasPluginLibraryPaths.ComputeFileHash(fullPath + ".meta") : string.Empty
                });
            }

            return installation;
        }

        private static void AddDirectory(NasCodeCopyPlan plan, string sourceDirectory, string relativeDirectory, NasPluginDescriptor plugin)
        {
            NasPluginLibraryPaths.EnsureNotReparsePoint(sourceDirectory, "插件文件夹");
            foreach (var directory in Directory.GetDirectories(sourceDirectory))
            {
                NasPluginLibraryPaths.EnsureNotReparsePoint(directory, "插件子文件夹");
                var relative = Path.Combine(relativeDirectory, Path.GetFileName(directory));
                plan.Directories.Add(relative);
                AddDirectory(plan, directory, relative, plugin);
            }

            foreach (var file in Directory.GetFiles(sourceDirectory))
            {
                NasPluginLibraryPaths.EnsureNotReparsePoint(file, "插件文件");
                if (ShouldExcludeRootPackageFile(file, sourceDirectory, plugin))
                    continue;
                plan.AddFile(file, Path.Combine(relativeDirectory, Path.GetFileName(file)));
            }
        }

        private static bool ShouldExcludeRootPackageFile(string file, string sourceDirectory, NasPluginDescriptor plugin)
        {
            if (!NasPluginLibraryPaths.SamePath(sourceDirectory, plugin.FolderPath))
                return false;
            if (Path.GetExtension(file).Equals(".unitypackage", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                return false;
            var owner = file.Substring(0, file.Length - ".meta".Length);
            return Path.GetExtension(owner).Equals(".unitypackage", StringComparison.OrdinalIgnoreCase);
        }

        private void AddFile(string sourcePath, string relativePath)
        {
            Files.Add(new NasCopyEntry(sourcePath, relativePath));
            TotalBytes += new FileInfo(sourcePath).Length;
        }
    }

    internal sealed class NasCopyEntry
    {
        public readonly string SourcePath;
        public readonly string RelativePath;

        public NasCopyEntry(string sourcePath, string relativePath)
        {
            SourcePath = sourcePath;
            RelativePath = relativePath;
        }
    }

    internal static class UnityPackagePathReader
    {
        private const int TarBlockSize = 512;

        public static List<string> ReadAssetPaths(string packagePath)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var file = File.OpenRead(packagePath))
            using (var gzip = new GZipStream(file, CompressionMode.Decompress))
            {
                var header = new byte[TarBlockSize];
                while (ReadExactly(gzip, header, 0, header.Length) == header.Length)
                {
                    if (header.All(value => value == 0))
                        break;

                    var name = ReadNullTerminatedAscii(header, 0, 100);
                    var prefix = ReadNullTerminatedAscii(header, 345, 155);
                    if (!string.IsNullOrWhiteSpace(prefix))
                        name = prefix + "/" + name;
                    var size = ReadOctal(header, 124, 12);

                    if (name.EndsWith("/pathname", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(name, "pathname", StringComparison.OrdinalIgnoreCase))
                    {
                        if (size > 1024L * 1024L)
                            throw new InvalidDataException("UnityPackage pathname 条目异常过大。");
                        var data = new byte[(int)size];
                        if (ReadExactly(gzip, data, 0, data.Length) != data.Length)
                            throw new EndOfStreamException("UnityPackage 数据不完整。");
                        var path = NormalizePackageAssetPath(Encoding.UTF8.GetString(data).Trim('\0', '\r', '\n', ' '));
                        if (!string.IsNullOrWhiteSpace(path))
                            paths.Add(path);
                    }
                    else
                    {
                        Skip(gzip, size);
                    }

                    var padding = (TarBlockSize - size % TarBlockSize) % TarBlockSize;
                    Skip(gzip, padding);
                }
            }

            foreach (var path in paths.ToList())
            {
                var parent = path;
                while (parent.LastIndexOf('/') > 0)
                {
                    parent = parent.Substring(0, parent.LastIndexOf('/'));
                    paths.Add(parent);
                    if (string.Equals(parent, "Assets", StringComparison.OrdinalIgnoreCase))
                        break;
                }
            }

            return paths.OrderBy(path => path.Length).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string NormalizePackageAssetPath(string path)
        {
            var normalized = (path ?? string.Empty).Replace('\\', '/').Trim(new[] { '/' });
            if (!string.Equals(normalized, "Assets", StringComparison.OrdinalIgnoreCase) &&
                !normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                return string.Empty;
            var segments = normalized.Split(new[] { '/' });
            if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment == "." || segment == ".."))
                throw new InvalidDataException("UnityPackage 包含非法资源路径：" + path);
            return string.Join("/", segments);
        }

        private static int ReadExactly(Stream stream, byte[] buffer, int offset, int count)
        {
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, offset + total, count - total);
                if (read <= 0)
                    break;
                total += read;
            }
            return total;
        }

        private static void Skip(Stream stream, long count)
        {
            var buffer = new byte[8192];
            while (count > 0)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (read <= 0)
                    throw new EndOfStreamException("UnityPackage 数据不完整。");
                count -= read;
            }
        }

        private static string ReadNullTerminatedAscii(byte[] buffer, int offset, int count)
        {
            var length = 0;
            while (length < count && buffer[offset + length] != 0)
                length++;
            return Encoding.ASCII.GetString(buffer, offset, length);
        }

        private static long ReadOctal(byte[] buffer, int offset, int count)
        {
            var text = Encoding.ASCII.GetString(buffer, offset, count).Trim('\0', ' ');
            if (string.IsNullOrWhiteSpace(text))
                return 0L;
            try
            {
                return Convert.ToInt64(text, 8);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("UnityPackage TAR 条目大小无效。", exception);
            }
        }
    }

    internal static class NasPluginLibraryPaths
    {
        public static readonly StringComparer PathComparer = Environment.OSVersion.Platform == PlatformID.Win32NT
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        public static string ProjectRoot
        {
            get { return Path.GetFullPath(Path.Combine(Application.dataPath, "..")); }
        }

        public static string CreatePluginId(string folderName)
        {
            return Hash128.Compute((folderName ?? string.Empty).Trim().ToLowerInvariant()).ToString();
        }

        public static string NormalizeExistingDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("NAS 插件库路径不能为空。");
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
            var full = Path.GetFullPath(expanded);
            if (!Directory.Exists(full))
                throw new DirectoryNotFoundException("找不到或无权访问：" + full);
            return TrimEndingSeparators(full);
        }

        public static string NormalizeAssetDirectory(string assetPath)
        {
            var normalized = (assetPath ?? string.Empty).Trim().Replace('\\', '/').TrimEnd(new[] { '/' });
            if (string.IsNullOrWhiteSpace(normalized))
                normalized = "Assets";
            if (!string.Equals(normalized, "Assets", StringComparison.OrdinalIgnoreCase) &&
                !normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("代码安装目录必须是 Assets 或其子目录。");
            var segments = normalized.Split(new[] { '/' });
            if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment == "." || segment == ".."))
                throw new InvalidOperationException("代码安装目录包含无效路径段。");
            return string.Join("/", segments);
        }

        public static string AssetPathToFullPath(string assetPath)
        {
            var normalized = NormalizeAssetDirectoryOrFile(assetPath);
            var fullPath = Path.GetFullPath(Path.Combine(ProjectRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
            var assetsRoot = Path.GetFullPath(Application.dataPath);
            if (!IsSameOrChildPath(fullPath, assetsRoot))
                throw new InvalidOperationException("资源路径超出当前工程 Assets：" + assetPath);
            return fullPath;
        }

        public static string CombineAssetPath(string left, string right)
        {
            return left.TrimEnd(new[] { '/' }) + "/" + right.Replace('\\', '/').TrimStart(new[] { '/' });
        }

        public static void EnsureNotReparsePoint(string path, string label)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(label + "不能是符号链接或重解析点：" + path);
        }

        public static bool SamePath(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;
            return PathComparer.Equals(TrimEndingSeparators(Path.GetFullPath(left)), TrimEndingSeparators(Path.GetFullPath(right)));
        }

        public static string ComputeFileHash(string path)
        {
            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        public static void EnsureDirectoryWithTracking(string path, List<string> createdDirectories)
        {
            if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path))
                return;
            var missing = new Stack<string>();
            var cursor = path;
            while (!string.IsNullOrWhiteSpace(cursor) && !Directory.Exists(cursor))
            {
                missing.Push(cursor);
                cursor = Path.GetDirectoryName(cursor);
            }
            while (missing.Count > 0)
            {
                var directory = missing.Pop();
                Directory.CreateDirectory(directory);
                createdDirectories.Add(directory);
            }
        }

        public static void DeleteDirectoryBestEffort(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Unity Team Git 无法清理临时目录：" + path + Environment.NewLine + exception.Message);
            }
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024L)
                return bytes + " B";
            if (bytes < 1024L * 1024L)
                return (bytes / 1024d).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024L * 1024L)
                return (bytes / (1024d * 1024d)).ToString("0.0") + " MB";
            return (bytes / (1024d * 1024d * 1024d)).ToString("0.0") + " GB";
        }

        private static string NormalizeAssetDirectoryOrFile(string assetPath)
        {
            var normalized = (assetPath ?? string.Empty).Trim().Replace('\\', '/').Trim(new[] { '/' });
            if (!string.Equals(normalized, "Assets", StringComparison.OrdinalIgnoreCase) &&
                !normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("资源路径必须位于 Assets 内：" + assetPath);
            var segments = normalized.Split(new[] { '/' });
            if (segments.Any(segment => string.IsNullOrWhiteSpace(segment) || segment == "." || segment == ".."))
                throw new InvalidOperationException("资源路径包含无效路径段：" + assetPath);
            return string.Join("/", segments);
        }

        private static bool IsSameOrChildPath(string candidate, string root)
        {
            var candidateFull = TrimEndingSeparators(Path.GetFullPath(candidate));
            var rootFull = TrimEndingSeparators(Path.GetFullPath(root));
            if (PathComparer.Equals(candidateFull, rootFull))
                return true;
            return candidateFull.StartsWith(rootFull + Path.DirectorySeparatorChar, PathComparison()) ||
                   candidateFull.StartsWith(rootFull + Path.AltDirectorySeparatorChar, PathComparison());
        }

        private static StringComparison PathComparison()
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
        }

        private static string TrimEndingSeparators(string path)
        {
            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrWhiteSpace(root) && string.Equals(path, root, PathComparison()))
                return path;
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    internal static class NasPluginLibraryStateStore
    {
        private static string StatePath
        {
            get { return Path.Combine(NasPluginLibraryPaths.ProjectRoot, "ProjectSettings", "UnityTeamGitNasPluginLibrary.json"); }
        }

        public static NasPluginLibraryState Load()
        {
            try
            {
                if (!File.Exists(StatePath))
                    return new NasPluginLibraryState();
                var loaded = JsonUtility.FromJson<NasPluginLibraryState>(File.ReadAllText(StatePath, Encoding.UTF8));
                if (loaded == null)
                    return new NasPluginLibraryState();
                if (loaded.Plugins == null)
                    loaded.Plugins = new List<NasPluginInstallation>();
                return loaded;
            }
            catch (Exception exception)
            {
                Debug.LogError("Unity Team Git 无法读取 NAS 插件安装清单：" + exception);
                return new NasPluginLibraryState();
            }
        }

        public static void Save(NasPluginLibraryState value)
        {
            var path = StatePath;
            var temporary = path + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(temporary, JsonUtility.ToJson(value, true), new UTF8Encoding(false));
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temporary, path);
        }
    }

    [Serializable]
    internal sealed class NasPluginLibraryState
    {
        public int Version = 1;
        public List<NasPluginInstallation> Plugins = new List<NasPluginInstallation>();
        public NasPendingPackage PendingPackage;
    }

    [Serializable]
    internal sealed class NasPluginInstallation
    {
        public string PluginId;
        public string Name;
        public string InstallKind;
        public string SourceFolderName;
        public string PackageFileName;
        public string RootAssetPath;
        public string InstalledAtUtc;
        public List<NasInstalledAsset> Assets = new List<NasInstalledAsset>();
    }

    [Serializable]
    internal sealed class NasInstalledAsset
    {
        public string AssetPath;
        public bool ExistedBefore;
        public bool IsDirectory;
        public string OriginalMetaBase64;
        public string InstalledContentHash;
        public string InstalledMetaHash;
    }

    [Serializable]
    internal sealed class NasPendingPackage
    {
        public string PluginId;
        public string Name;
        public string SourceFolderName;
        public string PackageFileName;
        public string StagedPackagePath;
        public List<NasPendingAsset> Assets = new List<NasPendingAsset>();
    }

    [Serializable]
    internal sealed class NasPendingAsset
    {
        public string AssetPath;
        public bool ExistedBefore;
        public bool WasDirectory;
        public string OriginalMetaBase64;
    }

    internal struct NasRemoveResult
    {
        public readonly bool Removed;
        public readonly int RetainedDirectories;

        public NasRemoveResult(bool removed, int retainedDirectories)
        {
            Removed = removed;
            RetainedDirectories = retainedDirectories;
        }
    }
}
