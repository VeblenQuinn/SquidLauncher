using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SquidLauncher.Content;
using SquidLauncher.Core;
using SquidLauncher.Game;
using SquidLauncher.UI;
using SquidLauncher.UI.Pages;
using SquidLauncher.Update;

namespace SquidLauncher
{
    public partial class MainWindow : Window
    {
        public static MainWindow Current { get; private set; }

        readonly Dictionary<RadioButton, Stack<FrameworkElement>> _stacks = new Dictionary<RadioButton, Stack<FrameworkElement>>();
        readonly Dictionary<RadioButton, FrameworkElement> _roots = new Dictionary<RadioButton, FrameworkElement>();
        RadioButton _tab;
        DispatcherTimer _toastTimer;

        LaunchPage _launch;
        DownloadPage _download;
        SettingsPage _settings;
        MorePage _more;
        bool _deferredDone;
        bool _revealed;
        bool _settlePlayed;
        RotateTransform _settleRotate;
        TranslateTransform _settleShift;

        // 四个主页面都是「第一次进那个标签页」才创建：XAML 解析 + 控件树构建都不便宜，
        // 启动时只建首页（启动页），其余等用户点过去再说
        public LaunchPage LaunchPage => _launch ?? (_launch = new LaunchPage());
        public DownloadPage DownloadPage => _download ?? (_download = new DownloadPage());
        public SettingsPage SettingsPage => _settings ?? (_settings = new SettingsPage());
        public MorePage MorePage => _more ?? (_more = new MorePage());

        FrameworkElement RootOf(RadioButton tab)
        {
            if (_roots.TryGetValue(tab, out var p)) return p;
            p = tab == NavDownload ? (FrameworkElement)DownloadPage
              : tab == NavSettings ? SettingsPage
              : tab == NavMore ? MorePage
              : LaunchPage;
            _roots[tab] = p;
            return p;
        }

        /// <summary>当前标签页显示的内容：有子页面就是栈顶，否则是根页面</summary>
        FrameworkElement CurrentPage()
            => _stacks[_tab].Count > 0 ? _stacks[_tab].Peek() : RootOf(_tab);

        public MainWindow()
        {
            Current = this;
            InitializeComponent();

            // 闪白防护一：把窗口类的背景画刷改成主题底色。
            // 窗口从建好到画出第一帧这段，Windows 会用「类背景刷」擦客户区，默认是白色，
            // 那一下就是启动时看到的白框。必须赶在第一次绘制前，所以挂 SourceInitialized。
            SourceInitialized += (s, e) =>
            {
                try
                {
                    var h = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                    Theme.PaintWindowBack(h);
                    Log.Info($"[启动] 窗口已建立 hwnd=0x{h.ToInt64():X}，背景画刷改为主题底色，防止白闪");
                }
                catch (Exception ex) { Log.Warn("设置窗口背景画刷失败：" + ex.Message); }
            };
            // 闪白防护二：整个窗口隐形，等内容画完再淡入（RevealAfterFirstFrame）。
            // 之前只隐内容不够：Window.Background 用的是 DynamicResource，首次布局前还没解析，
            // 客户区就露出系统白。所以这里再显式给窗口涂一层主题底色。
            SetStartupHidden();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            void Mark(string what)
            {
                App.StartupMark?.Invoke($"主窗口/{what} {sw.ElapsedMilliseconds}ms");
                sw.Restart();
            }
            VersionTag.Text = "v" + AppInfo.VersionString;
            UpdateThemeButton();
            Theme.Changed += UpdateThemeButton;
            Theme.Changed += SyncWindowBackground;
            TaskList.ItemsSource = TaskManager.Tasks;
            TaskManager.Tasks.CollectionChanged += (s, e) => UpdateTaskBadge();
            TaskManager.Added += t => { t.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(LauncherTask.State)) Dispatcher.BeginInvoke((Action)UpdateTaskBadge); }; UpdateTaskBadge(); };
            TaskManager.Finished += OnTaskFinished;

            _stacks[NavLaunch] = new Stack<FrameworkElement>();
            _stacks[NavDownload] = new Stack<FrameworkElement>();
            _stacks[NavSettings] = new Stack<FrameworkElement>();
            _stacks[NavMore] = new Stack<FrameworkElement>();
            Mark("骨架");

