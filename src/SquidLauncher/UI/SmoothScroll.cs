using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SquidLauncher.Core;

namespace SquidLauncher.UI
{
    /// <summary>
    /// 平滑滚动：鼠标滚轮和代码跳转都走缓动，而不是瞬移。
    ///
    /// 用类级事件处理器而不是隐式样式 + 附加属性 —— 给 ScrollViewer 加隐式样式会顶掉
    /// 它在系统主题里的模板（ScrollViewer 的默认外观完全来自 theme style），
    /// 而类处理器对已有、未有的所有 ScrollViewer 都生效，不碰样式。
    ///
    /// 用法：App.OnStartup 里调一次 SmoothScroll.Init()；代码跳转用 SmoothScroll.AnimateTo()。
    /// </summary>
    public static class SmoothScroll
    {
        /// <summary>一格滚轮滚动多少像素（约三行文字）</summary>
        const double WheelStep = 96;
        /// <summary>动画时长：滚动距离越长给得越长，但有上下限（压短一点，滚起来跟手）</summary>
        static readonly TimeSpan MinDuration = TimeSpan.FromMilliseconds(90);
        static readonly TimeSpan MaxDuration = TimeSpan.FromMilliseconds(220);

        static bool _inited;

        /// <summary>排查用：OL_SCROLL_TRACE=1 时把每次滚轮的起止值打进日志</summary>
        static readonly bool Trace = Environment.GetEnvironmentVariable("OL_SCROLL_TRACE") == "1";

        /// <summary>每个滚动区「这一串滚轮还想到哪」，连滚时在此累加</summary>
        sealed class TargetBox
        {
            public double Target;      // 这一串滚轮最终想停的位置
            public double From;        // 本次动画的起点
            public bool Animating;     // 是否还有一段缓动在跑
            public DoubleAnimation Current; // 当前这一段；旧段被顶掉后它的 Completed 不许再提交（否则会把位置拽回去）
        }
        static readonly ConditionalWeakTable<ScrollViewer, TargetBox> Targets =
            new ConditionalWeakTable<ScrollViewer, TargetBox>();

        /// <summary>注册全局滚轮处理（幂等）</summary>
        public static void Init()
        {
            if (_inited) return;
            _inited = true;

            // 排查用开关：OL_SMOOTH_SCROLL=0 关掉滚轮缓动，回到系统原生逐格滚动。
            // 怀疑「卡顿」是缓动引起时，设这个变量重启对比一次就能定性。
            if (Environment.GetEnvironmentVariable("OL_SMOOTH_SCROLL") == "0")
            {
                Log.Info("[滚动] OL_SMOOTH_SCROLL=0，滚轮缓动已关闭");
                return;
            }

            EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent,
                new MouseWheelEventHandler(OnPreviewWheel), true);
        }

        /// <summary>动画驱动用的代理属性（ScrollViewer.VerticalOffset 是只读的，没法直接动画）</summary>
        static readonly DependencyProperty OffsetProperty =
            DependencyProperty.RegisterAttached("Offset", typeof(double), typeof(SmoothScroll),
                new PropertyMetadata(0.0, OnOffsetChanged));

