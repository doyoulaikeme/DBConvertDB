using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using DbConvert;
using DbConvert.Providers;

namespace DbConvert.Gui
{
    public class KindOption
    {
        public string Key { get; set; }
        public string Display { get; set; }
        public override string ToString() => Display;
    }

    public class TableItem
    {
        public bool IsSelected { get; set; } = true;
        public string SourceName { get; set; }
        public string TargetName { get; set; }
        public string RowEstimate { get; set; }
        public int ColumnCount { get; set; }
        public string Note { get; set; }
        public TableDef Table { get; set; }
    }

    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<TableItem> _items = new ObservableCollection<TableItem>();
        private List<TableDef> _tables = new List<TableDef>();
        private bool _busy;
        private bool _swappingEndpoints;

        public MainWindow()
        {
            InitializeComponent();
            BuiltinProviders.Register();
            DgTables.ItemsSource = _items;

            // 这些按钮在 XAML 里没有挂 Click，必须在代码里接上，否则点了没反应
            BtnLoad.Click += BtnLoad_Click;
            BtnScript.Click += BtnScript_Click;
            BtnPreview.Click += BtnPreview_Click;
            BtnMigrate.Click += BtnMigrate_Click;
            BtnVerify.Click += BtnVerify_Click;
            BtnOpenOut.Click += BtnOpenOut_Click;

            // 自绘边框的窗口不能超出工作区，否则顶部标题栏会被屏幕截掉
            MaxHeight = SystemParameters.WorkArea.Height;
            MaxWidth = SystemParameters.WorkArea.Width;
            if (Height > MaxHeight) Height = MaxHeight;
            if (Width > MaxWidth) Width = MaxWidth;

            var kinds = new List<KindOption>();
            foreach (string k in Dialects.Kinds)
            {
                kinds.Add(new KindOption { Key = k, Display = Dialects.Get(k).Display + "  (" + k + ")" });
            }
            SrcKind.ItemsSource = kinds;
            DstKind.ItemsSource = kinds;
            SrcKind.SelectedIndex = 0;
            DstKind.SelectedItem = kinds.FirstOrDefault(x => x.Key == "dm") ?? kinds[0];
            SrcPort.Text = ConnectionStrings.DefaultPort(((KindOption)SrcKind.SelectedItem).Key).ToString();
            DstPort.Text = ConnectionStrings.DefaultPort(((KindOption)DstKind.SelectedItem).Key).ToString();
            ApplyDbLabel(SrcDbLabel, ((KindOption)SrcKind.SelectedItem).Key);
            ApplyDbLabel(DstDbLabel, ((KindOption)DstKind.SelectedItem).Key);

            TxtOut.Text = "output";
            ApplyChromeState();
            AppendLog("就绪。先填连接信息 → 测试连接 → 读取源表结构 → 开始迁移。");
            AppendLog("达梦/金仓需要先把 DmProvider.dll / Kdbndp.dll 放到程序目录或 providers 子目录。");
        }

        // ------------------------------------------------------------------
        // 自绘窗口边框
        // ------------------------------------------------------------------