            NavLaunch.IsChecked = true;   // 首屏内容：启动页
            Mark("首页");
            OpenStartupPage();
#if DEBUG
            InitDebugProbes();
#endif
            Loaded += async (s, e) => await StartupChecksAsync();
        }

        /// <summary>
        /// 启动时把整个窗口藏起来：用户看到的就是“程序还没窗口”，绝不会突然闪一下白。
        /// 同时把 Window.Background 显式设成主题底色——XAML 里那个 DynamicResource 要等首次
        /// 布局才解析，在那之前客户区是系统白，这正是之前白闪的来源。
        /// </summary>
        void SetStartupHidden()
        {
            Background = new SolidColorBrush(Theme.Background);
            Opacity = 0;
            // 再加一层保险：窗口先建在屏幕外。
            // WPF 的 Opacity 要等 HWND 建好才生效，这中间有几毫秒窗口可能是“不透明”的；
            // 建在屏幕外就完全没机会被看见。首帧后移回屏幕中央再淡入。
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -30000;
            Top = -30000;
        }

        /// <summary>主题变了同步窗口底色（窗口自身不参与 XAML 里的 DynamicResource 刷新）</summary>
        void SyncWindowBackground()
        {
            if (Opacity <= 0) return;   // 还没显形，等 Reveal 时再设
            Background = new SolidColorBrush(Theme.Background);
        }

        /// <summary>
        /// 首帧画完后把窗口淡进来（构造时 Opacity=0，用户在此之前看不到任何窗口）。
        /// 带兜底：App 侧 1.5 秒后也会强制调用。
        /// </summary>
        internal void RevealAfterFirstFrame()
        {
            if (_revealed) return;
            _revealed = true;
            if (Opacity >= 1) return;   // 本来就没被隐藏（离屏截图模式）
            try
            {
                Background = new SolidColorBrush(Theme.Background);
                // 从屏幕外移回屏幕中央（构造时为了不闪白放在了 -30000）
                var wa = SystemParameters.WorkArea;
                double w = ActualWidth > 0 ? ActualWidth : Width;
                double h = ActualHeight > 0 ? ActualHeight : Height;
                Left = wa.Left + Math.Max(0, (wa.Width - w) / 2);
                Top = wa.Top + Math.Max(0, (wa.Height - h) / 2);
                WindowStartupLocation = WindowStartupLocation.Manual;

                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                fade.Completed += (s, e) =>
                {
                    BeginAnimation(OpacityProperty, null);
                    Opacity = 1;
                    SyncWindowBackground();
                };
                BeginAnimation(OpacityProperty, fade);
                PlaySettleAnimation();
                Log.Info("[启动] 界面已就绪，窗口开始显形（此前整体不可见，不会有白闪）");
            }
            catch (Exception ex)
            {
                Log.Warn("窗口显形失败：" + ex.Message);
                Opacity = 1;
            }
        }

        /// <summary>
        /// 「刚端上来、被扶正」的落定动画：先歪一点，阻尼摆动两次归零。
        /// 抖的是内容层（Root 的 RenderTransform），不动 Window.Left/Top——不走窗口消息，
        /// 多显示器/DPI 缩放下不会跳帧；Root 本身没设底色，旋转后四角露出的就是窗口主题底色，看不出来。
        /// 与 140ms 淡入并行（t=0 起步），总共 300ms 内结束；只播一次，App 侧兜底重复调用 Reveal 时不重播。
        /// </summary>
        void PlaySettleAnimation()
        {
            if (_settlePlayed) return;
            _settlePlayed = true;
            try
            {
                if (_settleRotate == null)
                {
                    _settleRotate = new RotateTransform(0);
                    _settleShift = new TranslateTransform(0, 0);
                    var group = new TransformGroup();
                    group.Children.Add(_settleRotate);
                    group.Children.Add(_settleShift);
                    Root.RenderTransformOrigin = new Point(0.5, 0.5);   // 绕窗口中心，不是绕底边
                    Root.RenderTransform = group;
                }

                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var ease = new KeySpline(0.4, 0, 0.6, 1);   // 每段都是「峰处慢、过零快」

                // 旋转为主：0.35° 起手，阻尼萎缩到 0（两次可见回摆）
                var angle = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(300) };
                angle.KeyFrames.Add(new SplineDoubleKeyFrame(0.35, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0)), ease));
                angle.KeyFrames.Add(new SplineDoubleKeyFrame(-0.22, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(72)), ease));
                angle.KeyFrames.Add(new SplineDoubleKeyFrame(0.12, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(148)), ease));
                angle.KeyFrames.Add(new SplineDoubleKeyFrame(-0.05, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(218)), ease));
                angle.KeyFrames.Add(new SplineDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)), ease));

                // 位移为辅：峰值 2px，只为余韵，不作为主导
                var shift = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(300) };
                shift.KeyFrames.Add(new SplineDoubleKeyFrame(1.6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0)), ease));
                shift.KeyFrames.Add(new SplineDoubleKeyFrame(-1.4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(78)), ease));
                shift.KeyFrames.Add(new SplineDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(154)), ease));
                shift.KeyFrames.Add(new SplineDoubleKeyFrame(-0.4, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(222)), ease));
                shift.KeyFrames.Add(new SplineDoubleKeyFrame(0.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)), ease));

                // 动画结束把时钟摘掉、属性归零，别让 Transform 长期挂着动画时钟
                angle.Completed += (s, e) =>
                {
                    _settleRotate.BeginAnimation(RotateTransform.AngleProperty, null);
                    _settleShift.BeginAnimation(TranslateTransform.XProperty, null);
                    _settleRotate.Angle = 0;
                    _settleShift.X = 0;
                    Log.Info($"[启动] 落定动画结束 {stopwatch.ElapsedMilliseconds}ms（旋转 0.35°→0，位移 2px→0）");
                };

                _settleRotate.BeginAnimation(RotateTransform.AngleProperty, angle);
                _settleShift.BeginAnimation(TranslateTransform.XProperty, shift);
            }
            catch (Exception ex)
            {
                Log.Warn("落定动画失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 首帧之后再做「不影响看到界面」的活：背景图解码。
        /// 大图解码几十到几百毫秒，放在首帧之前只会让窗口白等。
        /// </summary>
        internal void RunDeferredStartup()
        {
            if (_deferredDone) return;   // ContentRendered 在内容变更时会反复派发
            _deferredDone = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                ApplyBackground();
                App.StartupMark?.Invoke($"延迟/背景 {sw.ElapsedMilliseconds}ms");
            }), DispatcherPriority.Background);
        }

        // ---------- 导航 ----------
        void Nav_Checked(object sender, RoutedEventArgs e)
        {
            _tab = (RadioButton)sender;
            Show(CurrentPage());
        }

        void Show(FrameworkElement page)
        {
            PageHost.Content = page;
            page.Opacity = 0;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            // 纵向位移交给内容区卡片（ContentReveal），整页只留 180ms 淡入当底色，免得两层叠着一起下坠
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            (page as IPage)?.OnShown();
            SidebarReveal.Play(page);
            ContentReveal.Play(page);
        }

        /// <summary>在当前标签页里打开子页面（有返回）</summary>
        public void Push(FrameworkElement page)
        {
            _stacks[_tab].Push(page);
            Show(page);
        }

        public void Back()
        {
            var st = _stacks[_tab];
            if (st.Count > 0) st.Pop();
            Show(CurrentPage());
        }

        public void GoTab(string name, FrameworkElement sub = null)
        {
            var rb = name switch { "download" => NavDownload, "settings" => NavSettings, "more" => NavMore, _ => NavLaunch };
            var st = _stacks[rb];
            while (st.Count > 0) st.Pop();
            if (rb.IsChecked == true) { _tab = rb; Show(CurrentPage()); } else rb.IsChecked = true;
            if (sub != null) Push(sub);
        }

        /// <summary>命令行 --page download|settings|more|versions|mods|modsearch 直接打开某页（快捷方式 / 测试用）</summary>
        void OpenStartupPage()
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "--page");
            if (i < 0 || i + 1 >= args.Length) return;
            switch (args[i + 1])
            {
                case "download": GoTab("download"); break;
                case "settings": GoTab("settings"); break;
                case "more": GoTab("more"); break;
                case "versions": Push(new VersionsPage()); break;
                case "instance":
                    var inst = LaunchPage.SelectedInstance();
                    if (inst != null) Push(new InstancePage(inst, i + 2 < args.Length ? args[i + 2] : null));
                    break;
                case "modsearch":
                    GoTab("download");
                    DownloadPage.OpenContent(global::SquidLauncher.Content.ContentType.Mod, LaunchPage.SelectedInstance());
                    break;
            }
        }

