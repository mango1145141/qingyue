using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EpubKindleFix;

// Central spring tuning for all interactive motion.
// stiffness = 跟手速度, damping = 阻尼(越低回弹越明显)。
// 配方: 悬停明显浮起放大、按下有压缩感、松开果冻回弹(用户确认的手感)。
public static class MotionTuning
{
    public const double PressStiffness = 520, PressDamping = 22;
    public static double PressScale => !Appearance.Animate ? 1 : Appearance.Motion == "轻柔" ? 0.98 : 0.93;
    public const double ReleaseStiffness = 300;
    public static double ReleaseDamping => Appearance.Motion == "轻柔" ? 30 : 11;
    public const double HoverStiffness = 300;
    public static double HoverDamping => Appearance.Motion == "轻柔" ? 30 : 14;
    public static double HoverScale => !Appearance.Animate ? 1 : Appearance.Motion == "轻柔" ? 1.012 : 1.045;
    public static double HoverLift => !Appearance.Animate ? 0 : Appearance.Motion == "轻柔" ? -0.8 : -2.4;
    public const double ToggleStiffness = 320;
    public static double ToggleDamping => Appearance.Motion == "轻柔" ? 32 : 15;
    public const double ExpandStiffness = 210;
    public static double ExpandDamping => Appearance.Motion == "轻柔" ? 29 : 14;
    public static double PulseKick => Appearance.Motion == "轻柔" ? 0.4 : 2.4;
}

// Drives a SpringValue on CompositionTarget.Rendering so a changed target
// preserves velocity (quick hover reversal, rapid clicking) instead of
// restarting a fixed-duration animation. Stops rendering once at rest.
// Frame time comes from Stopwatch: RenderingEventArgs.RenderingTime jitters
// on some machines, which made the motion feel stuttery.
public sealed class SpringAnimator
{
    private readonly SpringValue spring;
    private readonly Action<double> apply;
    private readonly Action? onRest;
    private bool running;
    private long lastFrameTick;

    public SpringAnimator(double stiffness, double damping, double initial, Action<double> apply, Action? onRest = null)
    {
        spring = new SpringValue(stiffness, damping);
        spring.Reset(initial);
        this.apply = apply;
        this.onRest = onRest;
    }

    public double Position => spring.Position;
    public double Velocity => spring.Velocity;

    public void SetTuning(double stiffness, double damping) => spring.SetTuning(stiffness, damping);

    public void To(double target)
    {
        if (!Appearance.Animate) { Snap(target); return; }
        spring.Target = target;
        Start();
    }

    public void To(double target, double stiffness, double damping)
    {
        spring.SetTuning(stiffness, damping);
        To(target);
    }

    public void Kick(double velocity)
    {
        spring.Kick(velocity);
        Start();
    }

    // Jump straight to a value with no motion (animations disabled, initial state).
    public void Snap(double value)
    {
        Stop();
        spring.Reset(value);
        apply(value);
        onRest?.Invoke();
    }

    private void Start()
    {
        if (running) return;
        if (spring.IsAtRest)
        {
            apply(spring.Position);
            onRest?.Invoke();
            return;
        }
        running = true;
        lastFrameTick = 0;
        CompositionTarget.Rendering += OnRendering;
    }

    private void Stop()
    {
        if (!running) return;
        running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!Appearance.Animate) { Snap(spring.Target); return; }
        var now = Stopwatch.GetTimestamp();
        var dt = lastFrameTick == 0 ? 1d / 60 : (double)(now - lastFrameTick) / Stopwatch.Frequency;
        lastFrameTick = now;
        spring.Step(dt);
        apply(spring.Position);
        if (spring.IsAtRest)
        {
            Stop();
            apply(spring.Position);
            onRest?.Invoke();
        }
    }
}

