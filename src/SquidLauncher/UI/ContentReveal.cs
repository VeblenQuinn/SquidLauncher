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
    /// 右侧内容区入场：卡片从下往上浮 8px 的同时淡入，依次错峰。
    /// 只动内容区的卡片（Style=CardBox 的 Border），不动 ScrollViewer 本身，也不动列表项——
    /// 版本列表有九百多项，逐个错峰会拖出几百毫秒的尾巴，滚动时还会反复触发。
    ///
    /// 与侧栏（横向 + 12 项错峰）分工：侧栏走横轴、内容走纵轴，内容比侧栏晚 60ms 起步，
    /// 看上去是「侧栏把视线带进来、内容随后落位」。整页的 180ms 淡入保留当底色。
    /// </summary>
    internal static class ContentReveal
    {
        const double ShiftY   = 8;      // 起始下沉（px），到位即 0
        const double DurationMs = 200;  // 单张时长
        const double StaggerMs  = 40;   // 相邻卡片错峰
        const double LeadMs     = 60;   // 比侧栏晚这么多起步
        const int    MaxItems   = 5;    // 首屏只做前 5 张，长页面不做长尾
        const int    DebounceMs = 300;
        const double SwapMs     = 120;  // 页内换内容：只淡入，不位移

        static readonly Dictionary<object, long> LastPlay = new Dictionary<object, long>();
        static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>进入页面时调用，排在侧栏之后。</summary>
        public static void Play(FrameworkElement page)
        {
            if (page == null) return;
            page.Dispatcher.BeginInvoke(new Action(() => PlayNow(page)), DispatcherPriority.Loaded);
        }

        /// <summary>页内换内容（DownloadPage 的 Host.Content 之类）：纯交叉淡入，不位移。</summary>
        public static void PlaySwap(FrameworkElement element)
        {
            if (element == null) return;
            element.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    element.BeginAnimation(UIElement.OpacityProperty, null);
                    element.Opacity = 0;
                    var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(SwapMs))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                        FillBehavior = FillBehavior.HoldEnd
                    };
                    fade.Completed += (s, e) =>
                    {
                        element.BeginAnimation(UIElement.OpacityProperty, null);
                        element.Opacity = 1;
                    };
                    element.BeginAnimation(UIElement.OpacityProperty, fade);
                }
                catch (Exception ex)
                {
                    Log.Warn("内容区淡入失败：" + ex.Message);
                }
            }), DispatcherPriority.Loaded);
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

                Log.Info(string.Format("内容入场：{0} 项，首项 {1:0}ms 起步，末项 {2:0}ms 起步、{3:0}ms 到位",
                    queue.Count, LeadMs, LeadMs + (queue.Count - 1) * StaggerMs,
                    LeadMs + (queue.Count - 1) * StaggerMs + DurationMs));

                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                for (int i = 0; i < queue.Count; i++)
                {
                    var el = queue[i];
                    var delay = TimeSpan.FromMilliseconds(LeadMs + i * StaggerMs);
                    var dur = TimeSpan.FromMilliseconds(DurationMs);

                    el.BeginAnimation(UIElement.OpacityProperty, null);
                    el.Opacity = 0;

                    var tt = el.RenderTransform as TranslateTransform;
                    if (tt == null)
                    {
                        tt = new TranslateTransform(0, ShiftY);
                        el.RenderTransform = tt;
                    }
                    tt.BeginAnimation(TranslateTransform.YProperty, null);
                    tt.Y = ShiftY;

                    var fade = new DoubleAnimation(0, 1, dur)
                    {
                        BeginTime = delay,
                        EasingFunction = ease,
                        FillBehavior = FillBehavior.HoldEnd
                    };
                    var rise = new DoubleAnimation(ShiftY, 0, dur)
                    {
                        BeginTime = delay,
                        EasingFunction = ease,
                        FillBehavior = FillBehavior.HoldEnd
                    };

                    var target = el;
                    var transform = tt;
                    fade.Completed += (s, e) => page.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        target.BeginAnimation(UIElement.OpacityProperty, null);
                        target.Opacity = 1;
                        transform.BeginAnimation(TranslateTransform.YProperty, null);
                        transform.Y = 0;
                    }), DispatcherPriority.Background);

                    el.BeginAnimation(UIElement.OpacityProperty, fade);
                    tt.BeginAnimation(TranslateTransform.YProperty, rise);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("内容区入场动画失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 取内容区的卡片队列：定位 Style=CardBox 的 Border，按它们所在容器的 XAML 顺序排队。
        /// 只取容器的直接子元素，所以卡片里面的东西、ScrollViewer 本身都不会被带上。
        /// </summary>
        static List<FrameworkElement> Collect(DependencyObject page)
        {
            var queue = new List<FrameworkElement>();
            var app = Application.Current;
            if (app == null) return queue;

            var cardStyle = app.TryFindResource("CardBox") as Style;
            if (cardStyle == null) return queue;

            var panels = new List<Panel>();
            Walk(page, cardStyle, panels);

            foreach (var panel in panels)
            {
                foreach (var child in panel.Children)
                {
                    if (child is Border b && ReferenceEquals(b.Style, cardStyle))
                        queue.Add(b);
                    if (queue.Count >= MaxItems) return queue;
                }
            }

            // 没有卡片的内容区（下载页那种列表页）：退一步，整块带走非侧栏的顶层容器
            if (queue.Count == 0) return CollectContentRoots(page, app);
            return queue;
        }

        /// <summary>
        /// 兜底规则：页面根容器里、「不含侧栏条目」的顶层子元素各算一个动画单元。
        /// 下载页就是 Grid → 侧栏 Border（跳过）+ Host（整块淡入上浮）。
        /// 最多 3 个，免得把什么奇怪的装饰层也拖下水。
        /// </summary>
        static List<FrameworkElement> CollectContentRoots(DependencyObject page, Application app)
        {
            var list = new List<FrameworkElement>();
            var navStyle = app.TryFindResource("SideNav") as Style;

            DependencyObject root = page;
            var uc = page as UserControl;
            if (uc != null && uc.Content is DependencyObject content) root = content;

            var panel = root as Panel;
            if (panel == null)
            {
                if (root is FrameworkElement self) list.Add(self);
                return list;
            }

            foreach (var child in panel.Children)
            {
                if (!(child is FrameworkElement el)) continue;
                if (navStyle != null && ContainsSideNav(el, navStyle)) continue;
                list.Add(el);
                if (list.Count >= 3) break;
            }
            return list;
        }

        static bool ContainsSideNav(DependencyObject node, Style navStyle)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(node))
            {
                if (child is RadioButton rb && ReferenceEquals(rb.Style, navStyle)) return true;
                if (child is DependencyObject dep && ContainsSideNav(dep, navStyle)) return true;
            }
            return false;
        }

        static void Walk(DependencyObject node, Style cardStyle, List<Panel> panels)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(node))
            {
                if (!(child is DependencyObject dep)) continue;

                if (child is Border b && ReferenceEquals(b.Style, cardStyle))
                {
                    var parent = LogicalTreeHelper.GetParent(b);
                    if (parent is Panel panel && !panels.Contains(panel)) panels.Add(panel);
                    continue;
                }
                Walk(dep, cardStyle, panels);
            }
        }
    }
}