#if DEBUG
        /// <summary>诊断入口。</summary>
        void InitDebugProbes()
        {
            // 诊断：--seedmap-struct 只看结构标记与详情卡片（跑完不退出，方便外部截图）
            if (Environment.GetCommandLineArgs().Contains("--seedmap-struct"))
            {
                Loaded += async (s, e) =>
                {
                    await Task.Delay(1500);
                    GoTab("more");
                    await Task.Delay(400);
                    MorePage.ShowSeedMap();
                    await Task.Delay(8000);

                    var sb = new System.Text.StringBuilder();
                    Log.Info("[诊断] 结构 A：导航完成");
                    sb.AppendLine(MorePage.SeedMapProbeStructs());
                    Log.Info("[诊断] 结构 B：扫描完成");
                    sb.AppendLine(MorePage.SeedMapProbeClickMark());
                    Log.Info("[诊断] 结构 C：点击完成");
                    sb.AppendLine("=== 远离原点仍应有标记 ===");
                    foreach (int c in new[] { 1000, 100000, 1000000, 10000000, 29000000 })
                        sb.AppendLine(MorePage.SeedMapProbeStructAt(c, c));
                    Log.Info("[诊断] 结构 D：远坐标完成");
                    MorePage.SeedMapReset();

                    // 先落盘一次：滚动探针要模拟真实鼠标输入，万一卡住也能看到前面结果
                    var structFile = System.IO.Path.Combine(Paths.Logs, "struct.txt");
                    System.IO.File.WriteAllText(structFile, sb.ToString(), System.Text.Encoding.UTF8);

                    sb.AppendLine("=== 内置引擎 === ");
                    sb.AppendLine(MorePage.SeedMapProbeEngineBundle());
                    Log.Info("[诊断] 内置引擎检查完成");

                    sb.AppendLine("=== 前往坐标 === ");
                    sb.AppendLine(MorePage.SeedMapProbeGoto());
                    Log.Info("[诊断] 结构 E：前往坐标完成");

                    sb.AppendLine("=== 滚动冲突（结构列表 vs 左侧面板） ===");
                    try
                    {
                        sb.AppendLine(await MorePage.SeedMapProbeScrollConflictAsync());
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine("滚动探针异常：" + ex.GetType().Name + " " + ex.Message);
                        Log.Warn("滚动探针异常：" + ex);
                    }
                    Log.Info("[诊断] 结构 F：滚动完成");

                    System.IO.File.WriteAllText(structFile, sb.ToString(), System.Text.Encoding.UTF8);
                    Log.Info("[诊断] 结构测试写盘完成");
                };
            }
            // 诊断：--seedmap-test 检查瓦片按需加载与拖动的每帧开销
            if (Environment.GetCommandLineArgs().Contains("--seedmap-test"))
            {
                Loaded += async (s, e) =>
                {
                    var file = System.IO.Path.Combine(Paths.Logs, "seedmap.txt");
                    var sb = new System.Text.StringBuilder();
                    // 边跑边写：万一卡在某一节，也能看出卡在哪，而不是等整份文件
                    void Flush()
                    {
                        try { System.IO.File.WriteAllText(file, sb.ToString(), System.Text.Encoding.UTF8); }
                        catch (Exception ex) { Log.Warn("写诊断文件失败：" + ex.Message); }
                    }

                    await Task.Delay(1500);
                    GoTab("more");
                    await Task.Delay(400);
                    MorePage.ShowSeedMap();
                    await Task.Delay(7000);   // 等瓦片一块块算完（每块约 200~400ms，2 个并发）

                    sb.AppendLine("=== 拖动 ===");
                    sb.AppendLine(MorePage.SeedMapProbe(40, 40, 0));
                    Flush();
                    sb.AppendLine("=== 拖动每帧 ===");
                    sb.AppendLine(MorePage.SeedMapProbeLive(60, 8));
                    Flush();
                    sb.AppendLine("=== 缩放 16→4 ===");
                    sb.AppendLine(MorePage.SeedMapProbeZoom(16, 4, 30));
                    Flush();
                    sb.AppendLine("=== 缩放 16→256 ===");
                    sb.AppendLine(MorePage.SeedMapProbeZoom(16, 256, 60));
                    Flush();
                    sb.AppendLine("=== 缝隙 ===");
                    sb.AppendLine(MorePage.SeedMapProbeSeams(400));
                    Flush();

                    // 结构测试回到正常视图（1:16），并且让扫描走正常异步路径
                    sb.AppendLine("=== 结构标记 ===");
                    MorePage.SeedMapSetZoom(16);
                    MorePage.SeedMapReset();
                    await Task.Delay(5000);
                    sb.AppendLine(MorePage.SeedMapProbeStructs());
                    Flush();
                    sb.AppendLine("=== 点击标记 ===");
                    sb.AppendLine(MorePage.SeedMapProbeClickMark());
                    Flush();

                    Log.Info("种子地图瓦片测试完成");
                };
            }
            // 诊断：--installed-test 检查“已安装 / 已安装 · 旧版本”的扫描与识别
            if (Environment.GetCommandLineArgs().Contains("--installed-test"))
            {
                Loaded += async (s, e) =>
                {
                    var file = System.IO.Path.Combine(Paths.Logs, "installed.txt");
                    var sb = new System.Text.StringBuilder();
                    void Flush()
                    {
                        try { System.IO.File.WriteAllText(file, sb.ToString(), System.Text.Encoding.UTF8); }
                        catch (Exception ex) { Log.Warn("写诊断文件失败：" + ex.Message); }
                    }
                    await Task.Delay(800);
                    try
                    {
                        var repo = GameRepo.Current;
                        var inst = repo.ListInstances().FirstOrDefault(i => i.IsValid && i.Id == Settings.SelectedVersion)
                                   ?? repo.ListInstances().FirstOrDefault(i => i.IsValid);
                        if (inst == null) { sb.AppendLine("没有可用实例"); Flush(); return; }
                        sb.AppendLine($"实例 {inst.Id}  MC={inst.McVersion}  Loader={LoaderInfo.Slug(inst.PrimaryLoader)}  隔离={inst.Isolated}");
                        foreach (var t in new[] { ContentType.Mod, ContentType.ResourcePack, ContentType.Shader })
                        {
                            var dir = InstalledContent.DirOf(inst, t);
                            var map = InstalledContent.ScanLocal(inst, t);
                            sb.AppendLine($"[{ContentTypeName(t)}] dir={dir} 索引命中={map?.Count ?? -1}");
                            Flush();
                        }

                        var mods = InstalledContent.ScanLocal(inst, ContentType.Mod);
                        sb.AppendLine("=== 深度识别（Modrinth 哈希） ===");
                        var changed = await InstalledContent.IdentifyAsync(inst, ContentType.Mod, CancellationToken.None);
                        mods = InstalledContent.ScanLocal(inst, ContentType.Mod);
                        sb.AppendLine($"识别结果 changed={changed} 命中={mods?.Count ?? -1}");
                        Flush();

                        sb.AppendLine("=== 旧版本检查 ===");
                        try
                        {
                            await InstalledContent.CheckOutdatedAsync(InstalledContent.DirOf(inst, ContentType.Mod), mods,
                                inst.McVersion, LoaderInfo.Slug(inst.PrimaryLoader), true, CancellationToken.None);
                        }
                        catch (Exception ex) { sb.AppendLine("旧版本检查异常：" + ex.GetType().Name + " " + ex.Message); }
                        int old = 0;
                        foreach (var kv in mods.OrderBy(k => k.Key).Take(400))
                        {
                            if (kv.Value.Outdated) old++;
                            sb.AppendLine($"  {kv.Key,-46} file={kv.Value.FileId} 旧={kv.Value.Outdated} 新文件={kv.Value.LatestFileName}");
                        }
                        sb.AppendLine($"旧版本数量={old} / {mods.Count}");

                        // 列表打标效果
                        var fakes = mods.Values.Take(3).Select(m => new ContentProject { Source = m.Source, Id = m.ProjectId, Title = m.FileName }).ToList();
                        InstalledContent.MarkProjects(fakes, mods);
                        foreach (var f in fakes) sb.AppendLine($"  列表标记：{f.Title} → “{f.InstalledText}” ShowInstalled={f.ShowInstalled} ShowOld={f.ShowInstalledOld}");
                        Flush();

                        sb.AppendLine("=== slug 跨站匹配 ===");
                        try
                        {
                            await InstalledContent.ResolveSlugsAsync(InstalledContent.DirOf(inst, ContentType.Mod), CancellationToken.None);
                        }
                        catch (Exception ex) { sb.AppendLine("slug 解析异常：" + ex.GetType().Name + " " + ex.Message); }
                        mods = InstalledContent.ScanLocal(inst, ContentType.Mod);
                        var withSlug = mods.Values.Count(m => !string.IsNullOrEmpty(m.Slug));
                        sb.AppendLine($"有 slug 的已装项目 = {withSlug} / {mods.Count}");
                        var sample = mods.Values.FirstOrDefault(m => !string.IsNullOrEmpty(m.Slug));
                        if (sample != null)
                        {
                            sb.AppendLine($"样本：{sample.FileName}  slug={sample.Slug}  源={sample.Source}");
                            // 假装这是另一个资源站上搜到的同一个项目（ID 故意给个错的）
                            var other = new ContentProject
                            {
                                Source = sample.Source == ContentSource.CurseForge ? ContentSource.Modrinth : ContentSource.CurseForge,
                                Id = "zzz-not-real",
                                Slug = sample.Slug,
                                Title = sample.Slug,
                            };
                            InstalledContent.MarkProjects(new[] { other }, mods);
                            sb.AppendLine($"  跨站匹配：{other.SourceText} / {other.Slug} → “{other.InstalledText}”（应为“已安装”或“已安装 · 旧版本”）");
                        }
                        Flush();

                        sb.AppendLine("=== 详情页按文件名匹配 ===");
                        var anyItem = mods.Values.FirstOrDefault();
                        if (anyItem != null)
                        {
                            var dir = InstalledContent.DirOf(inst, ContentType.Mod);
                            sb.AppendLine($"  已装文件 {anyItem.FileName} 在目录里？{InstalledContent.FilePresent(dir, anyItem.FileName)}");
                            sb.AppendLine($"  文件名不存在时？{InstalledContent.FilePresent(dir, "no-such-file-12345.jar")}");
                            var fakeFile = new ContentFile
                            {
                                Source = anyItem.Source == ContentSource.CurseForge ? ContentSource.Modrinth : ContentSource.CurseForge,
                                ProjectId = "zzz", FileId = "zzz", FileName = anyItem.FileName,
                            };
                            sb.AppendLine($"  跨站同文件 → FilePresent={InstalledContent.FilePresent(dir, fakeFile.FileName)}");
                        }
                        Flush();
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine("探针异常：" + ex);
                        Flush();
                    }
                    Log.Info("已安装资源测试完成");
                };
            }

            // 诊断：--deps-test 检查前置解析（不真下载）
            if (Environment.GetCommandLineArgs().Contains("--deps-test"))
            {
                Loaded += async (s, e) =>
                {
                    var file = System.IO.Path.Combine(Paths.Logs, "deps.txt");
                    var sb = new System.Text.StringBuilder();
                    void Flush()
                    {
                        try { System.IO.File.WriteAllText(file, sb.ToString(), System.Text.Encoding.UTF8); }
                        catch (Exception ex) { Log.Warn("写诊断文件失败：" + ex.Message); }
                    }
                    await Task.Delay(800);
                    try
                    {
                        var repo = GameRepo.Current;
                        var inst = repo.ListInstances().FirstOrDefault(i => i.IsValid && i.Id == Settings.SelectedVersion)
                                   ?? repo.ListInstances().FirstOrDefault(i => i.IsValid);
                        if (inst == null) { sb.AppendLine("没有可用实例"); Flush(); return; }
                        var dir = ContentService.TargetDir(inst, ContentType.Mod);
                        var loader = LoaderInfo.Slug(inst.PrimaryLoader);
                        sb.AppendLine($"实例 {inst.Id} MC={inst.McVersion} Loader={loader}");
                        sb.AppendLine($"目标目录 {dir} 存在={System.IO.Directory.Exists(dir)}");

                        foreach (var slug in new[] { "sophisticated-backpacks", "mekanism-generators", "ae2" })
                        {
                            sb.AppendLine($"--- {slug} ---");
                            try
                            {
                                var proj = await ContentService.Modrinth.GetProjectAsync(slug, CancellationToken.None);
                                var files = await ContentService.Modrinth.GetFilesAsync(proj.Id, inst.McVersion, loader, CancellationToken.None);
                                var best = ContentService.PickBest(files);
                                if (best == null) { sb.AppendLine("  没有适配文件"); Flush(); continue; }
                                sb.AppendLine($"  {proj.Title} → {best.FileName} 必需前置声明={best.Dependencies.Count(d => d.Required)}");
                                var plan = await ContentService.PlanDependenciesAsync(best, dir, inst.McVersion, loader, CancellationToken.None);
                                sb.AppendLine($"  计划下载={plan.Files.Count} 已装跳过={plan.Skipped.Count} 找不到={plan.Missing.Count}");
                                for (int i = 0; i < plan.Files.Count; i++)
                                    sb.AppendLine($"    + {plan.Names[i]}  [{plan.Files[i].FileName}]");
                                if (plan.Skipped.Count > 0) sb.AppendLine("    跳过：" + string.Join("、", plan.Skipped));
                                if (plan.Missing.Count > 0) sb.AppendLine("    缺失：" + string.Join("、", plan.Missing));
                                if (plan.Any)
                                {
                                    var picks = new System.Collections.Generic.List<ProjectPage.DepPick>();
                                    var body = ProjectPage.DepPickBody(plan, null, picks);
                                    sb.AppendLine($"    询问正文元素数={(body as System.Windows.Controls.Panel)?.Children.Count} 复选框行数={picks.Count}");
                                }
                            }
                            catch (Exception ex) { sb.AppendLine($"  异常：{ex.GetType().Name} {ex.Message}"); }
                            Flush();
                        }

                        sb.AppendLine("=== 空目录下解析（应有前置要下载） ===");
                        try
                        {
                            var tmp = System.IO.Path.Combine(Paths.Temp, "depstest");
                            System.IO.Directory.CreateDirectory(tmp);
                            foreach (var f in System.IO.Directory.GetFiles(tmp)) System.IO.File.Delete(f);
                            var proj = await ContentService.Modrinth.GetProjectAsync("ae2", CancellationToken.None);
                            var files = await ContentService.Modrinth.GetFilesAsync(proj.Id, inst.McVersion, loader, CancellationToken.None);
                            var best = ContentService.PickBest(files);
                            var plan = await ContentService.PlanDependenciesAsync(best, tmp, inst.McVersion, loader, CancellationToken.None);
                            sb.AppendLine($"  计划下载={plan.Files.Count} 已装跳过={plan.Skipped.Count} 找不到={plan.Missing.Count}");
                            for (int i = 0; i < plan.Files.Count; i++) sb.AppendLine($"    + {plan.Names[i]}  [{plan.Files[i].FileName}]");
                            var picks2 = new System.Collections.Generic.List<ProjectPage.DepPick>();
                            var body = ProjectPage.DepPickBody(plan, null, picks2);
                            sb.AppendLine($"  询问正文元素数={(body as System.Windows.Controls.Panel)?.Children.Count} 复选框行数={picks2.Count}（期望 ≥2）");
                            try { System.IO.Directory.Delete(tmp, true); } catch { }
                        }
                        catch (Exception ex) { sb.AppendLine("  空目录解析异常：" + ex); }
                        Flush();

                        sb.AppendLine("=== 详情页 UI（不点任何东西也该有前置面板） ===");
                        try
                        {
                            var proj = await ContentService.Modrinth.GetProjectAsync("ae2", CancellationToken.None);
                            var page = new ProjectPage(proj, ContentType.Mod, inst, inst.McVersion, loader);
                            page.OnShown();
                            for (int i = 0; i < 20 && page.Files.Items.Count == 0; i++) await Task.Delay(1000);
                            sb.AppendLine($"  文件行数={page.Files.Items.Count} 前置面板（未选中）={page.DepPanel.Visibility} 状态=“{page.StatusText.Text}”");
                            if (page.Files.Items.Count > 0) { page.Files.SelectedIndex = 0; await Task.Delay(4000); }
                            sb.AppendLine($"  选中后前置面板={page.DepPanel.Visibility} 前置项={page.DepList.Items.Count}");
                            sb.AppendLine($"  标题={page.DepTitle.Text}  提示={page.DepHint.Text}");
                            foreach (var it in page.DepList.Items)
                                if (it is DepRow r) sb.AppendLine($"    · {r.Title}  备注={r.Note}  项目={r.ProjectId}  slug={r.Slug}");
                        }
                        catch (Exception ex) { sb.AppendLine("  UI 部分异常：" + ex); }
                        Flush();
                    }
                    catch (Exception ex) { sb.AppendLine("探针异常：" + ex); Flush(); }
                    Log.Info("前置解析测试完成");
                };
            }

            // 诊断：--pagecost 量一下四个主页面各自的创建成本（懒加载省下的就是这些）
            if (Environment.GetCommandLineArgs().Contains("--pagecost"))
            {
                Loaded += (s, e) => Dispatcher.BeginInvoke((Action)(() =>
                {
                    var sb = new System.Text.StringBuilder();
                    sb.AppendLine("页面创建成本（Debug 版，Release 会更快）");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var a = new LaunchPage(); sb.AppendLine($"  LaunchPage  {sw.ElapsedMilliseconds}ms"); sw.Restart();
                    var b = new DownloadPage(); sb.AppendLine($"  DownloadPage {sw.ElapsedMilliseconds}ms"); sw.Restart();
                    var c = new SettingsPage(); sb.AppendLine($"  SettingsPage {sw.ElapsedMilliseconds}ms"); sw.Restart();
                    var d = new MorePage(); sb.AppendLine($"  MorePage    {sw.ElapsedMilliseconds}ms"); sw.Restart();
                    sb.AppendLine($"GC.KeepAlive: {a != null && b != null && c != null && d != null}");

                    // 对照：一个最简窗口到这里（框架已暖）要多久。用来分辨「我们的 XAML 慢」还是「WPF 首次开窗慢」
                    var w2 = new Window { Width = 400, Height = 300, Title = "probe", Content = new TextBlock { Text = "probe" } };
                    sb.AppendLine($"  裸窗口 构造 {sw.ElapsedMilliseconds}ms"); sw.Restart();
                    w2.Show();
                    sb.AppendLine($"  裸窗口 Show {sw.ElapsedMilliseconds}ms"); sw.Restart();
                    w2.Close();

                    // 对照 2：同样最简，但套上和主窗口一样的 WindowChrome，看自定义标题栏要多少
                    var w3 = new Window { Width = 400, Height = 300, Title = "probe3", Content = new TextBlock { Text = "probe" } };
                    System.Windows.Shell.WindowChrome.SetWindowChrome(w3, new System.Windows.Shell.WindowChrome
                    {
                        CaptionHeight = 52,
                        ResizeBorderThickness = new Thickness(6),
                        GlassFrameThickness = new Thickness(0, 0, 0, 1),
                        UseAeroCaptionButtons = false,
                    });
                    w3.Show();
                    sb.AppendLine($"  裸窗口+WindowChrome Show {sw.ElapsedMilliseconds}ms"); sw.Restart();
                    w3.Close();

                    // 对照 3：Styles.xaml 单独解析一次要多久（框架已暖，可大致反映冷启时的量级）
                    var rd = new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/SquidLauncher;component/UI/Styles.xaml"),
                    };
                    sb.AppendLine($"  Styles.xaml 解析 {sw.ElapsedMilliseconds}ms（{rd.Count} 个资源）"); sw.Restart();
                    System.IO.File.WriteAllText(System.IO.Path.Combine(Paths.Logs, "pagecost.txt"), sb.ToString(), System.Text.Encoding.UTF8);
                    Log.Info("页面成本测试完成");
                }), DispatcherPriority.ApplicationIdle);
            }

            // 诊断：--more-test 检查“滚到底”时是否给出加载提示
            if (Environment.GetCommandLineArgs().Contains("--more-test"))
            {
                Loaded += async (s, e) =>
                {
                    var file = System.IO.Path.Combine(Paths.Logs, "more.txt");
                    var sb = new System.Text.StringBuilder();
                    void Flush()
                    {
                        try { System.IO.File.WriteAllText(file, sb.ToString(), System.Text.Encoding.UTF8); }
                        catch (Exception ex) { Log.Warn("写诊断文件失败：" + ex.Message); }
                    }
                    await Task.Delay(600);
                    Window win = null;
                    try
                    {
                        var view = new ContentSearchView(ContentType.Mod);
                        win = new Window { Width = 1000, Height = 620, Content = view, Title = "more-test" };
                        win.Show();
                        view.OnShown();
                        await Task.Delay(3000);
                        await view.ProbeSearchAsync("sodium");
                        await Task.Delay(1200);
                        sb.AppendLine("首页加载完成：" + view.ProbeCounts() + " 底部提示=" + view.ProbeMoreState());
                        Flush();

                        view.ProbeScrollToBottom();
                        await Task.Delay(200);
                        sb.AppendLine("刚滚到底：" + view.ProbeCounts() + " 底部提示=" + view.ProbeMoreState());
                        Flush();

                        await Task.Delay(4000);
                        sb.AppendLine("一页加载后：" + view.ProbeCounts() + " 底部提示=" + view.ProbeMoreState());
                        Flush();

                        for (int i = 0; i < 10; i++)
                        {
                            view.ProbeScrollToBottom();
                            await Task.Delay(1500);
                            sb.AppendLine($"  第 {i + 1} 次滚到底：" + view.ProbeCounts() + " 底部提示=" + view.ProbeMoreState());
                        }
                        Flush();
                        sb.AppendLine("结束");
                    }
                    catch (Exception ex) { sb.AppendLine("探针异常：" + ex); }
                    finally { try { win?.Close(); } catch { } }
                    Flush();
                    Log.Info("滚到底提示测试完成");
                };
            }

            static string ContentTypeName(ContentType t) => t switch
            {
                ContentType.Mod => "Mod",
                ContentType.ResourcePack => "资源包",
                ContentType.Shader => "光影",
                _ => t.ToString(),
            };
        }