public static class UiMotion
{
    private static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "MotionOffset", typeof(TranslateTransform), typeof(UiMotion));
    private static readonly DependencyProperty RevisionProperty = DependencyProperty.RegisterAttached(
        "MotionRevision", typeof(long), typeof(UiMotion), new PropertyMetadata(0L));
    private static readonly DependencyProperty PendingHideProperty = DependencyProperty.RegisterAttached(
        "PendingHide", typeof(TaskCompletionSource), typeof(UiMotion));
    private static readonly DependencyProperty PulseProperty = DependencyProperty.RegisterAttached(
        "ButtonPulse", typeof(ScaleTransform), typeof(UiMotion));
    private static readonly DependencyProperty ButtonMotionProperty = DependencyProperty.RegisterAttached(
        "ButtonMotion", typeof(ButtonMotion), typeof(UiMotion));
    private static readonly DependencyProperty SwitchMotionProperty = DependencyProperty.RegisterAttached(
        "SwitchMotion", typeof(SwitchMotion), typeof(UiMotion));
    private static readonly DependencyProperty ExpandMotionProperty = DependencyProperty.RegisterAttached(
        "ExpandMotion", typeof(ExpandMotion), typeof(UiMotion));

    // ---------- Buttons: spring press / hover / click kick ----------

    private sealed class ButtonMotion
    {
        private readonly SpringAnimator scale;
        private readonly SpringAnimator? lift;
        private bool hover;
        private bool pressed;

        public ButtonMotion(ScaleTransform scaleTransform, TranslateTransform? liftTransform)
        {
            scale = new SpringAnimator(MotionTuning.ReleaseStiffness, MotionTuning.ReleaseDamping, 1,
                v => { scaleTransform.ScaleX = v; scaleTransform.ScaleY = v; });
            if (liftTransform is not null)
                lift = new SpringAnimator(MotionTuning.HoverStiffness, MotionTuning.HoverDamping, 0,
                    v => liftTransform.Y = v);
        }

        public void SetHover(bool value)
        {
            hover = value;
            if (!value) pressed = false;
            Update(hoverTuning: true);
        }

        public void SetPressed(bool value)
        {
            pressed = value;
            Update(hoverTuning: false);
        }

        private void Update(bool hoverTuning)
        {
            if (pressed)
            {
                scale.To(MotionTuning.PressScale, MotionTuning.PressStiffness, MotionTuning.PressDamping);
                lift?.To(0);
            }
            else if (hover)
            {
                if (hoverTuning) scale.SetTuning(MotionTuning.HoverStiffness, MotionTuning.HoverDamping);
                else scale.SetTuning(MotionTuning.ReleaseStiffness, MotionTuning.ReleaseDamping);
                scale.To(MotionTuning.HoverScale);
                lift?.To(MotionTuning.HoverLift);
            }
            else
            {
                scale.To(1, MotionTuning.ReleaseStiffness, MotionTuning.ReleaseDamping);
                lift?.To(0);
            }
        }

        // Click feedback: a velocity impulse so the bounce blends with any
        // in-flight press/hover motion instead of restarting it.
        public void Pulse()
        {
            scale.SetTuning(MotionTuning.ReleaseStiffness, MotionTuning.ReleaseDamping);
            scale.Kick(MotionTuning.PulseKick);
        }
    }

    public static void AttachButton(ButtonBase button)
    {
        if (!Appearance.Animate)
        {
            ConfigureButton(button);
            return;
        }
        if (button.GetValue(ButtonMotionProperty) is ButtonMotion) return;
        button.ApplyTemplate();
        var template = button.Template;
        if (template is null || template.FindName("ButtonScale", button) is not ScaleTransform scale) return;
        var lift = template.FindName("ButtonLift", button) as TranslateTransform;
        var motion = new ButtonMotion(scale, lift);
        button.SetValue(ButtonMotionProperty, motion);
        button.MouseEnter += (_, _) => motion.SetHover(true);
        button.MouseLeave += (_, _) => motion.SetHover(false);
        button.PreviewMouseLeftButtonDown += (_, _) => motion.SetPressed(true);
        button.PreviewMouseLeftButtonUp += (_, _) => motion.SetPressed(false);
    }

    // Lazy-attach entry points. Class handlers for FrameworkElement.Loaded do
    // not reliably fire in this app (only Checked/Unchecked do), so all spring
    // wiring also happens on demand from input events.
    public static void ButtonHover(ButtonBase button, bool hover)
    {
        AttachButton(button);
        if (button.GetValue(ButtonMotionProperty) is ButtonMotion motion) motion.SetHover(hover);
    }

    public static void ButtonPress(ButtonBase button, bool pressed)
    {
        AttachButton(button);
        if (button.GetValue(ButtonMotionProperty) is ButtonMotion motion) motion.SetPressed(pressed);
    }

    public static void Pulse(ButtonBase button)
    {
        // The Switch (CheckBox) has its own spring feedback; a whole-control
        // scale pulse would fight the thumb motion.
        if (button is CheckBox) return;
        if (!Appearance.Animate || !button.IsLoaded) return;
        AttachButton(button);
        if (button.GetValue(ButtonMotionProperty) is ButtonMotion motion)
        {
            motion.Pulse();
            return;
        }
        // Fallback for templates without spring parts: legacy keyframe pulse.
        var scale = button.GetValue(PulseProperty) as ScaleTransform;
        if (scale is null)
        {
            scale = new ScaleTransform(1, 1);
            var transforms = new TransformGroup();
            if (button.RenderTransform != Transform.Identity) transforms.Children.Add(button.RenderTransform.CloneCurrentValue());
            transforms.Children.Add(scale);
            button.RenderTransform = transforms;
            button.RenderTransformOrigin = new Point(0.5, 0.5);
            button.SetValue(PulseProperty, scale);
        }
        var pulse = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(scale.ScaleX, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.038, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0.993, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(270)))
        { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(410)))
        { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse, HandoffBehavior.SnapshotAndReplace);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse, HandoffBehavior.SnapshotAndReplace);
    }

    // ---------- Switch (CheckBox style "Switch"): spring thumb + synced track color ----------

    private sealed class SwitchMotion
    {
        private static readonly Color OffColor = (Color)ColorConverter.ConvertFromString("#E1E6EC")!;
        private static readonly Color OnColor = (Color)ColorConverter.ConvertFromString("#6486A8")!;
        private const double Travel = 16;

        private readonly SpringAnimator slide;
        private readonly ScaleTransform stretch;
        private readonly SolidColorBrush trackBrush;

        public SwitchMotion(TranslateTransform slideTransform, ScaleTransform stretchTransform, Border track)
        {
            stretch = stretchTransform;
            trackBrush = track.Background as SolidColorBrush ?? new SolidColorBrush(OffColor);
            if (trackBrush.IsFrozen)
            {
                trackBrush = trackBrush.Clone();
                track.Background = trackBrush;
            }
            slide = new SpringAnimator(MotionTuning.ToggleStiffness, MotionTuning.ToggleDamping, 0, Apply);

            void Apply(double v)
            {
                slideTransform.X = v;
                // Velocity-based squash, like the demo's jelly thumb.
                stretch.ScaleX = 1 + Math.Min(Math.Abs(slide!.Velocity) * 0.0007, 0.15);
                var t = Math.Clamp(v / Travel, 0, 1);
                trackBrush.Color = Color.FromRgb(
                    (byte)(OffColor.R + (OnColor.R - OffColor.R) * t),
                    (byte)(OffColor.G + (OnColor.G - OffColor.G) * t),
                    (byte)(OffColor.B + (OnColor.B - OffColor.B) * t));
            }
        }

        public void SnapTo(bool on) => slide.Snap(on ? Travel : 0);

        public void To(bool on)
        {
            if (!Appearance.Animate) { SnapTo(on); return; }
            slide.To(on ? Travel : 0, MotionTuning.ToggleStiffness, MotionTuning.ToggleDamping);
        }
    }

    // snapToState: true on control load (initial state should not animate);
    // false when attaching lazily from a toggle, so the change animates.
    public static void AttachSwitch(CheckBox box, bool snapToState = true)
    {
        if (box.GetValue(SwitchMotionProperty) is SwitchMotion) return;
        box.ApplyTemplate();
        var template = box.Template;
        if (template is null) return;
        if (template.FindName("ThumbSlide", box) is not TranslateTransform slideT) return;
        if (template.FindName("ThumbStretch", box) is not ScaleTransform stretchT) return;
        if (template.FindName("Track", box) is not Border track) return;
        var motion = new SwitchMotion(slideT, stretchT, track);
        box.SetValue(SwitchMotionProperty, motion);
        if (snapToState) motion.SnapTo(box.IsChecked == true);
    }

    public static void SwitchChanged(CheckBox box)
    {
        if (box.GetValue(SwitchMotionProperty) is not SwitchMotion motion)
        {
            AttachSwitch(box, snapToState: false);
            if (box.GetValue(SwitchMotionProperty) is not SwitchMotion attached) return;
            motion = attached;
        }
        motion.To(box.IsChecked == true);
    }

    // Hover-time lazy attach: snaps the thumb to match IsChecked, which also
    // repairs the visual state if the switch never got attached at load.
    public static void SwitchHover(CheckBox box)
    {
        AttachSwitch(box, snapToState: true);
    }

    // ---------- Expander: spring height + arrow driven by one progress spring ----------

    private sealed class ExpandMotion
    {
        public required Border Host { get; init; }
        public required FrameworkElement Content { get; init; }
        public RotateTransform? Arrow { get; set; }
        public double OpenHeight { get; set; } = 1;
        public bool Open { get; set; }
        public SpringAnimator? Progress { get; set; }
    }

    public static void Expand(Expander expander, bool animate)
    {
        // IsExpanded can change while XAML is still constructing the control.
        // Loaded initializes the final state after the template is available.
        if (!expander.IsLoaded) return;
        expander.ApplyTemplate();
        var template = expander.Template;
        if (template is null) return;
        if (template.FindName("AnimatedContent", expander) is not Border host
            || host.Child is not FrameworkElement content) return;
        var open = expander.IsExpanded;
        host.IsHitTestVisible = open;
        System.Windows.Input.KeyboardNavigation.SetTabNavigation(host, open
            ? System.Windows.Input.KeyboardNavigationMode.Continue : System.Windows.Input.KeyboardNavigationMode.None);

        var arrow = (template.FindName("ExpandArrow", expander) as System.Windows.Shapes.Path)
            ?.RenderTransform as RotateTransform;

        var motion = expander.GetValue(ExpandMotionProperty) as ExpandMotion;
        if (motion is null || !ReferenceEquals(motion.Host, host))
        {
            motion = new ExpandMotion { Host = host, Content = content, Arrow = arrow };
            var captured = motion;
            captured.Progress = new SpringAnimator(MotionTuning.ExpandStiffness, MotionTuning.ExpandDamping, open ? 1 : 0,
                v =>
                {
                    captured.Host.Height = Math.Max(0, v * captured.OpenHeight);
                    if (captured.Arrow is not null) captured.Arrow.Angle = -90 + 90 * v;
                },
                onRest: () =>
                {
                    // Release animation rendering once content is stationary:
                    // open returns to Auto height, closed pins at 0.
                    if (captured.Open) captured.Host.ClearValue(FrameworkElement.HeightProperty);
                    else captured.Host.Height = 0;
                    if (captured.Arrow is not null) captured.Arrow.Angle = captured.Open ? 0 : -90;
                });
            expander.SetValue(ExpandMotionProperty, motion);
        }
        motion.Arrow = arrow ?? motion.Arrow;
        motion.Open = open;

        var width = Math.Max(1, expander.ActualWidth);
        content.Measure(new Size(width, double.PositiveInfinity));
        motion.OpenHeight = Math.Max(1, content.DesiredSize.Height);
        host.Focusable = false;

        if (!animate || !Appearance.Animate)
        {
            motion.Progress!.Snap(open ? 1 : 0);
            return;
        }
        // Retargeting mid-flight preserves position and velocity, so rapid
        // toggling reverses smoothly instead of restarting.
        motion.Progress!.To(open ? 1 : 0, MotionTuning.ExpandStiffness, MotionTuning.ExpandDamping);
        if (open) Reveal(content, 5);
    }

    // ---------- Reveal / Hide (unchanged keyframe motion) ----------

    public static void Reveal(FrameworkElement element, double distance = 8)
    {
        var revision = (long)element.GetValue(RevisionProperty) + 1;
        element.SetValue(RevisionProperty, revision);
        (element.GetValue(PendingHideProperty) as TaskCompletionSource)?.TrySetResult();
        element.ClearValue(PendingHideProperty);
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 1;
        if (!Appearance.Animate || !element.IsLoaded || !element.IsVisible) return;
        var offset = element.GetValue(OffsetProperty) as TranslateTransform;
        if (offset is null)
        {
            offset = new TranslateTransform();
            var transforms = new TransformGroup();
            if (element.RenderTransform != Transform.Identity) transforms.Children.Add(element.RenderTransform.CloneCurrentValue());
            transforms.Children.Add(offset);
            element.RenderTransform = transforms;
            element.SetValue(OffsetProperty, offset);
        }
        var duration = new Duration(TimeSpan.FromMilliseconds(200));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        var slide = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        slide.KeyFrames.Add(new EasingDoubleKeyFrame(distance, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        slide.KeyFrames.Add(new EasingDoubleKeyFrame(-distance * 0.16, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        slide.KeyFrames.Add(new EasingDoubleKeyFrame(distance * 0.035, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(390)))
        { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        slide.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)))
        { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        slide.Completed += (_, _) =>
        {
            if ((long)element.GetValue(RevisionProperty) != revision) return;
            // Release animation rendering once content is stationary, so text
            // resumes normal pixel-aligned rendering instead of a faded layer.
            offset.BeginAnimation(TranslateTransform.YProperty, null);
            offset.Y = 0;
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
        };
        offset.BeginAnimation(TranslateTransform.YProperty, slide);
        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    public static Task HideAsync(FrameworkElement element)
    {
        if (!Appearance.Animate || !element.IsVisible)
        {
            element.Visibility = Visibility.Collapsed;
            return Task.CompletedTask;
        }
        var revision = (long)element.GetValue(RevisionProperty) + 1;
        element.SetValue(RevisionProperty, revision);
        (element.GetValue(PendingHideProperty) as TaskCompletionSource)?.TrySetResult();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        element.SetValue(PendingHideProperty, completion);
        var fade = new DoubleAnimation(element.Opacity, 0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) =>
        {
            if ((long)element.GetValue(RevisionProperty) == revision)
            {
                element.Visibility = Visibility.Collapsed;
                element.BeginAnimation(UIElement.OpacityProperty, null);
                element.Opacity = 1;
                element.ClearValue(PendingHideProperty);
            }
            completion.TrySetResult();
        };
        element.BeginAnimation(UIElement.OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
        return completion.Task;
    }

    public static void ConfigureButton(ButtonBase button)
    {
        if (Appearance.Animate) return;
        button.ApplyTemplate();
        if (VisualTreeHelper.GetChildrenCount(button) == 0 || VisualTreeHelper.GetChild(button, 0) is not FrameworkElement root) return;
        foreach (VisualStateGroup group in VisualStateManager.GetVisualStateGroups(root))
            foreach (VisualTransition transition in group.Transitions) transition.GeneratedDuration = new Duration(TimeSpan.Zero);
    }
}
