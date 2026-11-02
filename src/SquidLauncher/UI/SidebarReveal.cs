using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SquidLauncher.Core;

namespace SquidLauncher.UI
{
    /// <summary>
    /// 左侧栏入场：条目和分组小标题按视觉顺序，自左向右依次淡入 + 轻微右移到位。
    /// 只在「进入页面」时播放（由 MainWindow.Show 调用），页内切子项不重播。
    /// 与整页淡入（MainWindow.Show 里的 Opacity + TranslateY）同帧开跑，互不冲突。
    /// </summary>
    internal static class SidebarReveal
    {
        const double ShiftX    = -12;   // 起始横向偏移（px）
        const double DurationMs = 220;  // 单条时长
        const double StaggerMs  = 35;   // 相邻元素错峰
        const int    DebounceMs = 300;  // 同一页面的重复调用忽略窗口

        // InstancePage.OnShown() 会被手动再调一次，靠这个窗口挡住双播
        static readonly Dictionary<object, long> LastPlay = new Dictionary<object, long>();

        // .NET Framework 上没有 Environment.TickCount64，用单调 Stopwatch 当时间源
        static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>进入页面时调用；元素还没挂上树，所以排到 Loaded 优先级再播。</summary>
        public static void Play(FrameworkElement page)
        {
            if (page == null) return;
            page.Dispatcher.BeginInvoke(new Action(() => PlayNow(page)), DispatcherPriority.Loaded);
        }

        static void PlayNow(FrameworkElement page)
        {
            try
            {
                var queue = Collect(page);
                if (queue.Count == 0) return;

                long now = Clock.ElapsedMilliseconds;
                if (LastPlay.TryGetValue(page, out long last) && now - last < DebounceMs) return;
                LastPlay[page] = now;

                Log.Info(string.Format("侧栏入场：{0} 项，首项 0ms 起步，末项 {1:0}ms 起步、{2:0}ms 到位",
                    queue.Count, (queue.Count - 1) * StaggerMs, (queue.Count - 1) * StaggerMs + DurationMs));

                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                for (int i = 0; i < queue.Count; i++)
                {
                    var el = queue[i];
                    var delay = TimeSpan.FromMilliseconds(i * StaggerMs);
                    var dur   = TimeSpan.FromMilliseconds(DurationMs);

                    // 起点：先清掉上一轮动画与残留值，避免重播时从半路开始
                    el.BeginAnimation(UIElement.OpacityProperty, null);
                    el.Opacity = 0;

                    var tt = el.RenderTransform as TranslateTransform;
                    if (tt == null)
                    {
                        tt = new TranslateTransform(ShiftX, 0);
                        el.RenderTransform = tt;
                    }
                    tt.BeginAnimation(TranslateTransform.XProperty, null);
                    tt.X = ShiftX;

                    var fade = new DoubleAnimation(0, 1, dur)
                    {
                        BeginTime = delay,
                        EasingFunction = ease,
                        FillBehavior = FillBehavior.HoldEnd
                    };
                    var slide = new DoubleAnimation(ShiftX, 0, dur)
                    {
                        BeginTime = delay,
                        EasingFunction = ease,
                        FillBehavior = FillBehavior.HoldEnd
                    };

                    // 播完把动画清掉、写回终值，下一次进入才能干净重播
                    var target = el;
                    var transform = tt;
                    fade.Completed += (s, e) => page.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        target.BeginAnimation(UIElement.OpacityProperty, null);
                        target.Opacity = 1;
                        transform.BeginAnimation(TranslateTransform.XProperty, null);
                        transform.X = 0;
                    }), DispatcherPriority.Background);

                    el.BeginAnimation(UIElement.OpacityProperty, fade);
                    tt.BeginAnimation(TranslateTransform.XProperty, slide);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("侧栏入场动画失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 找出这一页的侧栏队列：先定位用 SideNav 样式的条目，再取它们所在的容器，
        /// 按容器里的 XAML 顺序把「分组小标题 + 条目」排成一条队列。
        /// 底部按钮、页面头部不在同一个容器里，自然被排除。
        /// </summary>
        static List<FrameworkElement> Collect(DependencyObject page)
        {
            var queue = new List<FrameworkElement>();
            var app = Application.Current;
            if (app == null) return queue;

            var navStyle = app.TryFindResource("SideNav") as Style;
            var subStyle = app.TryFindResource("Sub") as Style;
            if (navStyle == null) return queue;

            var panels = new List<Panel>();
            Walk(page, navStyle, panels);

            foreach (var panel in panels)
            {
                foreach (var child in panel.Children)
                {
                    if (child is RadioButton rb && ReferenceEquals(rb.Style, navStyle)) queue.Add(rb);
                    else if (child is TextBlock tb && subStyle != null && ReferenceEquals(tb.Style, subStyle)) queue.Add(tb);
                }
            }
            return queue;
        }

        static void Walk(DependencyObject node, Style navStyle, List<Panel> panels)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(node))
            {
                if (!(child is DependencyObject dep)) continue;

                if (child is RadioButton rb && ReferenceEquals(rb.Style, navStyle))
                {
                    var parent = LogicalTreeHelper.GetParent(rb);
                    if (parent is Panel panel && !panels.Contains(panel)) panels.Add(panel);
                    continue;
                }
                Walk(dep, navStyle, panels);
            }
        }
    }
}