#endif

        // ---------- 标题栏 ----------
        void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        void Close_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>标题栏上一键切换明暗（会覆盖"跟随系统"设置）</summary>
        void Theme_Click(object sender, RoutedEventArgs e)
        {
            var mode = Theme.IsDark ? ThemeMode.Light : ThemeMode.Dark;
            Settings.ThemeMode = Theme.ModeKey(mode);
            Theme.Apply(Settings.ThemeColor, mode);
            Toast(mode == ThemeMode.Dark ? "已切换到深色模式" : "已切换到浅色模式", ToastKind.Ok);
        }

        void UpdateThemeButton()
        {
            if (ThemeButton == null) return;
            // 深色时显示太阳（点了变浅色），浅色时显示月亮
            ThemeButton.Content = Theme.IsDark ? "\uE706" : "\uE708";
            ThemeButton.ToolTip = Theme.IsDark ? "切换到浅色模式" : "切换到深色模式";
        }

        void Window_StateChanged(object sender, EventArgs e)
        {
            // 无边框窗口最大化时会溢出屏幕边缘几个像素
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        }

        // ---------- 任务 ----------
        void Tasks_Click(object sender, RoutedEventArgs e) => ToggleTasks(TaskLayer.Visibility != Visibility.Visible);
        void TaskLayer_MouseDown(object sender, MouseButtonEventArgs e) => ToggleTasks(false);
        void ClearTasks_Click(object sender, RoutedEventArgs e) => TaskManager.ClearFinished();
        void CancelTask_Click(object sender, RoutedEventArgs e) => ((sender as FrameworkElement)?.DataContext as LauncherTask)?.Cancel();

        public void ToggleTasks(bool show)
        {
            TaskLayer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            NoTasks.Visibility = TaskManager.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void UpdateTaskBadge()
        {
            int running = TaskManager.Tasks.Count(t => t.IsActive);
            TaskCount.Text = running > 0 ? running.ToString() : "";
            NoTasks.Visibility = TaskManager.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void OnTaskFinished(LauncherTask t)
        {
            UpdateTaskBadge();
            if (t.Title.StartsWith("启动 ")) return; // 启动页自己会提示（窗口出现 / 失败对话框）
            // 「另存」自己会在完成时提示保存到哪儿（带完整路径），别再被通用提示盖掉
            if (t.State == TaskState.Done && !t.Title.StartsWith("另存 ")) Toast($"{t.Title}：完成", ToastKind.Ok);
            else if (t.State == TaskState.Failed) Toast($"{t.Title}：失败（点右上角任务查看原因）", ToastKind.Error);
        }

        // ---------- 提示 ----------
        public enum ToastKind { Info, Ok, Error }

        public void Toast(string text, ToastKind kind = ToastKind.Info)
        {
            ToastText.Text = text;
            ToastIcon.Text = kind == ToastKind.Ok ? "" : kind == ToastKind.Error ? "" : "";
            ToastBox.Visibility = Visibility.Visible;
            ToastBox.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
            _toastTimer?.Stop();
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == ToastKind.Error ? 5 : 3) };
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer.Stop();
                var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(250));
                anim.Completed += (s2, e2) => ToastBox.Visibility = Visibility.Collapsed;
                ToastBox.BeginAnimation(OpacityProperty, anim);
            };
            _toastTimer.Start();
        }

        // ---------- 对话框 ----------
        readonly Stack<object> _dialogStack = new Stack<object>();

        internal void ShowDialogContent(FrameworkElement content)
        {
            if (DialogHost.Content != null) _dialogStack.Push(DialogHost.Content);
            DialogHost.Content = content;
            DialogLayer.Visibility = Visibility.Visible;
            content.Opacity = 0;
            var st = new ScaleTransform(0.96, 0.96);
            content.RenderTransformOrigin = new Point(0.5, 0.5);
            content.RenderTransform = st;
            content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(160)));
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(160)));
        }

        internal void CloseDialogContent(FrameworkElement content)
        {
            if (DialogHost.Content != content) return;
            if (_dialogStack.Count > 0) DialogHost.Content = _dialogStack.Pop();
            else { DialogHost.Content = null; DialogLayer.Visibility = Visibility.Collapsed; }
        }

        // ---------- 背景 ----------
        public void ApplyBackground()
        {
            var img = Theme.LoadImage(Settings.BackgroundImage, 1920);
            BgImage.Source = img;
            BgImage.Visibility = img == null ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---------- 启动时 ----------
        async Task StartupChecksAsync()
        {
            _ = Task.Run(() => Mirrors.ProbeAsync());
            if (Environment.GetCommandLineArgs().Contains("--updated"))
                Toast($"已更新到 {AppInfo.VersionString}", ToastKind.Ok);

            if (Settings.AutoCheckUpdate)
            {
                try
                {
                    await Task.Delay(1500);
                    var info = await Task.Run(() => Updater.CheckAsync());
                    if (info != null && info.IsNewer) await Dialogs.ShowUpdateAsync(info);
                }
                catch (Exception ex) { Log.Warn("自动检查更新失败：" + ex.Message); }
            }
        }
    }

    /// <summary>页面显示时回调（刷新数据用）</summary>
    public interface IPage
    {
        void OnShown();
    }
}
