using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LanChat;

/// <summary>
/// 内置窗口的打开 / 关闭过渡动画（Material 3 共享轴风格：淡入 + 轻微缩放与上移）。
/// 通过类级事件处理统一挂到所有 Window 上，无需逐个窗口改代码。
/// </summary>
internal static class WindowAnimations
{
    private static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(190);
    private static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(140);

    /// <summary>每个窗口的动画状态，避免 Loaded/Closing 重复触发。</summary>
    private sealed class State
    {
        public bool Opened;
        public bool Closing;
        public FrameworkElement? Root;
        public ScaleTransform? Scale;
        public TranslateTransform? Translate;
    }

    private static readonly ConditionalWeakTable<Window, State> States = new();

    public static void Initialize()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window) return;
        var state = States.GetValue(window, _ => new State());
        if (state.Opened) return;
        state.Opened = true;

        PrepareTransforms(window, state);
        AnimateIn(window, state);

        // 复用同一实例的窗口（如运行日志）再次打开时重新播放动画。
        window.Closed += (_, _) =>
        {
            state.Opened = false;
            state.Closing = false;
        };

        // 聊天室窗口自带关闭流程（先通知房主再自我关闭），只做打开动画。
        if (window.GetType().Name != "ChatRoomWindow")
            window.Closing += (_, args) => AnimateOut(window, state, args);
    }

    private static void PrepareTransforms(Window window, State state)
    {
        if (window.Content is not FrameworkElement root) return;
        state.Root = root;
        var transform = new TransformGroup();
        state.Scale = new ScaleTransform(0.965, 0.965);
        state.Translate = new TranslateTransform(0, 12);
        transform.Children.Add(state.Scale);
        transform.Children.Add(state.Translate);
        root.RenderTransform = transform;
        root.RenderTransformOrigin = new Point(0.5, 0.5);
    }

    private static void AnimateIn(Window window, State state)
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        window.Opacity = 0;
        window.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, OpenDuration) { EasingFunction = easing });
        state.Scale?.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, OpenDuration) { EasingFunction = easing });
        state.Scale?.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, OpenDuration) { EasingFunction = easing });
        state.Translate?.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, OpenDuration) { EasingFunction = easing });
    }

    private static void AnimateOut(Window window, State state, CancelEventArgs args)
    {
        if (state.Closing) return;
        state.Closing = true;
        args.Cancel = true;

        var easing = new CubicEase { EasingMode = EasingMode.EaseIn };
        // 取消关闭会把模态结果重置为 false，先记住原值，动画结束后还原。
        bool? dialogResult = null;
        try { dialogResult = window.DialogResult; }
        catch (InvalidOperationException) { }
        var fade = new DoubleAnimation(0, CloseDuration) { EasingFunction = easing };
        fade.Completed += (_, _) =>
        {
            try
            {
                if (dialogResult is not null && window.DialogResult != dialogResult) window.DialogResult = dialogResult;
            }
            catch (InvalidOperationException) { }
            // 第二次进入 Closing 时 state.Closing 为 true，直接放行。
            window.Close();
        };
        window.BeginAnimation(UIElement.OpacityProperty, fade);
        state.Scale?.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.972, CloseDuration) { EasingFunction = easing });
        state.Scale?.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.972, CloseDuration) { EasingFunction = easing });
        state.Translate?.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(8, CloseDuration) { EasingFunction = easing });
    }
}
