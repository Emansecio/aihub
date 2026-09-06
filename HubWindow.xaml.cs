using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace AIHub;

public partial class HubWindow : Window
{
    private readonly List<AppEntry> apps;
    private readonly IAppLauncher launcher;
    private readonly Settings settings;
    private Selection selection;
    private readonly bool desktopIntegration;
    private readonly List<OrbitButton> icons = new();
    private readonly Dictionary<OrbitButton, int> previousSlots = new();
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer feedbackTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private readonly DispatcherTimer idleTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer fullScreenTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly DispatcherTimer reorderTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private OrbitButton? draggedIcon;
    private bool reordering;
    private Point reorderStart;
    private double reorderGrabOffset;
    private double reorderY;
    private int reorderAnchor;
    private int reorderSlot;
    private int reorderEdge;
    private AppEntry[]? orderBeforeDrag;
    private OrbitButton[]? iconsBeforeDrag;
    private string? selectionBeforeDrag;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private HwndSource? source;
    private bool dragging;
    private bool hotkeyRegistered;
    private long? lastLaunch;
    private int dragStartY;
    private double dragStartRatio;
    private bool pointerInside;
    private bool idleSuspended;
    private bool pulseRunning;
    private bool closed;
    private bool fileDragActive;
    private bool manuallyHidden;
    private bool hiddenForFullScreen;
    public bool IsCollapsed { get; private set; }
    private static readonly DependencyProperty RevealProperty = DependencyProperty.Register("RevealProgress", typeof(double), typeof(HubWindow), new PropertyMetadata(1.0, OnRevealChanged));
    public double RevealProgress => (double)GetValue(RevealProperty);
    public int SelectedIndex => selection.Index;

