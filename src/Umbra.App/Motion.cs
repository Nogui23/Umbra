using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Umbra.App;

/// <summary>Shared animation vocabulary: soft fades, rises and springs with the same curves everywhere.</summary>
public static class Motion
{
    public static readonly IEasingFunction Out = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction OutStrong = Frozen(new QuinticEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction Spring = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 });

    /// <summary>False when Windows has animations turned off; everything then jumps to its final state.</summary>
    public static bool Enabled { get; set; } = SystemParameters.ClientAreaAnimation;

    static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>An animation that holds <paramref name="from"/> during the delay, so staggered items do not flash.</summary>
    public static AnimationTimeline Tween(double from, double to, int ms, int delayMs = 0, IEasingFunction? ease = null)
    {
        ease ??= Out;
        if (delayMs <= 0)
            return new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };
        var frames = new DoubleAnimationUsingKeyFrames();
        frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs))));
        frames.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs + ms)), ease));
        return frames;
    }

    public static void Fade(UIElement element, double to, int ms, Action? done = null)
    {
        if (!Enabled || ms <= 0)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = to;
            done?.Invoke();
            return;
        }
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Out };
        if (done is not null) animation.Completed += (_, _) => done();
        element.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    /// <summary>Fades the element in while it rises into place.</summary>
    public static void Enter(FrameworkElement element, int delayMs = 0, double rise = 18, int ms = 460)
    {
        if (!Enabled)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            return;
        }
        var move = new TranslateTransform();
        element.RenderTransform = move;
        element.BeginAnimation(UIElement.OpacityProperty, Tween(0, 1, (int)(ms * 0.75), delayMs));
        move.BeginAnimation(TranslateTransform.YProperty, Tween(rise, 0, ms, delayMs, OutStrong));
    }

    /// <summary>Runs <see cref="Enter"/> on each element with a growing delay.</summary>
    public static void Stagger(IEnumerable<FrameworkElement> elements, int startMs = 0, int stepMs = 45, double rise = 18)
    {
        var delay = startMs;
        foreach (var element in elements)
        {
            if (element.Visibility != Visibility.Visible) continue;
            Enter(element, delay, rise);
            delay += stepMs;
        }
    }

    /// <summary>Scales the element in with a slight overshoot.</summary>
    public static void Pop(FrameworkElement element, double from = 0.9, int ms = 320)
    {
        if (!Enabled)
        {
            element.Opacity = 1;
            return;
        }
        var scale = new ScaleTransform(1, 1);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = scale;
        var grow = Tween(from, 1, ms, 0, Spring);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        element.BeginAnimation(UIElement.OpacityProperty, Tween(0, 1, 160));
    }

    /// <summary>A short horizontal shake, used to reject wrong input.</summary>
    public static void Shake(FrameworkElement element)
    {
        if (!Enabled) return;
        var move = new TranslateTransform();
        element.RenderTransform = move;
        var frames = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        double[] offsets = [0, -10, 9, -7, 5, -3, 0];
        for (var i = 0; i < offsets.Length; i++)
            frames.KeyFrames.Add(new EasingDoubleKeyFrame(offsets[i], KeyTime.FromPercent(i / (double)(offsets.Length - 1)), Out));
        move.BeginAnimation(TranslateTransform.XProperty, frames);
    }

    // ----- XAML: local:Motion.Enter="120" animates the element in every time it is loaded -----

    public static readonly DependencyProperty EnterProperty = DependencyProperty.RegisterAttached(
        "Enter", typeof(int), typeof(Motion), new PropertyMetadata(-1, OnEnterChanged));

    public static int GetEnter(DependencyObject d) => (int)d.GetValue(EnterProperty);
    public static void SetEnter(DependencyObject d, int value) => d.SetValue(EnterProperty, value);

    static void OnEnterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || (int)e.OldValue >= 0 || (int)e.NewValue < 0) return;
        element.Loaded += (_, _) => Enter(element, GetEnter(element));
    }
}

/// <summary>Eased mouse-wheel scrolling for every ScrollViewer that scrolls in pixels.</summary>
public static class SmoothScroll
{
    sealed class State
    {
        public double Target;
        public bool Active;
    }

    static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(SmoothScroll));

    static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(SmoothScroll),
        new PropertyMetadata(0.0, (d, e) => ((ScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));

    public static void Install() =>
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.MouseWheelEvent, new MouseWheelEventHandler(OnWheel));

    static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || !Motion.Enabled) return;
        var viewer = (ScrollViewer)sender;
        if (viewer.ScrollableHeight <= 0) return;
        // Lists that scroll item by item keep the built-in behaviour.
        if (viewer.CanContentScroll && viewer.TemplatedParent is ItemsControl owner &&
            VirtualizingPanel.GetScrollUnit(owner) != ScrollUnit.Pixel) return;

        if (viewer.GetValue(StateProperty) is not State state)
        {
            state = new State();
            viewer.SetValue(StateProperty, state);
        }
        var current = viewer.VerticalOffset;
        var target = Math.Clamp((state.Active ? state.Target : current) - e.Delta * 0.95, 0, viewer.ScrollableHeight);
        // At the edge the event bubbles so an outer scroller can take it.
        if (Math.Abs(target - current) < 0.5) return;

        state.Target = target;
        state.Active = true;
        var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(300)) { EasingFunction = Motion.Out };
        animation.Completed += (_, _) =>
        {
            if (state.Target == target) state.Active = false;
        };
        viewer.BeginAnimation(OffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
        e.Handled = true;
    }
}
