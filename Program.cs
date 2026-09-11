using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MemoryOptimizer
{
    [Flags]
    public enum MemSwapScope
    {
        None = 0,
        EmptyWorkingSets = 1 << 0,
        FlushFileCache = 1 << 1,
        FlushModifiedList = 1 << 2,
        PurgeStandbyList = 1 << 3,
        PurgeLowPriorityStandbyList = 1 << 4,
        RegistryReconciliation = 1 << 5,
        CombinePhysicalMemory = 1 << 6,
        All = 0b1111111
    }

    /// <summary>
    /// 配置模型实体
    /// </summary>
    public class AppConfig
    {
        public bool SilentModeEnabled { get; set; } = false;
        public bool AutoTimerEnabled { get; set; } = false;
        public int TimerIntervalMinutes { get; set; } = 30;
        public bool AutoThresholdEnabled { get; set; } = false;
        public double ThresholdPercent { get; set; } = 80.0;
    }

    internal static class Program
    {
        private static NotifyIcon? _notifyIcon;
        private static ContextMenuStrip? _trayMenu;
        private static System.Windows.Forms.Timer? _uiUpdateTimer;

        // 当前应用配置实例
        private static AppConfig _config = new AppConfig();

        // 配置文件路径：用户文件夹根目录下的 .memory_optimizer.json
        private static readonly string ConfigFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 
            ".memory_optimizer.json"
        );

        private static DateTime _lastTimerCleanTime = DateTime.MinValue;
        private static string _lastCleanLog = "无";

        // 用于防重复清理的原子标记 (0 为空闲，1 为清理中)
        private static int _isCleaningState = 0;

        // 托盘菜单项引用
        private static ToolStripMenuItem? _itemMemoryInfo;
        private static ToolStripMenuItem? _itemLastLog;
        private static ToolStripMenuItem? _itemSilentToggle;
        private static ToolStripMenuItem? _itemTimerToggle;
        private static ToolStripMenuItem? _itemThresholdToggle;

        [STAThread]
        private static void Main()
        {
            ApplicationConfiguration.Initialize();

            // 纯 64 位系统架构校验
            if (!Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem)
            {
                MessageBox.Show("[致命错误] 本工具已完全重构为纯 64 位 (x64 Native) 架构，不支持 32 位系统运行！", 
                    "架构不兼容", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 1. 管理员权限校验
            EnsureAdministratorPrivileges();

            // 2. 启用系统提权 (SeProfileSingleProcessPrivilege / SeIncreaseQuotaPrivilege)
            AcquirePrivileges();

            // 3. 立刻读取配置（若文件不存在，则在此时同步生成并保存为隐藏文件）
            LoadOrCreateConfigImmediately();

            // 4. 构建托盘 UI (此时已能完美加载最新配置)
            InitializeTrayApp();

            // 5. 启动后台监控线程
            Thread monitorThread = new Thread(BackgroundMonitorWorker)
            {
                IsBackground = true,
                Name = "MemoryMonitorThread_x64"
            };
            monitorThread.Start();

            // 6. 进入 Windows 消息循环
            Application.Run();
        }

        #region 立刻同步读取与写入配置 (用户根目录 + 隐藏属性)

        /// <summary>
        /// 程序开启时立刻同步读取配置，若不存在则立刻创建并生成隐藏文件
        /// </summary>
        private static void LoadOrCreateConfigImmediately()
        {
            try
            {
                if (File.Exists(ConfigFilePath))
                {
                    string json = File.ReadAllText(ConfigFilePath);
                    var loadedConfig = JsonSerializer.Deserialize<AppConfig>(json);
                    if (loadedConfig != null)
                    {
                        _config = loadedConfig;
                        return;
                    }
                }
            }
            catch
            {
                // 解析失败时强制重新生成默认配置
            }

            // 文件不存在或解析失败：立即在开启时生成默认配置文件
            _config = new AppConfig();
            SaveConfig();
        }

        private static void SaveConfig()
        {
            try
            {
                // 1. 确保目标目录存在
                string? directory = Path.GetDirectoryName(ConfigFilePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // 2. 若文件已存在且已被设为隐藏，需先解除隐藏属性，否则 WriteAllText 会抛出拒绝访问异常
                if (File.Exists(ConfigFilePath))
                {
                    FileAttributes currentAttributes = File.GetAttributes(ConfigFilePath);
                    if ((currentAttributes & FileAttributes.Hidden) == FileAttributes.Hidden)
                    {
                        File.SetAttributes(ConfigFilePath, currentAttributes & ~FileAttributes.Hidden);
                    }
                }

                // 3. 序列化配置内容
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_config, options);

                // 4. 立刻写入配置文件
                File.WriteAllText(ConfigFilePath, json);

                // 5. 将配置文件设置为系统隐藏文件
                File.SetAttributes(ConfigFilePath, File.GetAttributes(ConfigFilePath) | FileAttributes.Hidden);
            }
            catch (Exception ex)
            {
                _lastCleanLog = $"保存配置失败: {ex.Message}";
            }
        }

        #endregion

        #region 管理员权限校验

        private static void EnsureAdministratorPrivileges()
        {
            if (!IsAdministrator())
            {
                try
                {
                    var exeName = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exeName))
                    {
                        var startInfo = new ProcessStartInfo
                        {
                            FileName = exeName,
                            UseShellExecute = true,
                            Verb = "runas"
                        };

                        Process.Start(startInfo);
                    }
                }
                catch
                {
                    MessageBox.Show("提权失败或被取消！此工具需要管理员权限才能对系统内存进行深度清理。", 
                        "权限不足", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }

                Environment.Exit(0);
            }
        }

        private static bool IsAdministrator()
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        #endregion

        #region 托盘 UI & 动态条状图图标绘制

        private static void InitializeTrayApp()
        {
            _trayMenu = new ContextMenuStrip();

            // 1. 内存状态与日志展示
            _itemMemoryInfo = new ToolStripMenuItem("内存占用: 读取中...") { Enabled = false };
            _itemLastLog = new ToolStripMenuItem("上次清理: 无") { Enabled = false };

            // 2. 立即清理按钮
            var itemCleanNow = new ToolStripMenuItem("⚡ 立即深度清理内存", null, (s, e) => TriggerAsyncClean("手动触发"));

            // 3. 静默模式切换开关
            _itemSilentToggle = new ToolStripMenuItem("🔕 静默模式 (关闭弹窗通知)", null, ToggleSilentMode);

            // 4. 定时清理设置子菜单
            _itemTimerToggle = new ToolStripMenuItem("定时自动清理: 已关闭", null, ToggleTimerClean);
            var itemSetTimer = new ToolStripMenuItem("设置定时清理间隔...", null, SetTimerInterval);
            var menuTimerGroup = new ToolStripMenuItem("⏰ 定时清理设置");
            menuTimerGroup.DropDownItems.Add(_itemTimerToggle);
            menuTimerGroup.DropDownItems.Add(itemSetTimer);

            // 5. 阈值清理设置子菜单
            _itemThresholdToggle = new ToolStripMenuItem("阈值自动清理: 已关闭", null, ToggleThresholdClean);
            var itemSetThreshold = new ToolStripMenuItem("设置触发阀值百分比...", null, SetThresholdPercent);
            var menuThresholdGroup = new ToolStripMenuItem("📊 阈值清理设置");
            menuThresholdGroup.DropDownItems.Add(_itemThresholdToggle);
            menuThresholdGroup.DropDownItems.Add(itemSetThreshold);

            // 6. 打开配置文件存储目录
            var itemOpenConfigFolder = new ToolStripMenuItem("📁 打开配置保存路径", null, (s, e) => OpenConfigFolder());

            // 7. 退出程序
            var itemExit = new ToolStripMenuItem("❌ 退出程序", null, (s, e) => ExitApplication());

            // 组合右键菜单
            _trayMenu.Items.Add(_itemMemoryInfo);
            _trayMenu.Items.Add(_itemLastLog);
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(itemCleanNow);
            _trayMenu.Items.Add(_itemSilentToggle);
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(menuTimerGroup);
            _trayMenu.Items.Add(menuThresholdGroup);
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(itemOpenConfigFolder);
            _trayMenu.Items.Add(itemExit);

            // 创建托盘图标
            _notifyIcon = new NotifyIcon
            {
                Text = "Windows 内存深度优化工具 (x64)",
                ContextMenuStrip = _trayMenu,
                Visible = true
            };

            // 双击托盘图标：异步触发清理
            _notifyIcon.DoubleClick += (s, e) => TriggerAsyncClean("双击托盘");

            // UI 刷新定时器 (每秒更新一次图标条状图与右键菜单文本)
            _uiUpdateTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _uiUpdateTimer.Tick += (s, e) => UpdateTrayUI();
            _uiUpdateTimer.Start();

            // 立即刷新一次 UI 状态
            UpdateTrayUI();
            // 启动通知：传入 forceShow: true，确保即使开启了静默模式，启动时依然弹窗
	    ShowNotification("内存优化工具已启动", "已缩放到系统托盘静默运行，双击图标可直接清理内存。", 3000, ToolTipIcon.Info, forceShow: true);
        }

        private static void ShowNotification(string title, string text, int timeout = 2000, ToolTipIcon icon = ToolTipIcon.Info, bool forceShow = false)
        {
            if ((forceShow || !_config.SilentModeEnabled) && _notifyIcon != null)
            {
                _notifyIcon.ShowBalloonTip(timeout, title, text, icon);
            }
        }

        private static void UpdateTrayUI()
        {
            if (_notifyIcon == null) return;

            GetMemoryUsage(out ulong totalPhys, out ulong availPhys, out double usedPercent);

            bool isCleaning = Interlocked.CompareExchange(ref _isCleaningState, 0, 0) == 1;

            // 1. 动态生成带有“内存占用条状图”的 Icon
            Icon oldIcon = _notifyIcon.Icon!;
            _notifyIcon.Icon = CreateMemoryBarIcon((int)Math.Round(usedPercent), isCleaning);
            if (oldIcon != null)
            {
                DestroyIcon(oldIcon.Handle);
                oldIcon.Dispose();
            }

            // 2. 更新悬浮提示
            string statusStr = isCleaning ? "正在深度清理中..." : $"内存占用: {usedPercent:F1}%";
            string silentStr = _config.SilentModeEnabled ? " [静默]" : "";
            _notifyIcon.Text = $"内存深度优化工具 (x64){silentStr}\n{statusStr}";

            // 3. 更新右键菜单项
            if (_itemMemoryInfo != null)
            {
                ulong usedBytes = totalPhys - availPhys;
                _itemMemoryInfo.Text = $"内存占用: {usedPercent:F1}% ({FormatBytes(usedBytes)} / {FormatBytes(totalPhys)})";
            }

            if (_itemLastLog != null)
            {
                _itemLastLog.Text = $"最近记录: {_lastCleanLog}";
            }

            if (_itemSilentToggle != null)
            {
                _itemSilentToggle.Checked = _config.SilentModeEnabled;
            }

            if (_itemTimerToggle != null)
            {
                _itemTimerToggle.Text = _config.AutoTimerEnabled ? $"定时清理: 已开启 ({_config.TimerIntervalMinutes}分钟)" : "定时清理: 已关闭";
                _itemTimerToggle.Checked = _config.AutoTimerEnabled;
            }

            if (_itemThresholdToggle != null)
            {
                _itemThresholdToggle.Text = _config.AutoThresholdEnabled ? $"阈值清理: 已开启 (>={_config.ThresholdPercent:F0}%)" : "阈值清理: 已关闭";
                _itemThresholdToggle.Checked = _config.AutoThresholdEnabled;
            }
        }

        private static Icon CreateMemoryBarIcon(int percent, bool isCleaning)
        {
            percent = Math.Clamp(percent, 0, 100);

            using Bitmap bmp = new Bitmap(16, 16);
            using Graphics g = Graphics.FromImage(bmp);

            g.Clear(Color.Transparent);

            Color barColor = Color.LimeGreen;
            if (percent >= 85) barColor = Color.Red;
            else if (percent >= 70) barColor = Color.Orange;

            if (isCleaning) barColor = Color.DeepSkyBlue;

            using (Pen borderPen = new Pen(Color.FromArgb(180, 255, 255, 255), 1))
            {
                g.DrawRectangle(borderPen, 1, 1, 13, 13);
            }

            int fillHeight = (int)Math.Round((percent / 100.0) * 11);
            if (fillHeight > 0)
            {
                using Brush brush = new SolidBrush(barColor);
                g.FillRectangle(brush, 3, 13 - fillHeight, 10, fillHeight);
            }

            IntPtr hIcon = bmp.GetHicon();
            return Icon.FromHandle(hIcon);
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        private static void OpenConfigFolder()
        {
            try
            {
                string userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                Process.Start(new ProcessStartInfo
                {
                    FileName = userProfilePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开用户文件夹: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void ExitApplication()
        {
            _uiUpdateTimer?.Stop();
            _notifyIcon?.Dispose();
            Application.Exit();
        }

        #endregion

        #region 异步清理触发器

        private static void TriggerAsyncClean(string triggerSource)
        {
            if (Interlocked.CompareExchange(ref _isCleaningState, 1, 0) != 0)
            {
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    ulong memoryBefore = GetAvailablePhysicalMemoryBytes();

                    ExecuteMemorySwap(MemSwapScope.All);

                    ulong memoryAfter = GetAvailablePhysicalMemoryBytes();
                    long freedMemory = (long)memoryAfter - (long)memoryBefore;

                    string freedStr = freedMemory > 0 ? FormatBytes((ulong)freedMemory) : "0 B";
                    _lastCleanLog = $"[{DateTime.Now:HH:mm:ss}] {triggerSource} -> 释放 {freedStr}";

                    ShowNotification("内存清理完成", $"触发源: {triggerSource}\n本次成功释放: {freedStr}");
                }
                catch (Exception ex)
                {
                    _lastCleanLog = $"[{DateTime.Now:HH:mm:ss}] 清理异常: {ex.Message}";
                }
                finally
                {
                    Interlocked.Exchange(ref _isCleaningState, 0);
                }
            });
        }

        #endregion

        #region 交互配置逻辑 (修改后立刻保存)

        private static void ToggleSilentMode(object? sender, EventArgs e)
        {
            _config.SilentModeEnabled = !_config.SilentModeEnabled;
            SaveConfig();
            UpdateTrayUI();
        }

        private static void ToggleTimerClean(object? sender, EventArgs e)
        {
            _config.AutoTimerEnabled = !_config.AutoTimerEnabled;
            if (_config.AutoTimerEnabled) _lastTimerCleanTime = DateTime.Now;
            SaveConfig();
            UpdateTrayUI();
        }

        private static void SetTimerInterval(object? sender, EventArgs e)
        {
            string input = Microsoft.VisualBasic.Interaction.InputBox("请输入定时清理的时间间隔（分钟）:", "设置定时清理间隔", _config.TimerIntervalMinutes.ToString());
            if (int.TryParse(input, out int min) && min > 0)
            {
                _config.TimerIntervalMinutes = min;
                _config.AutoTimerEnabled = true;
                _lastTimerCleanTime = DateTime.Now;
                SaveConfig();
                UpdateTrayUI();
            }
        }

        private static void ToggleThresholdClean(object? sender, EventArgs e)
        {
            _config.AutoThresholdEnabled = !_config.AutoThresholdEnabled;
            SaveConfig();
            UpdateTrayUI();
        }

        private static void SetThresholdPercent(object? sender, EventArgs e)
        {
            string input = Microsoft.VisualBasic.Interaction.InputBox("请输入触发自动清理的内存占比百分比阈值 (10 - 99):", "设置占比阈值", _config.ThresholdPercent.ToString("F0"));
            if (double.TryParse(input, out double pct) && pct >= 10 && pct <= 99)
            {
                _config.ThresholdPercent = pct;
                _config.AutoThresholdEnabled = true;
                SaveConfig();
                UpdateTrayUI();
            }
        }

        #endregion

        #region 后台定时与阈值监控线程

        private static void BackgroundMonitorWorker()
        {
            _lastTimerCleanTime = DateTime.Now;

            while (true)
            {
                try
                {
                    Thread.Sleep(3000);

                    GetMemoryUsage(out _, out _, out double usedPercent);

                    bool shouldCleanByTimer = false;
                    bool shouldCleanByThreshold = false;

                    if (_config.AutoTimerEnabled)
                    {
                        if ((DateTime.Now - _lastTimerCleanTime).TotalMinutes >= _config.TimerIntervalMinutes)
                        {
                            shouldCleanByTimer = true;
                        }
                    }

                    if (_config.AutoThresholdEnabled)
                    {
                        if (usedPercent >= _config.ThresholdPercent)
                        {
                            shouldCleanByThreshold = true;
                        }
                    }

                    if (shouldCleanByTimer || shouldCleanByThreshold)
                    {
                        string reason = shouldCleanByTimer && shouldCleanByThreshold
                            ? "定时+阈值"
                            : (shouldCleanByTimer ? "定时" : $"阈值({usedPercent:F0}%)");

                        TriggerAsyncClean($"自动({reason})");

                        _lastTimerCleanTime = DateTime.Now;

                        Thread.Sleep(20000);
                    }
                }
                catch
                {
                    // 保护逻辑
                }
            }
        }

        #endregion

        #region 核心内存深度清理 (Native API)

        private static void ExecuteMemorySwap(MemSwapScope scope = MemSwapScope.All)
        {
            if (scope.HasFlag(MemSwapScope.EmptyWorkingSets)) EmptyWorkingSets();
            if (scope.HasFlag(MemSwapScope.FlushFileCache)) FlushFileCache();
            if (scope.HasFlag(MemSwapScope.FlushModifiedList)) FlushModifiedList();
            if (scope.HasFlag(MemSwapScope.PurgeStandbyList)) PurgeStandbyList();
            if (scope.HasFlag(MemSwapScope.PurgeLowPriorityStandbyList)) PurgeLowPriorityStandbyList();
            if (scope.HasFlag(MemSwapScope.RegistryReconciliation)) RegistryReconciliation();
            if (scope.HasFlag(MemSwapScope.CombinePhysicalMemory)) CombinePhysicalMemory();
        }

        private static void _ExecuteMemoryListOperation(int infoValue)
        {
            GCHandle handle = GCHandle.Alloc(infoValue, GCHandleType.Pinned);
            try
            {
                NtSetSystemInformation(
                    SYSTEM_INFORMATION_CLASS.SystemMemoryListInformation,
                    handle.AddrOfPinnedObject(),
                    sizeof(int));
            }
            finally
            {
                if (handle.IsAllocated) handle.Free();
            }
        }

        private static void _ExecuteStructureOperation<T>(T structure, SYSTEM_INFORMATION_CLASS infoClass) where T : struct
        {
            GCHandle handle = GCHandle.Alloc(structure, GCHandleType.Pinned);
            try
            {
                NtSetSystemInformation(
                    infoClass,
                    handle.AddrOfPinnedObject(),
                    (uint)Marshal.SizeOf(structure));
            }
            finally
            {
                if (handle.IsAllocated) handle.Free();
            }
        }

        public static void EmptyWorkingSets() => _ExecuteMemoryListOperation(2);

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct SYSTEM_FILECACHE_INFORMATION_64
        {
            public ulong CurrentSize;
            public ulong PeakSize;
            public ulong PageFaultCount;
            public ulong MinimumWorkingSet;
            public ulong MaximumWorkingSet;
            public ulong CurrentSizeIncludingTransitionInPages;
            public ulong PeakSizeIncludingTransitionInPages;
            public ulong TransitionRePurposeCount;
            public ulong Flags;
        }

        public static void FlushFileCache()
        {
            var scfi = new SYSTEM_FILECACHE_INFORMATION_64
            {
                MaximumWorkingSet = ulong.MaxValue,
                MinimumWorkingSet = ulong.MaxValue
            };
            _ExecuteStructureOperation(scfi, SYSTEM_INFORMATION_CLASS.SystemFileCacheInformationEx);
        }

        public static void FlushModifiedList() => _ExecuteMemoryListOperation(3);

        public static void PurgeStandbyList() => _ExecuteMemoryListOperation(4);

        public static void PurgeLowPriorityStandbyList() => _ExecuteMemoryListOperation(5);

        public static void RegistryReconciliation() =>
            NtSetSystemInformation(
                SYSTEM_INFORMATION_CLASS.SystemRegistryReconciliationInformation,
                IntPtr.Zero,
                0);

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct MEMORY_COMBINE_INFORMATION_EX_64
        {
            public long Handle;
            public ulong PagesCombined;
            public ulong Flags;
        }

        public static void CombinePhysicalMemory()
        {
            var combineInfoEx = new MEMORY_COMBINE_INFORMATION_EX_64();
            _ExecuteStructureOperation(combineInfoEx, SYSTEM_INFORMATION_CLASS.SystemCombinePhysicalMemoryInformation);
        }

        #endregion

        #region 原生 64 位 Windows Native API (ntdll & kernel32)

        private enum SYSTEM_INFORMATION_CLASS
        {
            SystemMemoryListInformation = 80,
            SystemFileCacheInformationEx = 81,
            SystemRegistryReconciliationInformation = 134,
            SystemCombinePhysicalMemoryInformation = 130
        }

        [DllImport("ntdll.dll", SetLastError = true)]
        private static extern int NtSetSystemInformation(SYSTEM_INFORMATION_CLASS SystemInformationClass, IntPtr SystemInformation, uint SystemInformationLength);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto, Pack = 8)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            public MEMORYSTATUSEX()
            {
                dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        private static ulong GetAvailablePhysicalMemoryBytes()
        {
            var statEx = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(statEx))
            {
                return statEx.ullAvailPhys;
            }
            return 0;
        }

        private static void GetMemoryUsage(out ulong totalPhys, out ulong availPhys, out double usedPercent)
        {
            var statEx = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(statEx))
            {
                totalPhys = statEx.ullTotalPhys;
                availPhys = statEx.ullAvailPhys;
                usedPercent = statEx.dwMemoryLoad;
            }
            else
            {
                totalPhys = 0;
                availPhys = 0;
                usedPercent = 0;
            }
        }

        #endregion

        #region 原生 64 位权限控制 API (advapi32)

        private static void AcquirePrivileges()
        {
            SetPrivilege("SeProfileSingleProcessPrivilege", true);
            SetPrivilege("SeIncreaseQuotaPrivilege", true);
        }

        private static void SetPrivilege(string privilege, bool enable)
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out long hToken))
                return;

            try
            {
                if (LookupPrivilegeValue(null, privilege, out LUID luid))
                {
                    TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES
                    {
                        PrivilegeCount = 1,
                        Privileges = new LUID_AND_ATTRIBUTES[1]
                    };
                    tp.Privileges[0].Luid = luid;
                    tp.Privileges[0].Attributes = enable ? SE_PRIVILEGE_ENABLED : 0;

                    AdjustTokenPrivileges(hToken, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                }
            }
            finally
            {
                CloseHandle(hToken);
            }
        }

        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED = 0x00000002;

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct LUID_AND_ATTRIBUTES
        {
            public LUID Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
            public LUID_AND_ATTRIBUTES[] Privileges;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool OpenProcessToken(long ProcessHandle, uint DesiredAccess, out long TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out LUID lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(long TokenHandle, bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern long GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(long hObject);

        #endregion

        #region 工具方法

        private static string FormatBytes(ulong bytes)
        {
            string[] Suffix = { "B", "KB", "MB", "GB", "TB" };
            int i;
            double dblS32 = bytes;
            for (i = 0; i < Suffix.Length && bytes >= 1024; i++, bytes /= 1024)
            {
                dblS32 = bytes / 1024.0;
            }
            return $"{dblS32:0.00} {Suffix[i]}";
        }

        #endregion
    }
}