        static void OnOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScrollViewer sv) sv.ScrollToVerticalOffset((double)e.NewValue);
        }

        /// <summary>
        /// 判断这个 ScrollViewer 能不能交给平滑滚动处理。
        ///
        /// 不能接管的三类：
        ///   1. 文本框 / 密码框的滚动宿主（PART_ContentHost）——按行滚动，有自己的语义；
        ///   2. 弹出层里的滚动区（ComboBox 下拉、右键菜单）——弹层生命周期短，动画没意义；
        ///   3. 偏移单位不是像素的滚动区——虚拟化列表默认按"条目"滚动，
        ///      逐帧 ScrollToVerticalOffset 传小数会被取整，滚动会一顿一顿甚至卡住。
        ///      本项目所有列表都设了 VirtualizingPanel.ScrollUnit=Pixel，因此会被放行。
        /// </summary>
        static bool ShouldSkip(ScrollViewer sv)
        {
            if (sv.TemplatedParent is TextBoxBase || sv.TemplatedParent is PasswordBox) return true;
            if (IsInPopup(sv)) return true;
            if (sv.CanContentScroll && ScrollUnitOf(sv) != ScrollUnit.Pixel) return true;
            return false;
        }

        /// <summary>滚动区是否位于 Popup（下拉层、右键菜单、ToolTip）里</summary>
        static bool IsInPopup(DependencyObject d)
        {
            for (var cur = d; cur != null; cur = LogicalTreeHelper.GetParent(cur) ?? VisualTreeHelper.GetParent(cur))
                if (cur is Popup) return true;
            return false;
        }

        sealed class UnitBox { public ScrollUnit Unit; public bool Resolved; }
        static readonly ConditionalWeakTable<ScrollViewer, UnitBox> UnitCache =
            new ConditionalWeakTable<ScrollViewer, UnitBox>();

        /// <summary>
        /// 查这个滚动区的偏移单位。
        ///
        /// 注意：VirtualizingPanel.ScrollUnit 是设在 **ItemsControl（如 ListBox）** 上的附加属性，
        /// 直接读 ItemsHost 面板会拿到默认值 Item（实测如此），所以必须从 ItemsControl 读。
        /// </summary>
        static ScrollUnit ScrollUnitOf(ScrollViewer sv)
        {
            var box = UnitCache.GetOrCreateValue(sv);
            if (box.Resolved) return box.Unit;
            box.Unit = ResolveScrollUnit(sv);
            box.Resolved = true;
            return box.Unit;
        }

        static ScrollUnit ResolveScrollUnit(ScrollViewer sv)
        {
            if (sv.TemplatedParent is ItemsControl owner) return VirtualizingPanel.GetScrollUnit(owner);
            for (DependencyObject cur = sv; cur != null; cur = VisualTreeHelper.GetParent(cur))
                if (cur is ItemsControl ic) return VirtualizingPanel.GetScrollUnit(ic);
            // 不是列表（内容直接是 StackPanel 之类）：偏移本来就是像素
            return ScrollUnit.Pixel;
        }

        static void OnPreviewWheel(object sender, MouseWheelEventArgs e)
        {
            var sv = (ScrollViewer)sender;
            if (ShouldSkip(sv)) return;

            // 内容没超出（不能滚），或者已经到边界还继续往外滚：交回上层，让父级滚动区接着滚
            double max = sv.ScrollableHeight;
            if (max <= 0) return;
            double current = CurrentOffset(sv);
            if ((e.Delta > 0 && current <= 0.5) || (e.Delta < 0 && current >= max - 0.5)) return;

            // 按住 Shift / Ctrl 时保留原行为（横向滚动、缩放等）
            if ((Keyboard.Modifiers & (ModifierKeys.Shift | ModifierKeys.Control)) != 0) return;

            var box = Targets.GetOrCreateValue(sv);

            // 动画期间真实偏移跑出了 [起点, 目标] 区间 → 位置是被别的东西改的
            // （拖滚动条、代码跳转、内容高度变化），这一串滚轮作废，从当前位置重新起算。
            if (box.Animating &&
                (current < Math.Min(box.From, box.Target) - 2 || current > Math.Max(box.From, box.Target) + 2))
                box.Animating = false;

            // 没有正在跑的缓动：从当前真实偏移起算。
            // 有的话：在「上一格的目标」上继续加，绝不拿当前帧位置当基准反推出更小的目标 ——
            // 那会让动画半路掉头往回走，表现就是「滚一下又弹回来」。
            if (!box.Animating) box.Target = current;

            double target = Math.Max(0, Math.Min(max, box.Target - e.Delta / 120.0 * WheelStep));
            box.From = current;
            box.Target = target;
            box.Animating = true;
            if (Trace) Log.Info(string.Format("[滚动] 格 {0,4} 当前 {1:0.0} → 目标 {2:0.0}（上限 {3:0.0}）", e.Delta, current, target, max));
            Animate(sv, target, null, box);
            e.Handled = true;
        }

        /// <summary>平滑滚动到指定偏移（超出范围会自动夹到合法区间）</summary>
        public static void AnimateTo(ScrollViewer sv, double offset, TimeSpan? duration = null)
            => Animate(sv, offset, duration, null);

        static void Animate(ScrollViewer sv, double offset, TimeSpan? duration, TargetBox box)
        {
            if (sv == null) return;
            double max = Math.Max(0, sv.ScrollableHeight);
            double from = CurrentOffset(sv);
            double to = Math.Max(0, Math.Min(max, offset));
            double distance = Math.Abs(to - from);

            // 顺序很重要：先把基础值改成当前位置，再移除旧动画。
            // 反过来的话，移除动画的瞬间属性会落回上一次的基础值，画面会跳一帧。
            sv.SetValue(OffsetProperty, from);
            sv.BeginAnimation(OffsetProperty, null);

            if (box != null) { box.From = from; box.Target = to; box.Animating = distance >= 0.5; }
            if (distance < 0.5) return;

            var span = duration ?? TimeSpan.FromMilliseconds(
                Math.Min(MaxDuration.TotalMilliseconds, MinDuration.TotalMilliseconds + distance * 0.35));

            var anim = new DoubleAnimation(from, to, new Duration(span))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                // 停在终点：动画结束后 VerticalOffset 就是 to，不会回弹
                FillBehavior = FillBehavior.HoldEnd,
            };

            // 动画跑完就撤掉时钟、把终点写回基础值。
            // 留着 HoldEnd 的时钟会一直揪住旧偏移：之后拖滚动条 / 窗口重新布局 / 切页时
            // 它会把位置又拽回去，表现就是滚动「黏住、卡一下」。
            anim.Completed += (s, o) =>
            {
                // 被后来的一段顶掉的旧动画也会触发 Completed。它提交的是过期位置，
                // 还会顺手把新动画撤掉 —— 表现就是「滚到一半往回跳」。这里直接无视。
                if (box != null && !ReferenceEquals(box.Current, anim)) return;

                // 提交真实偏移而不是 to：ScrollToVerticalOffset 每帧都会把越界值夹回合法范围，
                // to 有可能比真实可达位置大，直接写 to 再撤时钟，画面会往回跳一下（回弹）。
                double real = CurrentOffset(sv);
                sv.SetValue(OffsetProperty, real);
                sv.BeginAnimation(OffsetProperty, null);
                if (Trace) Log.Info(string.Format("[滚动] 停 {0:0.0}（请求 {1:0.0}）", real, to));
                if (box != null) { box.Target = real; box.Animating = false; box.Current = null; }
            };

            if (box != null) box.Current = anim;
            sv.BeginAnimation(OffsetProperty, anim);
        }

        /// <summary>
        /// 当前实际偏移。ScrollToVerticalOffset 每帧都会被调用，所以动画进行中 VerticalOffset
        /// 本身就是实时值，不需要再读动画属性。
        /// </summary>
        static double CurrentOffset(ScrollViewer sv) => sv.VerticalOffset;
    }
}