    public HubWindow(IReadOnlyList<AppEntry> apps, IAppLauncher launcher, Settings settings, bool desktopIntegration = true)
    {
        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string id in settings.AppOrder ?? new())
            if (!string.IsNullOrWhiteSpace(id)) ranks.TryAdd(id, ranks.Count);
        this.apps = apps.OrderBy(app => ranks.GetValueOrDefault(app.Id, int.MaxValue)).ToList();
        this.launcher = launcher;
        this.settings = settings;
        this.desktopIntegration = desktopIntegration;
        selection = new Selection(apps.Count, Math.Max(0, this.apps.FindIndex(a => a.Id == settings.SelectedId)));
        InitializeComponent();
        BuildIcons();
        HideButton.Click += (_, _) => HideManually();
        AllowDrop = true;
        PreviewDragEnter += OnFileDragOver;
        PreviewDragOver += OnFileDragOver;
        PreviewDragLeave += (_, _) => { fileDragActive = false; ScheduleIdle(); };
        PreviewDrop += OnFileDrop;
        DragHandle.LostMouseCapture += (_, _) => { dragging = false; ScheduleIdle(); };
        MouseEnter += (_, _) => { pointerInside = true; RegisterActivity(); };
        MouseLeave += (_, _) =>
        {
            pointerInside = false;
            idleTimer.Stop();
            if (CanIdle()) SetCollapsed(true);
        };
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (RevealProgress < 0.95) { RegisterActivity(); e.Handled = true; }
        };
        ApplySide();
        UpdateSelection(false);
        PreviewMouseWheel += (_, e) => { if (!reordering && selection.Scroll(e.Delta)) UpdateSelection(); e.Handled = true; };
        PreviewKeyDown += OnKeyDown;
        ContextMenu = CreateMenu();
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveSettings(); };
        feedbackTimer.Tick += (_, _) => { feedbackTimer.Stop(); Indicator.Opacity = 1; };
        idleTimer.Tick += (_, _) =>
        {
            idleTimer.Stop();
            if (CanIdle()) SetCollapsed(true);
        };
        IsVisibleChanged += (_, _) =>
        {
            pointerInside = IsMouseOver;
            if (IsVisible) RegisterActivity();
            else
            {
                FinishIconDrag(false);
                fileDragActive = false;
                idleTimer.Stop(); feedbackTimer.Stop();
                Indicator.Opacity = 1;
                BeginAnimation(RevealProperty, null);
                SetValue(RevealProperty, IsCollapsed ? 0.0 : 1.0);
                foreach (var icon in icons)
                {
                    icon.BeginAnimation(OrbitButton.AngleProperty, null);
                    icon.BeginAnimation(OpacityProperty, null);
                }
            }
            UpdatePulse();
        };
        SourceInitialized += (_, _) =>
        {
            // ShowInTaskbar alone does not exclude a borderless WPF window from Alt+Tab.
            // Explicit taskbar windows are used only by the visual-review test mode.
            if (!ShowInTaskbar) NativeMethods.ExcludeFromAltTab(new WindowInteropHelper(this).Handle);
            if (!desktopIntegration) return;
            source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            source.AddHook(WindowMessage);
            hotkeyRegistered = NativeMethods.RegisterHotKey(source.Handle, 1, 0x4000 | 0x0001 | 0x0002, 0x20); // Ctrl+Alt+Space, no repeat.
            SetupTray();
            Dock();
        };
        fullScreenTimer.Tick += (_, _) => CheckFullScreen();
        reorderTimer.Tick += (_, _) => AdvanceReorderEdge();
        Loaded += (_, _) =>
        {
            if (desktopIntegration) { Dock(); fullScreenTimer.Start(); CheckFullScreen(); }
            ScheduleIdle();
        };
        DpiChanged += (_, _) => { if (desktopIntegration) Dispatcher.BeginInvoke(new Action(Dock)); };
        Closed += (_, _) =>
        {
            closed = true;
            FinishIconDrag(false);
            fullScreenTimer.Stop();
            UpdatePulse();
            saveTimer.Stop(); feedbackTimer.Stop(); idleTimer.Stop();
            BeginAnimation(RevealProperty, null);
            if (desktopIntegration) SaveSettings();
            if (source != null)
            {
                if (hotkeyRegistered) NativeMethods.UnregisterHotKey(source.Handle, 1);
                source.RemoveHook(WindowMessage);
            }
            tray?.ContextMenuStrip?.Dispose();
            tray?.Dispose();
            trayIcon?.Dispose();
        };
    }

    private bool CanIdle() => !closed && IsVisible && !pointerInside && !dragging && draggedIcon == null && !fileDragActive && !idleSuspended && ContextMenu?.IsOpen != true && ErrorPanel.Visibility != Visibility.Visible;

    private void ScheduleIdle()
    {
        idleTimer.Stop();
        if (!IsCollapsed && CanIdle()) idleTimer.Start();
    }

    private void RegisterActivity()
    {
        SetCollapsed(false);
        ScheduleIdle();
    }

    private void SetCollapsed(bool collapsed)
    {
        if (IsCollapsed == collapsed) return;
        IsCollapsed = collapsed;
        double from = RevealProgress;
        double to = collapsed ? 0 : 1;
        BeginAnimation(RevealProperty, null);
        // Hold the current value until the replacement animation receives its first
        // frame; using the destination as the base causes a flash on reversal.
        SetValue(RevealProperty, SystemParameters.ClientAreaAnimation ? from : to);
        if (SystemParameters.ClientAreaAnimation)
            BeginAnimation(RevealProperty, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(collapsed ? 360 : 260))
            {
                EasingFunction = new CubicEase { EasingMode = collapsed ? EasingMode.EaseInOut : EasingMode.EaseOut }
            });
        UpdateReveal();
    }

    private static void OnRevealChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) => ((HubWindow)sender).UpdateReveal();

    private void UpdateReveal()
    {
        double reveal = Math.Clamp(RevealProgress, 0, 1);
        HubScale.CenterX = settings.RightSide ? Surface.Width : 0;
        HubScale.ScaleX = HubScale.ScaleY = 0.8788 + 0.1212 * reveal;
        HubSlide.X = (settings.RightSide ? 46.6 : -46.6) * (1 - reveal);
        CollapsedGlow.Opacity = 1 - reveal;
        double opacity = Math.Clamp((reveal - 0.25) / 0.75, 0, 1);
        OuterArc.Opacity = SelectionArc.Opacity = AppIcons.Opacity = HideButton.Opacity = Label.Opacity = opacity;
        AppIcons.IsHitTestVisible = HideButton.IsHitTestVisible = !IsCollapsed && reveal > 0.95;
        UpdatePulse();
    }

    private void UpdatePulse()
    {
        bool run = !closed && IsVisible && IsCollapsed && RevealProgress <= 0.001 && SystemParameters.ClientAreaAnimation;
        if (run == pulseRunning) return;
        pulseRunning = run;
        NeonPulse.BeginAnimation(OpacityProperty, null);
        if (!run) return;
        // Only opacity changes; keep the rim vector-based and avoid a per-frame timer.
        var pulse = new DoubleAnimation(1, 0.45, TimeSpan.FromSeconds(1.4))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(pulse, 20);
        NeonPulse.BeginAnimation(OpacityProperty, pulse);
    }

    internal bool IsReordering => reordering;

    internal void PrepareIconDrag(int index, Point dialPosition)
    {
        FinishIconDrag(false);
        if (index < 0 || index >= icons.Count || icons[index].Visibility != Visibility.Visible) return;
        draggedIcon = icons[index];
        reorderStart = Dial.TransformToAncestor(this).Transform(dialPosition);
        reorderGrabOffset = dialPosition.Y - (310 + 246 * Math.Sin(draggedIcon.Angle * Math.PI / 180));
    }

    internal void DragIconTo(Point dialPosition)
    {
        if (draggedIcon == null) return;
        bool started = !reordering;
        if (!reordering)
        {
            var delta = Dial.TransformToAncestor(this).Transform(dialPosition) - reorderStart;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            // Button owns mouse capture from its normal press handler; keep this transition independent of input dispatch.
            orderBeforeDrag = apps.ToArray();
            iconsBeforeDrag = icons.ToArray();
            selectionBeforeDrag = apps[selection.Index].Id;
            reorderAnchor = selection.Index;
            int index = icons.IndexOf(draggedIcon);
            reorderSlot = (index - reorderAnchor + apps.Count) % apps.Count;
            if (reorderSlot >= (apps.Count + 1) / 2) reorderSlot -= apps.Count;
            reordering = true;
            selection.Select(index);
            idleTimer.Stop(); saveTimer.Stop();
            Panel.SetZIndex(draggedIcon, 10);
            draggedIcon.Cursor = Cursors.SizeNS;
            draggedIcon.Effect = new System.Windows.Media.Effects.DropShadowEffect
            { Color = Color.FromRgb(196, 130, 255), BlurRadius = 12, ShadowDepth = 0, Opacity = 0.8 };
        }

        reorderY = dialPosition.Y - reorderGrabOffset;
        int targetSlot = reorderY < 112 ? -2 : reorderY < 235 ? -1 : reorderY < 386 ? 0 : 1;
        bool moved = targetSlot != reorderSlot;
        while (reorderSlot != targetSlot)
        {
            int direction = Math.Sign(targetSlot - reorderSlot);
            SwapDraggedIcon(direction);
            reorderSlot += direction;
        }
        if (started || moved) UpdateSelection();
        else draggedIcon.Angle = Math.Asin((Math.Clamp(reorderY, 65, 461) - 310) / 246) * 180 / Math.PI;
        int edge = dialPosition.X is >= 100 and <= 540 ? (reorderY <= 90 ? -1 : reorderY >= 455 ? 1 : 0) : 0;
        if (edge == reorderEdge) return;
        reorderTimer.Stop();
        reorderEdge = edge;
        if (edge != 0) reorderTimer.Start();
    }

    private void SwapDraggedIcon(int direction)
    {
        int index = icons.IndexOf(draggedIcon!);
        int next = (index + direction + apps.Count) % apps.Count;
        (apps[index], apps[next]) = (apps[next], apps[index]);
        (icons[index], icons[next]) = (icons[next], icons[index]);
        selection.Select(next);
    }

    internal void AdvanceReorderEdge()
    {
        if (!reordering || reorderEdge == 0) return;
        SwapDraggedIcon(reorderEdge);
        reorderAnchor = (reorderAnchor + reorderEdge + apps.Count) % apps.Count;
        UpdateSelection();
    }

    internal void FinishIconDrag(bool commit)
    {
        reorderTimer.Stop();
        reorderEdge = 0;
        var button = draggedIcon;
        draggedIcon = null;
        bool wasReordering = reordering;
        reordering = false;
        if (wasReordering && !commit)
        {
            apps.Clear(); apps.AddRange(orderBeforeDrag!);
            icons.Clear(); icons.AddRange(iconsBeforeDrag!);
            selection.Select(Math.Max(0, apps.FindIndex(app => app.Id == selectionBeforeDrag)));
        }
        orderBeforeDrag = null; iconsBeforeDrag = null; selectionBeforeDrag = null;
        if (!wasReordering) { ScheduleIdle(); return; } // Let Button handle capture and click for a normal press.
        Panel.SetZIndex(button!, 0);
        button!.ClearValue(CursorProperty);
        button.ClearValue(EffectProperty);
        button.ReleaseMouseCapture();
        UpdateSelection(IsVisible && !closed);
        if (commit)
        {
            settings.AppOrder = apps.Select(app => app.Id).ToList();
            saveTimer.Stop();
            SaveSettings();
        }
        ScheduleIdle();
    }

    private void BuildIcons()
    {
        icons.Clear();
        previousSlots.Clear();
        AppIcons.Children.Clear();
        for (int i = 0; i < apps.Count; i++)
        {
            FrameworkElement content = apps[i].Icon is { } icon
                ? new Image { Source = icon, Width = 60, Height = 60, Stretch = Stretch.Uniform }
                : new TextBlock { Text = apps[i].Monogram, FontSize = 54, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White };
            RenderOptions.SetBitmapScalingMode(content, BitmapScalingMode.HighQuality);
            var button = new OrbitButton { Width = 72, Height = 72, Content = content, Style = (Style)FindResource("BareButton") };
            AutomationProperties.SetName(button, $"Selecionar {apps[i].Name}");
            AutomationProperties.SetAutomationId(button, "Select_" + apps[i].Id);
            button.Click += (_, _) => { selection.Select(icons.IndexOf(button)); UpdateSelection(); };
            button.PreviewMouseLeftButtonDown += (_, e) => PrepareIconDrag(icons.IndexOf(button), e.GetPosition(Dial));
            button.PreviewMouseMove += (_, e) =>
            {
                if (draggedIcon != button) return;
                if (e.LeftButton != MouseButtonState.Pressed || !button.IsMouseCaptured) { FinishIconDrag(false); return; }
                DragIconTo(e.GetPosition(Dial));
                if (reordering) e.Handled = true;
            };
            button.PreviewMouseLeftButtonUp += (_, e) =>
            {
                bool wasReordering = reordering;
                FinishIconDrag(true);
                if (wasReordering) e.Handled = true;
            };
            button.LostMouseCapture += (_, _) => { if (draggedIcon == button && !button.IsMouseCaptured) FinishIconDrag(false); };
            icons.Add(button);
            AppIcons.Children.Add(button);
        }
    }

    private void UpdateSelection(bool animate = true)
    {
        for (int i = 0; i < icons.Count; i++)
        {
            int slot = (i - (reordering ? reorderAnchor : selection.Index) + apps.Count) % apps.Count;
            if (slot >= (apps.Count + 1) / 2) slot -= apps.Count;
            double angle = slot == -2 ? -85 : slot * 38;
            double opacity = slot == 0 ? 1 : 0.40;
            var button = icons[i];
            bool wasVisible = button.Visibility == Visibility.Visible && previousSlots.ContainsKey(button);
            // Keep the existing four visual positions. Additional shortcuts circulate
            // through them without expanding the hub or covering its close button.
            button.Visibility = slot is >= -2 and <= 1 ? Visibility.Visible : Visibility.Collapsed;
            if (button.Visibility != Visibility.Visible)
            {
                button.BeginAnimation(OrbitButton.AngleProperty, null);
                button.BeginAnimation(OpacityProperty, null);
                previousSlots[button] = slot;
                continue;
            }
            bool wraps = !wasVisible || previousSlots.TryGetValue(button, out int old) && Math.Abs(slot - old) > 1;
            double oldAngle = button.Angle;
            button.BeginAnimation(OrbitButton.AngleProperty, null);
            bool isDragged = reordering && button == draggedIcon;
            button.Angle = isDragged ? Math.Asin((Math.Clamp(reorderY, 65, 461) - 310) / 246) * 180 / Math.PI : angle;
            if (!isDragged && animate && !wraps && double.IsFinite(oldAngle) && SystemParameters.ClientAreaAnimation)
                button.BeginAnimation(OrbitButton.AngleProperty, new DoubleAnimation(oldAngle, angle, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            button.BeginAnimation(OpacityProperty, null);
            button.Opacity = isDragged ? 1 : opacity;
            if (!isDragged && animate && SystemParameters.ClientAreaAnimation)
                button.BeginAnimation(OpacityProperty, new DoubleAnimation(wraps ? 0 : Math.Min(opacity + 0.3, 1), opacity, TimeSpan.FromMilliseconds(180)));
            previousSlots[button] = slot;
        }
        AppName.Text = apps[selection.Index].Name;
        UpdateLabel();
        ErrorPanel.Visibility = Visibility.Collapsed;
        RegisterActivity();
        AutomationProperties.SetName(LaunchButton, "Abrir " + apps[selection.Index].Name);
        settings.SelectedId = apps[selection.Index].Id;
        if (desktopIntegration && !reordering && !closed) { saveTimer.Stop(); saveTimer.Start(); }
    }

    private void UpdateLabel()
    {
        AppName.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = Math.Max(233, AppName.DesiredSize.Width + 104);
        Label.Width = width;
        LabelShape.Data = Geometry.Parse(FormattableString.Invariant($"M 0,47 L 25,27 Q 32,23 32,15 Q 32,0 52,0 L {width - 23},0 Q {width},0 {width},23 L {width},71 Q {width},94 {width - 23},94 L 52,94 Q 32,94 32,79 Q 32,72 25,67 Z"));
        LabelShape.RenderTransform = settings.RightSide ? new MatrixTransform(-1, 0, 0, 1, width, 0) : Transform.Identity;
        Canvas.SetLeft(Label, settings.RightSide ? Surface.Width - 480 - width : 480);
        Canvas.SetLeft(AppName, settings.RightSide ? 35 : 69);
    }

    // Animate the angular coordinate so icons travel along the arc, never through the knob.
    private sealed class OrbitButton : Button
    {
        private readonly TranslateTransform position = new();
        public OrbitButton() => RenderTransform = position;
        public static readonly DependencyProperty AngleProperty = DependencyProperty.Register(nameof(Angle), typeof(double), typeof(OrbitButton), new PropertyMetadata(double.NaN, OnAngleChanged));
        public double Angle { get => (double)GetValue(AngleProperty); set => SetValue(AngleProperty, value); }
        private static void OnAngleChanged(DependencyObject value, DependencyPropertyChangedEventArgs e)
        {
            var button = (OrbitButton)value;
            double radians = (double)e.NewValue * Math.PI / 180;
            button.position.X = 174 + 228 * Math.Cos(radians) - 36;
            button.position.Y = 310 + 246 * Math.Sin(radians) - 36;
        }
    }

    private void LaunchClick(object sender, RoutedEventArgs e)
    {
        if (IsCollapsed || RevealProgress < 0.95) { RegisterActivity(); return; }
        RegisterActivity();
        if (lastLaunch is long previous && Stopwatch.GetElapsedTime(previous).TotalMilliseconds < 450) return;
        lastLaunch = Stopwatch.GetTimestamp();
        try
        {
            launcher.Launch(apps[selection.Index]);
            ErrorPanel.Visibility = Visibility.Collapsed;
            Indicator.Opacity = 0.55;
            feedbackTimer.Stop(); feedbackTimer.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowError(ex.Message);
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (reordering)
        {
            if (e.Key == Key.Escape) FinishIconDrag(false);
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Escape) RegisterActivity();
        switch (e.Key)
        {
            case Key.Down: case Key.Right: selection.Step(1); UpdateSelection(); e.Handled = true; break;
            case Key.Up: case Key.Left: selection.Step(-1); UpdateSelection(); e.Handled = true; break;
            case Key.Enter: case Key.Space: LaunchClick(sender, e); e.Handled = true; break;
            case Key.Escape: HideManually(); e.Handled = true; break;
        }
    }

    private ContextMenu CreateMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => { FinishIconDrag(false); idleTimer.Stop(); RegisterActivity(); };
        menu.Closed += (_, _) => ScheduleIdle();
        void Add(string text, Action action) { var item = new MenuItem { Header = text }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        Add("Lateral esquerda", () => ChangeSide(false));
        Add("Lateral direita", () => ChangeSide(true));
        Add("Centralizar na lateral", () => { settings.VerticalPosition = 0.5; Dock(); SaveSettings(); });
        var monitors = new MenuItem { Header = "Monitor" };
        foreach (var screen in Forms.Screen.AllScreens)
        {
            var captured = screen;
            var item = new MenuItem { Header = screen.DeviceName + (screen.Primary ? " (principal)" : "") };
            item.Click += (_, _) => { settings.Monitor = captured.DeviceName; Dock(); SaveSettings(); };
            monitors.Items.Add(item);
        }
        menu.Items.Add(monitors);
        menu.Items.Add(new Separator());
        Add("Adicionar atalho…", ChooseShortcut);
        if (settings.CustomShortcuts.Count > 0)
        {
            var removeMenu = new MenuItem { Header = "Remover atalho do hub" };
            foreach (var shortcut in settings.CustomShortcuts)
            {
                if (shortcut == null) continue;
                var item = new MenuItem { Header = shortcut.Name };
                item.Click += (_, _) => RemoveShortcut(shortcut.Id);
                removeMenu.Items.Add(item);
            }
            menu.Items.Add(removeMenu);
        }
        menu.Items.Add(new Separator());
        Add("Ocultar hub", HideManually);
        Add("Sair do AIHub", Close);
        return menu;
    }

    private void ChooseShortcut()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Adicionar aplicativo ao hub",
            Filter = "Aplicativos e atalhos|*.exe;*.lnk",
            CheckFileExists = true,
            DereferenceLinks = false
        };
        idleSuspended = true;
        idleTimer.Stop();
        RegisterActivity();
        try
        {
            if (dialog.ShowDialog(this) == true) AddShortcut(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        { ShowError(ex.Message); }
        finally { idleSuspended = false; ScheduleIdle(); }
    }

    private static string[] DroppedFiles(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop, false)
            || e.Data.GetData(DataFormats.FileDrop, false) is not string[] files || files.Length == 0) return Array.Empty<string>();
        return files.All(path => !string.IsNullOrWhiteSpace(path)
            && (string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))) ? files : Array.Empty<string>();
    }

    private static DragDropEffects DropEffect(DragEventArgs e) => (e.AllowedEffects & DragDropEffects.Link) != 0
        ? DragDropEffects.Link : (e.AllowedEffects & DragDropEffects.Copy);

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        try { if (DroppedFiles(e).Length > 0) e.Effects = DropEffect(e); }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException or IOException) { }
        fileDragActive = e.Effects != DragDropEffects.None;
        if (fileDragActive) idleTimer.Stop(); else ScheduleIdle();
    }

    private void OnFileDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        fileDragActive = false;
        var failures = new List<string>();
        try
        {
            var effect = DropEffect(e);
            if (effect == DragDropEffects.None) return;
            foreach (string path in DroppedFiles(e).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try { AddShortcut(path); e.Effects = effect; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception or System.Runtime.InteropServices.COMException)
                { failures.Add(Path.GetFileName(path) + ": " + ex.Message); }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or System.Runtime.InteropServices.COMException) { failures.Add(ex.Message); }
        finally
        {
            if (failures.Count > 0) ShowError("Não foi possível adicionar: " + string.Join("\n", failures.Take(3)));
            ScheduleIdle();
        }
    }

    public void AddShortcut(string path)
    {
        FinishIconDrag(false);
        path = Path.GetFullPath(path);
        int existing = apps.FindIndex(a => string.Equals(a.Target, path, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) { selection.Select(existing); UpdateSelection(); return; }
        string id = "custom-" + Guid.NewGuid().ToString("N");
        string name = Path.GetFileNameWithoutExtension(path);
        var entry = AppCatalog.FromFile(id, name, path);
        settings.CustomShortcuts.Add(new CustomShortcut(id, name, path));
        apps.Add(entry);
        selection = new Selection(apps.Count, apps.Count - 1);
        RebuildShortcuts();
    }

    public void RemoveShortcut(string id)
    {
        FinishIconDrag(false);
        if (settings.CustomShortcuts.RemoveAll(s => s?.Id == id) == 0) return;
        string selectedId = apps[selection.Index].Id;
        apps.RemoveAll(a => a.Id == id);
        selection = new Selection(apps.Count, Math.Max(0, apps.FindIndex(a => a.Id == selectedId)));
        RebuildShortcuts();
    }

    private void RebuildShortcuts()
    {
        settings.AppOrder = apps.Select(app => app.Id).ToList();
        BuildIcons();
        ApplySide();
        UpdateSelection(false);
        ContextMenu = CreateMenu();
        SaveSettings();
    }

    private void ChangeSide(bool right) { settings.RightSide = right; ApplySide(); Dock(); SaveSettings(); }

    private void ApplySide()
    {
        Dial.RenderTransformOrigin = new Point(0.5, 0.5);
        Dial.RenderTransform = new ScaleTransform(settings.RightSide ? -1 : 1, 1);
        Canvas.SetLeft(Dial, settings.RightSide ? Surface.Width - Dial.Width : 0);
        foreach (var button in icons)
            if (button.Content is FrameworkElement content) content.LayoutTransform = new ScaleTransform(settings.RightSide ? -1 : 1, 1);
        UpdateLabel();
        Canvas.SetLeft(ErrorPanel, settings.RightSide ? 24 : 480);
        UpdateReveal();
    }

    private Forms.Screen CurrentScreen() => Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == settings.Monitor) ?? Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];

    private void Dock()
    {
        if (closed || !desktopIntegration || !IsLoaded && source == null) return;
        var area = CurrentScreen().WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Round(Width * dpi.DpiScaleX);
        int height = (int)Math.Round(Height * dpi.DpiScaleY);
        int x = settings.RightSide ? area.Right - width : area.Left;
        int y = area.Top + (int)Math.Round(Math.Max(0, area.Height - height) * settings.VerticalPosition);
        NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, x, y, 0, 0, 0x0015); // no size/z-order/activation
    }

    private void DragStart(object sender, MouseButtonEventArgs e)
    {
        dragging = true; dragStartY = Forms.Cursor.Position.Y; dragStartRatio = settings.VerticalPosition;
        idleTimer.Stop();
        DragHandle.CaptureMouse(); e.Handled = true;
    }

    private void DragMove(object sender, MouseEventArgs e)
    {
        if (!dragging) return;
        var available = Math.Max(1, CurrentScreen().WorkingArea.Height - Height * VisualTreeHelper.GetDpi(this).DpiScaleY);
        settings.VerticalPosition = Math.Clamp(dragStartRatio + (Forms.Cursor.Position.Y - dragStartY) / available, 0, 1);
        Dock(); e.Handled = true;
    }

    private void DragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!dragging) return;
        dragging = false; DragHandle.ReleaseMouseCapture(); SaveSettings(); ScheduleIdle(); e.Handled = true;
    }

    private void SetupTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Opening += (_, _) => { idleSuspended = true; idleTimer.Stop(); RegisterActivity(); };
        menu.Closed += (_, _) => { idleSuspended = false; ScheduleIdle(); };
        menu.Items.Add("Mostrar / ocultar hub", null, (_, _) => Toggle());
        menu.Items.Add("Lateral esquerda", null, (_, _) => ChangeSide(false));
        menu.Items.Add("Lateral direita", null, (_, _) => ChangeSide(true));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Close());
        using (var stream = Application.GetResourceStream(new Uri("/AIHub;component/Assets/AIHub.ico", UriKind.Relative)).Stream)
        using (var icon = new System.Drawing.Icon(stream, 32, 32)) trayIcon = (System.Drawing.Icon)icon.Clone();
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = hotkeyRegistered ? "AIHub · Ctrl+Alt+Espaço" : "AIHub · clique duplo para mostrar", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => Toggle();
    }

    private void HideManually()
    {
        manuallyHidden = true;
        hiddenForFullScreen = false;
        fullScreenTimer.Stop();
        Hide();
    }

    private void ShowManually()
    {
        manuallyHidden = false;
        if (desktopIntegration) { fullScreenTimer.Start(); CheckFullScreen(); }
        if (hiddenForFullScreen) return;
        Show(); Dock(); RegisterActivity();
    }

    private void Toggle() { if (IsVisible) HideManually(); else ShowManually(); }

    internal void CheckFullScreen()
    {
        if (closed || manuallyHidden) return;
        ApplyFullScreenState(FullScreenDetector.CoversMonitor(NativeMethods.GetForegroundWindow(), new WindowInteropHelper(this).Handle, CurrentScreen().Bounds));
    }

    internal void ApplyFullScreenState(bool fullScreen)
    {
        if (closed || manuallyHidden) return;
        if (fullScreen)
        {
            hiddenForFullScreen = true;
            if (IsVisible) Hide();
        }
        else if (hiddenForFullScreen)
        {
            hiddenForFullScreen = false;
            Show(); Dock(); // ShowActivated=false preserves the foreground application's focus.
        }
    }

    private IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == 1) { Toggle(); handled = true; }
        if (msg == NativeMethods.ShowHubMessage) { ShowManually(); handled = true; }
        if (msg is 0x007E or 0x001A) Dispatcher.BeginInvoke(new Action(Dock));
        return IntPtr.Zero;
    }

    private void SaveSettings()
    {
        if (!desktopIntegration) return;
        try { settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError("Não foi possível salvar a posição: " + ex.Message); }
    }

    private void ShowError(string message) { ErrorText.Text = message; ErrorPanel.Visibility = Visibility.Visible; RegisterActivity(); }
}