        private void BtnMin_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void BtnMax_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            ApplyChromeState();
        }

        private void ApplyChromeState()
        {
            bool maximized = WindowState == WindowState.Maximized;
            if (IcoMax != null) IcoMax.Text = maximized ? "\uE923" : "\uE922";
            if (RootBorder != null)
            {
                RootBorder.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(12);
                RootBorder.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
                // 最大化时 WindowChrome 的“假边框”会把内容整体下移 8px，
                // 这里让根部让出 8px，避免日志卡片底部被屏幕边缘裁掉。
                RootBorder.Margin = maximized ? new Thickness(0, 0, 0, 8) : new Thickness(0);
            }
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
            if (chrome != null) chrome.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(12);
        }

        /// <summary>自绘边框的窗口需要自己校正最大化位置，否则会向外溢出屏幕边缘。</summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var helper = new WindowInteropHelper(this);
            HwndSource source = HwndSource.FromHwnd(helper.Handle);
            source?.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_GETMINMAXINFO = 0x0024;
            if (msg == WM_GETMINMAXINFO)
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    RECT work = info.rcWork;
                    RECT mon = info.rcMonitor;
                    // WindowChrome 会给最大化窗口留“假边框”，实测本机正好等于工作区尺寸，
                    // 不再额外加 16px，避免最大化后右侧/底部露出桌面。
                    mmi.ptMaxPosition.x = work.left - mon.left;
                    mmi.ptMaxPosition.y = work.top - mon.top;
                    mmi.ptMaxSize.x = work.right - work.left;
                    mmi.ptMaxSize.y = work.bottom - work.top;
                    Marshal.StructureToPtr(mmi, lParam, true);
                    handled = true;
                }
            }
            return IntPtr.Zero;
        }

        // ------------------------------------------------------------------
        // 连接
        // ------------------------------------------------------------------

        private EndpointConfig BuildEndpoint(ComboBox kind, TextBox host, TextBox port, TextBox db, TextBox user, PasswordBox pwd, TextBox conn)
        {
            var ep = new EndpointConfig
            {
                Kind = ((KindOption)kind.SelectedItem).Key,
                Host = host.Text.Trim(),
                Database = db.Text.Trim(),
                User = user.Text.Trim(),
                Password = pwd.Password,
                ConnectionString = conn.Text.Trim()
            };
            if (int.TryParse(port.Text.Trim(), out int p)) ep.Port = p;
            else ep.Port = ConnectionStrings.DefaultPort(ep.Kind);
            return ep;
        }

        private EndpointConfig BuildSource() => BuildEndpoint(SrcKind, SrcHost, SrcPort, SrcDb, SrcUser, SrcPwd, SrcConn);

        private EndpointConfig BuildTarget() => BuildEndpoint(DstKind, DstHost, DstPort, DstDb, DstUser, DstPwd, DstConn);

        private async void BtnTestSrc_Click(object sender, RoutedEventArgs e)
        {
            await TestAsync(BuildSource());
        }

        private async void BtnTestDst_Click(object sender, RoutedEventArgs e)
        {
            await TestAsync(BuildTarget());
        }

        private async Task TestAsync(EndpointConfig ep)
        {
            if (_busy) return;
            RunBusy(true, "正在连接...");
            try
            {
                await Task.Run(() =>
                {
                    using (var session = SchemaSession.Open(ep, AppendLog))
                    {
                        AppendLog("连接成功：" + session.Dialect.Display + " / " + ProviderHost.ServerInfo(session.Connection) + "，schema = " + session.Schema);
                    }
                });
                SetStatus("连接成功");
            }
            catch (Exception ex)
            {
                AppendLog("连接失败：" + ex.Message);
                SetStatus("连接失败");
            }
            finally
            {
                RunBusy(false, null);
            }
        }

        private void SrcKind_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_swappingEndpoints) return;
            if (SrcPort == null || SrcKind.SelectedItem == null) return;
            SrcPort.Text = ConnectionStrings.DefaultPort(((KindOption)SrcKind.SelectedItem).Key).ToString();
            ApplySuggestedDb(SrcDb, ((KindOption)SrcKind.SelectedItem).Key);
            ApplyDbLabel(SrcDbLabel, ((KindOption)SrcKind.SelectedItem).Key);
            InvalidateLoadedTables("源库类型已变，请重新读取源表结构");
        }

        private void DstKind_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_swappingEndpoints) return;
            if (DstPort == null || DstKind.SelectedItem == null) return;
            DstPort.Text = ConnectionStrings.DefaultPort(((KindOption)DstKind.SelectedItem).Key).ToString();
            ApplySuggestedDb(DstDb, ((KindOption)DstKind.SelectedItem).Key);
            ApplyDbLabel(DstDbLabel, ((KindOption)DstKind.SelectedItem).Key);
        }

        /// <summary>这一个输入框在不同库含义不同，标签跟着类型变。</summary>
        private void ApplyDbLabel(TextBlock label, string kind)
        {
            if (label == null) return;
            switch (Dialects.NormalizeKind(kind))
            {
                case "oracle":
                    label.Text = "服务名";
                    label.ToolTip = "Oracle 填 SERVICE_NAME，如 ORCL / ORCLPDB1";
                    break;
                case "dm":
                    label.Text = "Schema";
                    label.ToolTip = "达梦填 schema（模式名），一般与登录用户同名，可留空";
                    break;
                case "sqlite":
                    label.Text = "文件路径";
                    label.ToolTip = "SQLite 数据库文件路径，如 D:/data/app.db";
                    break;
                default:
                    label.Text = "数据库";
                    label.ToolTip = "填要连接的数据库名（MySQL/PostgreSQL/SQL Server 等）";
                    break;
            }
        }

        private void SwapEndpoints_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _swappingEndpoints = true;
            try
            {
                int srcKind = SrcKind.SelectedIndex;
                int dstKind = DstKind.SelectedIndex;
                SrcKind.SelectedIndex = dstKind;
                DstKind.SelectedIndex = srcKind;

                SwapText(SrcHost, DstHost);
                SwapText(SrcPort, DstPort);
                SwapText(SrcDb, DstDb);
                SwapText(SrcUser, DstUser);
                SwapText(SrcConn, DstConn);

                string srcPwd = SrcPwd.Password;
                SrcPwd.Password = DstPwd.Password;
                DstPwd.Password = srcPwd;
            }
            finally
            {
                _swappingEndpoints = false;
            }

            // 端口跟着类型走：某一侧端口为空时按新类型补默认端口
            if (string.IsNullOrWhiteSpace(SrcPort.Text) && SrcKind.SelectedItem != null)
                SrcPort.Text = ConnectionStrings.DefaultPort(((KindOption)SrcKind.SelectedItem).Key).ToString();
            if (string.IsNullOrWhiteSpace(DstPort.Text) && DstKind.SelectedItem != null)
                DstPort.Text = ConnectionStrings.DefaultPort(((KindOption)DstKind.SelectedItem).Key).ToString();
            if (SrcKind.SelectedItem != null) ApplyDbLabel(SrcDbLabel, ((KindOption)SrcKind.SelectedItem).Key);
            if (DstKind.SelectedItem != null) ApplyDbLabel(DstDbLabel, ((KindOption)DstKind.SelectedItem).Key);

            // 交换后原来读到的表结构属于旧源库，必须作废，避免拿旧表名去新源库迁移
            bool hadLoaded = _items.Count > 0;
            if (hadLoaded)
            {
                _items.Clear();
                _tables = new List<TableDef>();
            }
            SetStatus(hadLoaded ? "已交换，请重新读取源表结构" : "已交换源库与目标库");
        }

        private static void SwapText(TextBox a, TextBox b)
        {
            string t = a.Text;
            a.Text = b.Text;
            b.Text = t;
        }

        /// <summary>连接参数变了以后，已读到的表结构就不再属于当前源库。</summary>
        private void InvalidateLoadedTables(string status)
        {
            if (_items.Count == 0) return;
            _items.Clear();
            _tables = new List<TableDef>();
            SetStatus(status);
        }

        private void ApplySuggestedDb(TextBox db, string kind)
        {
            if (!string.IsNullOrWhiteSpace(db.Text)) return;
            if (kind == "sqlite") db.Text = "D:/mydata/target.db";
        }

        // ------------------------------------------------------------------
        // 读取表结构
        // ------------------------------------------------------------------

        private async void BtnLoad_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            RunBusy(true, "正在读取源库结构...");
            try
            {
                EndpointConfig src = BuildSource();
                EndpointConfig dst = BuildTarget();
                string targetKind = dst.Kind;
                string targetSchema = "";
                OptionsConfig options = BuildOptions(); // 必须在 UI 线程读控件，不能进后台线程

                var result = await Task.Run(() =>
                {
                    var cfg = new MigrationConfig
                    {
                        Source = src,
                        Target = dst,
                        Options = options
                    };
                    Dialect targetDialect = Dialects.Get(targetKind);
                    using (var session = SchemaSession.Open(src, AppendLog))
                    {
                        var tables = session.LoadTables(cfg.Options, cfg.Tables, AppendLog, null, targetSchema, targetDialect);
                        int viewCount = cfg.Options.IncludeViews ? session.Reader.ReadViews(session.Connection, session.Schema, null).Count : 0;
                        int routineCount = cfg.Options.IncludeRoutines ? session.Reader.ReadRoutines(session.Connection, session.Schema, null).Count : 0;
                        int triggerCount = cfg.Options.IncludeTriggers ? session.Reader.ReadTriggers(session.Connection, session.Schema, null).Count : 0;
                        return (tables, viewCount, routineCount, triggerCount);
                    }
                });

                _tables = result.tables;
                _items.Clear();
                foreach (TableDef t in result.tables)
                {
                    _items.Add(new TableItem
                    {
                        SourceName = t.QualifiedName,
                        TargetName = string.IsNullOrEmpty(t.TargetSchema) ? t.TargetName : t.TargetSchema + "." + t.TargetName,
                        RowEstimate = t.RowCountEstimate < 0 ? "?" : t.RowCountEstimate.ToString("N0"),
                        ColumnCount = t.Columns.Count,
                        Note = t.Comment ?? (t.PrimaryKey == null ? "无主键" : ""),
                        Table = t
                    });
                }
                AppendLog("源库对象：表 " + result.tables.Count + "，视图 " + result.viewCount +
                          "，过程/函数 " + result.routineCount + "，触发器 " + result.triggerCount +
                          "（视图会建；同类型库的过程/函数/触发器直接写入目标库，跨类型库生成改造草稿）");
                SetStatus("已读取 " + result.tables.Count + " 张表");
            }
            catch (Exception ex)
            {
                AppendLog("读取结构失败：" + ex.Message);
                SetStatus("读取失败");
            }
            finally
            {
                RunBusy(false, null);
            }
        }

        private OptionsConfig BuildOptions()
        {
            var opt = new OptionsConfig
            {
                Mode = "both",
                DropExisting = ChkDrop.IsChecked == true,
                TruncateTarget = ChkTruncate.IsChecked == true,
                CreateIndexes = ChkIndex.IsChecked == true,
                CreateIndexesAfterLoad = ChkIndexAfter.IsChecked == true,
                SkipUnsupportedColumns = ChkSkipUnsupported.IsChecked == true,
                ScriptOnly = ChkScriptOnly.IsChecked == true,
                VerifyMode = SelectedText(CmbVerify, "checksum"),
                IdentifierCase = SelectedText(CmbCase, "auto"),
                IdentityMode = SelectedText(CmbIdentity, "auto"),
                OutDir = TxtOut.Text.Trim()
            };
            if (int.TryParse(TxtBatch.Text.Trim(), out int batch) && batch > 0) opt.BatchSize = batch;
            return opt;
        }

        private static string SelectedText(ComboBox combo, string fallback)
        {
            var item = combo.SelectedItem as ComboBoxItem;
            return item == null ? fallback : Convert.ToString(item.Content);
        }

        private MigrationConfig BuildConfig(bool verifyOnly)
        {
            var cfg = new MigrationConfig
            {
                Source = BuildSource(),
                Target = BuildTarget(),
                Options = BuildOptions()
            };
            if (verifyOnly) cfg.Options.Mode = "verify";

            var selected = _items.Where(x => x.IsSelected).Select(x => x.SourceName).ToList();
            if (selected.Count == 0) throw new InvalidOperationException("请至少勾选一张表");
            if (selected.Count != _items.Count)
            {
                cfg.Options.Include = string.Join(",", selected);
            }
            return cfg;
        }

        // ------------------------------------------------------------------
        // 脚本 / 迁移 / 校验
        // ------------------------------------------------------------------

        private async void BtnScript_Click(object sender, RoutedEventArgs e)
        {
            string path = await GenerateScriptFile();
            if (!string.IsNullOrEmpty(path)) AppDialog.Show(this, "脚本已生成：\n" + path, "完成", DialogKind.Success);
        }

        private async Task<string> GenerateScriptFile()
        {
            if (_busy) return null;
            RunBusy(true, "正在生成脚本...");
            try
            {
                var cfg = BuildConfig(false);
                string script = await Task.Run(() =>
                {
                    var engine = new MigrationEngine(cfg, AppendLog);
                    var tables = engine.DiscoverTables();
                    return engine.GenerateFullScript(tables);
                });
                string dir = ReportWriter.ResolveOutDir(cfg.Options);
                string path = Path.Combine(dir, "schema_" + ReportWriter.Timestamp() + ".sql");
                File.WriteAllText(path, script, new UTF8Encoding(true));
                AppendLog("SQL 脚本：" + path);
                SetStatus("脚本已生成");
                return path;
            }
            catch (Exception ex)
            {
                AppendLog("生成脚本失败：" + ex.Message);
                SetStatus("生成失败");
                return null;
            }
            finally
            {
                RunBusy(false, null);
            }
        }

        private async void BtnPreview_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            RunBusy(true, "正在生成预览...");
            try
            {
                var cfg = BuildConfig(false);
                string script = await Task.Run(() =>
                {
                    var engine = new MigrationEngine(cfg, AppendLog);
                    var tables = engine.DiscoverTables();
                    return engine.GenerateFullScript(tables);
                });
                var box = new TextBox
                {
                    Text = script,
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 12
                };
                var win = new Window
                {
                    Title = "建表脚本预览（" + Dialects.Get(cfg.Target.Kind).Display + "）",
                    Width = 980,
                    Height = 720,
                    Owner = this,
                    Content = box,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner
                };
                win.ShowDialog();
                SetStatus("预览完成");
            }
            catch (Exception ex)
            {
                AppendLog("预览失败：" + ex.Message);
            }
            finally
            {
                RunBusy(false, null);
            }
        }

        private async void BtnMigrate_Click(object sender, RoutedEventArgs e)
        {
            if (ChkScriptOnly.IsChecked == true)
            {
                string path = await GenerateScriptFile();
                if (!string.IsNullOrEmpty(path))
                    AppDialog.Show(this, "SQL 已生成：\n" + path, "生成完成", DialogKind.Success);
                return;
            }
            await RunMigration(false);
        }

        private void ChkScriptOnly_Changed(object sender, RoutedEventArgs e)
        {
            if (BtnMigrate == null) return;
            bool script = ChkScriptOnly.IsChecked == true;
            BtnMigrate.Content = script ? "生成 SQL" : "开始迁移";
            BtnMigrate.ToolTip = script
                ? "只生成表/视图/过程/函数/触发器结构 SQL，不连接目标库、不搬数据"
                : "连接目标库直接建表、搬数据并创建视图/过程/函数/触发器，不需要手工执行 schema.sql";
        }

        private async void BtnVerify_Click(object sender, RoutedEventArgs e)
        {
            await RunMigration(true);
        }

        private async Task RunMigration(bool verifyOnly)
        {
            if (_busy) return;

            MigrationConfig cfg;
            try
            {
                cfg = BuildConfig(verifyOnly);
            }
            catch (Exception ex)
            {
                AppDialog.Show(this, ex.Message, "提示", DialogKind.Warning);
                return;
            }

            if (!verifyOnly && cfg.Options.DropExisting)
            {
                if (!AppDialog.Confirm(this, "目标库上的同名表将被删除后重建，是否继续？", "确认删除重建",
                    "继续迁移", "取消", DialogKind.Warning)) return;
            }

            RunBusy(true, verifyOnly ? "正在校验..." : "正在迁移...");
            try
            {
                var report = await Task.Run(() =>
                {
                    var engine = new MigrationEngine(cfg, AppendLog);
                    return engine.Run(false);
                });
                SetStatus(report.Success ? "完成：全部成功" : "完成：存在失败项，请看报告");
                var summary = new StringBuilder();
                foreach (TableResult t in report.Tables)
                {
                    summary.AppendLine("[" + t.Status + "] " + t.Source + " → " + t.Target + "  " + t.RowsCopied + " 行");
                }
                summary.AppendLine();
                summary.AppendLine("总行数 " + report.TotalRowsCopied + "，耗时 " + (report.FinishedAt - report.StartedAt).ToString(@"hh\:mm\:ss"));
                summary.AppendLine();
                summary.AppendLine("表、视图、外键、过程/函数/触发器已尝试直接写入目标库；");
                summary.AppendLine("同类型库的过程/函数/触发器直接创建；跨类型库无法安全直译的，会在 issues_*.csv 标记，并另存 objects_manual_*.sql 草稿。");
                AppendLog(summary.ToString());
                AppDialog.Show(this, summary.ToString() + "\n报告与脚本已写入：" + ReportWriter.ResolveOutDir(cfg.Options),
                    report.Success ? "迁移完成" : "迁移完成，但有失败项",
                    report.Success ? DialogKind.Success : DialogKind.Warning);
            }
            catch (Exception ex)
            {
                AppendLog("执行失败：" + ex.Message);
                SetStatus("执行失败");
                AppDialog.Show(this, ex.Message, "执行失败", DialogKind.Error);
            }
            finally
            {
                RunBusy(false, null);
            }
        }

        private void BtnOpenOut_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string dir = ReportWriter.ResolveOutDir(BuildOptions());
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppendLog("打开目录失败：" + ex.Message);
            }
        }

        // ------------------------------------------------------------------
        // UI 工具
        // ------------------------------------------------------------------

        private void RunBusy(bool busy, string status)
        {
            _busy = busy;
            Prg.IsIndeterminate = busy;
            BtnLoad.IsEnabled = !busy;
            BtnScript.IsEnabled = !busy;
            BtnPreview.IsEnabled = !busy;
            BtnMigrate.IsEnabled = !busy;
            BtnVerify.IsEnabled = !busy;
            BtnTestSrc.IsEnabled = !busy;
            BtnTestDst.IsEnabled = !busy;
            BtnSwap.IsEnabled = !busy;
            if (!string.IsNullOrEmpty(status)) SetStatus(status);
        }

        private void SetStatus(string text)
        {
            TxtStatus.Text = text;
        }

        /// <summary>线程安全的日志追加。</summary>
        private void AppendLog(string message)
        {
            if (message == null) return;
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action<string>(AppendLog), message);
                return;
            }
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
            TxtLog.AppendText(line + Environment.NewLine);
            TxtLog.ScrollToEnd();
        }
        // ------------------------------------------------------------------
        // 窗口消息结构（用于校正最大化边界）
        // ------------------------------------------------------------------

        private const int MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMaxTrackSize;
            public POINT ptMinTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    }
}
