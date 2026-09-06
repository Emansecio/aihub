using AIHub;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class Program
{
    private static int checks;
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var selection = new Selection(4);
            Check(selection.Scroll(-120) && selection.Index == 1, "scroll para baixo seleciona o próximo");
            selection.Scroll(120);
            Check(selection.Index == 0, "scroll para cima seleciona o anterior");
            selection.Scroll(120);
            Check(selection.Index == 3, "seleção circular nas duas pontas");
            selection.Select(0);
            Check(!selection.Scroll(-30) && !selection.Scroll(-30) && !selection.Scroll(-30), "scroll de precisão acumula deltas");
            Check(selection.Scroll(-30) && selection.Index == 1, "quatro deltas de precisão equivalem a um passo");
            selection.Select(0); selection.Scroll(-60); selection.Scroll(60); selection.Scroll(60);
            Check(selection.Index == 3, "inversão do scroll descarta resíduo da direção anterior");
            var apps = AppCatalog.Discover();
            Check(apps.Select(a => a.Id).SequenceEqual(new[] { "codex", "cursor", "hermes", "grok", "terminal" }), "quatro IAs e Terminal disponíveis na roleta");
            Check(!apps.Single(a => a.Id == "terminal").ActivateExisting, "Terminal usa sua abertura normal, sem interceptar uma sessão existente");
            Check(apps.Take(4).All(a => a.ActivateExisting), "IAs preservam a ativação de janelas existentes");
            foreach (var entry in apps)
            {
                Check(entry.Target.StartsWith("shell:") || File.Exists(entry.Target), $"atalho disponível: {entry.Name}");
                if (entry.Executable != null) Check(File.Exists(entry.Executable), $"executável disponível: {entry.Name}");
            }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (args.Contains("--features-only"))
            {
                TestCustomShortcuts(apps);
                TestFullScreen(apps);
                TestAutoCollapse(apps);
                Console.WriteLine($"PASS: {checks} verificações.");
                return 0;
            }
            if (args.Contains("--collapse-only"))
            {
                TestAutoCollapse(apps);
                Console.WriteLine($"PASS: {checks} verificações.");
                return 0;
            }
            if (args.Contains("--visual-review"))
            {
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                var previewSettings = Settings.Load();
                var preview = new HubWindow(AppCatalog.Discover(previewSettings.CustomShortcuts), new AppLauncher(), previewSettings)
                {
                    ShowInTaskbar = true
                };
                app.Run(preview);
                return 0;
            }
            var fake = new FakeLauncher();
            var window = new HubWindow(apps, fake, new Settings(), false) { Left = 440, Top = 180 };
            window.Show(); Pump(250);
            var button = (Button)window.FindName("LaunchButton");
            Wheel(window, -120);
            Check(window.SelectedIndex == 1 && fake.Opened.Count == 0, "evento de scroll muda seleção sem abrir aplicativo");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(fake.Opened.SequenceEqual(new[] { "cursor" }), "botão central abre apenas a seleção atual");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(fake.Opened.Count == 1, "duplo clique não duplica a abertura");
            for (int i = 1; i < apps.Count; i++) Wheel(window, -120);
            Check(window.SelectedIndex == 0, "a volta completa inclui todas as opções do hub");
            var appIcons = (Canvas)window.FindName("AppIcons");
            var terminalButton = appIcons.Children.OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "Select_terminal");
            terminalButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(window.SelectedIndex == 4 && fake.Opened.Count == 1, "selecionar Terminal não executa nada");
            Pump(500);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(fake.Opened.Last() == "terminal", "botão central encaminha a abertura do Terminal");
            Wheel(window, -120);
            Check(window.SelectedIndex == 0, "scroll retorna do Terminal à primeira opção");
            for (int i = 0; i < apps.Count; i++)
            {
                Wheel(window, -120); Pump(240);
                var visible = appIcons.Children.OfType<Button>().Where(b => b.Visibility == Visibility.Visible).ToArray();
                var selectedButton = appIcons.Children.OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "Select_" + apps[window.SelectedIndex].Id);
                var dial = (Canvas)window.FindName("Dial");
                var hideButton = (Button)window.FindName("HideButton");
                var hideBounds = hideButton.TransformToAncestor(dial).TransformBounds(new Rect(hideButton.RenderSize));
                Check(visible.Length == 4 && selectedButton.Visibility == Visibility.Visible && visible.All(b => !b.TransformToAncestor(dial).TransformBounds(new Rect(b.RenderSize)).IntersectsWith(hideBounds)), $"{apps[window.SelectedIndex].Name}: quatro posições visíveis sem cobrir o botão de ocultar");
            }
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Down) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(key);
            Check(window.SelectedIndex == 1 && key.Handled, "teclado percorre as opções");
            // Check actual layered-window hit testing, not only WPF's visual tree.
            Pump(250);
            var hwnd = new WindowInteropHelper(window).Handle;
            var empty = window.PointToScreen(new Point(window.Width * 0.95, window.Height * 0.08));
            Check(WindowFromPoint(new PointI((int)empty.X, (int)empty.Y)) != hwnd, "área transparente deixa o mouse passar");
            var knob = button.PointToScreen(new Point(button.Width / 2, button.Height / 2));
            Check(WindowFromPoint(new PointI((int)knob.X, (int)knob.Y)) == hwnd, "botão circular recebe o mouse");
            Wheel(window, 120); Pump(250);
            Capture(window, "hub-preview.png");
            window.Close();

            var failing = new HubWindow(apps, new FailingLauncher(), new Settings(), false);
            failing.Show(); Pump(80);
            ((Button)failing.FindName("LaunchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(((Border)failing.FindName("ErrorPanel")).Visibility == Visibility.Visible, "falha ao abrir aparece na interface");
            failing.Close();

            var right = new HubWindow(apps, fake, new Settings { RightSide = true, SelectedId = "hermes" }, false);
            right.Show(); Pump(200);
            Check(right.SelectedIndex == 2 && ((TextBlock)right.FindName("AppName")).Text == "Hermes", "seleção salva é restaurada");
            var rightLabel = (Canvas)right.FindName("Label");
            Check(Math.Abs(Canvas.GetLeft(rightLabel) + rightLabel.Width - 420) < 0.1, "balão acompanha lateral direita mantendo a ponta junto ao arco");
            Capture(right, "hub-right-preview.png");
            right.Close();

            TestCustomShortcuts(apps);
            TestFullScreen(apps);
            TestAutoCollapse(apps);

            if (args.Contains("--launch-terminal"))
            {
                var before = TerminalSessions();
                var liveTerminal = new HubWindow(apps, new AppLauncher(), new Settings { SelectedId = "terminal" }, false);
                liveTerminal.Show(); Pump(150);
                ((Button)liveTerminal.FindName("LaunchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                bool opened = false;
                for (int i = 0; i < 30 && !opened; i++) { Pump(500); opened = TerminalSessions().Except(before).Any(); }
                Check(opened && ((Border)liveTerminal.FindName("ErrorPanel")).Visibility != Visibility.Visible, "clique real abre uma nova janela ou sessão do Terminal sem enviar comandos");
                liveTerminal.Close();
            }

            if (args.Contains("--launch-installed"))
            {
                new AppLauncher().Launch(apps[0] with { Executable = null });
                Pump(800);
                Console.WriteLine("INFO: ativação do pacote Codex aceita pelo Windows, sem caminho versionado.");
                var live = new HubWindow(apps, new AppLauncher(), new Settings(), false);
                live.Show(); Pump(200);
                for (int i = 0; i < apps.Count; i++)
                {
                    if (i > 0) Wheel(live, -120);
                    live.Activate(); Pump(100);
                    ((Button)live.FindName("LaunchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var entry = apps[i];
                    string name = entry.Executable != null ? Path.GetFileNameWithoutExtension(entry.Executable) : "ChatGPT";
                    bool found = false;
                    for (int n = 0; n < 40 && !found; n++)
                    {
                        Pump(500);
                        foreach (var process in Process.GetProcessesByName(name))
                            using (process) found |= process.MainWindowHandle != IntPtr.Zero;
                    }
                    Check(((Border)live.FindName("ErrorPanel")).Visibility != Visibility.Visible && found, $"abertura real pelo botão: {entry.Name} tem janela nativa");
                }
                live.Close();
            }
            app.Shutdown();
            Console.WriteLine($"PASS: {checks} verificações.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static void TestAutoCollapse(IReadOnlyList<AppEntry> apps)
    {
        var fake = new FakeLauncher();
        var hub = new HubWindow(apps, fake, new Settings(), false) { Left = 1000, Top = 300 };
        hub.Show(); Pump(80);
        var handle = new WindowInteropHelper(hub).Handle;
        var surface = (Canvas)hub.FindName("Surface");
        var button = (Button)hub.FindName("LaunchButton");
        var expandedArc = surface.PointToScreen(new Point(402, 310));
        var expandedSize = hub.RenderSize;
        Leave(hub);
        Check(hub.IsCollapsed, "sair com o mouse inicia o recolhimento imediatamente");
        Pump(420);
        Check(hub.RevealProgress < 0.001, "recolhimento termina suavemente em menos de meio segundo");
        Check(((FrameworkElement)hub.FindName("CollapsedGlow")).Opacity == 1, "botão recolhido apresenta contorno neon");
        Check(((Canvas)hub.FindName("Label")).Opacity == 0 && !((Canvas)hub.FindName("AppIcons")).IsHitTestVisible, "arco e legenda desaparecem e deixam de aceitar cliques");
        Check(WindowFromPoint(new PointI((int)expandedArc.X, (int)expandedArc.Y)) != handle, "área liberada pelo recolhimento deixa o mouse passar");
        var smallKnob = button.TransformToAncestor(hub).TransformBounds(new Rect(button.RenderSize));
        Check(Math.Abs(smallKnob.Width - 242 * (241.2 / 900) * 0.676 * 1.3) < 0.1 && smallKnob.Left < 2, "botão recolhido fica 30% maior, cerca de 57 pixels junto à borda esquerda");
        Capture(hub, "hub-collapsed.png");
        var neon = (Canvas)hub.FindName("NeonPulse");
        if (SystemParameters.ClientAreaAnimation)
        {
            double bright = neon.Opacity;
            Pump(800);
            Check(neon.HasAnimatedProperties && neon.Opacity < bright - 0.1, "neon pulsa suavemente quando recolhido");
        }
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(fake.Opened.Count == 0 && !hub.IsCollapsed, "primeiro clique no botão recolhido apenas revela o hub");
        Check(!neon.HasAnimatedProperties && neon.Opacity == 1, "expansão remove o relógio da pulsação");
        Pump(80);
        if (SystemParameters.ClientAreaAnimation) Check(hub.RevealProgress is > 0 and < 1, "expansão progride suavemente sem salto imediato");
        Enter(hub); Pump(3300);
        Check(!hub.IsCollapsed && hub.RevealProgress == 1 && hub.RenderSize == expandedSize, "hover mantém o hub aberto no tamanho escolhido");
        Check(((FrameworkElement)hub.FindName("CollapsedGlow")).Opacity == 0, "neon desaparece com o hub expandido");
        Leave(hub); Pump(90);
        double interrupted = hub.RevealProgress;
        Enter(hub);
        Check(!hub.IsCollapsed && Math.Abs(hub.RevealProgress - interrupted) < 0.05, "retornar durante o recolhimento reverte a animação sem salto");
        Pump(300);
        Check(hub.RevealProgress == 1, "animação interrompida termina totalmente aberta");
        Leave(hub);
        hub.ContextMenu.IsOpen = true;
        Pump(3300);
        Check(!hub.IsCollapsed, "menu aberto impede recolhimento durante o uso");
        hub.ContextMenu.IsOpen = false;
        hub.Close();

        var right = new HubWindow(apps, fake, new Settings { RightSide = true }, false) { Left = 1000, Top = 300 };
        right.Show(); Leave(right); Pump(420);
        var rightButton = (Button)right.FindName("LaunchButton");
        var rightBounds = rightButton.TransformToAncestor(right).TransformBounds(new Rect(rightButton.RenderSize));
        Check(right.IsCollapsed && rightBounds.Width is > 56 and < 58 && right.Width - rightBounds.Right < 2, "recolhimento também se ancora na borda direita");
        right.Hide();
        Check(!((Canvas)right.FindName("NeonPulse")).HasAnimatedProperties && !right.HasAnimatedProperties, "ocultar remove animações do neon e da transição");
        right.Show(); Pump(300);
        Check(!right.IsCollapsed && right.RevealProgress == 1, "reabrir o hub restaura a apresentação expandida");
        for (int i = 0; i < 20; i++) { Leave(right); Pump(15); Enter(right); Pump(15); }
        Pump(300);
        Check(right.RevealProgress == 1 && !((Canvas)right.FindName("NeonPulse")).HasAnimatedProperties, "vinte reversões rápidas terminam abertas sem pulsação residual");
        right.Close();

        using var iconStream = Application.GetResourceStream(new Uri("/AIHub;component/Assets/AIHub.ico", UriKind.Relative)).Stream;
        var decoder = new IconBitmapDecoder(iconStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Check(decoder.Frames.Select(f => f.PixelWidth).Order().SequenceEqual(new[] { 16, 24, 32, 48, 64, 128, 256 }), "ícone gerado está incorporado em sete resoluções");
    }

    private static void Enter(HubWindow hub) => hub.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
    private static void Leave(HubWindow hub) => hub.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });

    private static HashSet<string> TerminalSessions()
    {
        var sessions = new HashSet<string>();
        foreach (var name in new[] { "WindowsTerminal", "pwsh", "powershell", "cmd", "wsl" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process) sessions.Add($"{name}:{process.Id}:{process.MainWindowHandle}");
        return sessions;
    }

    private static void TestCustomShortcuts(IReadOnlyList<AppEntry> apps)
    {
        string fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "shortcut-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureDirectory);
        string shortcutPath = Path.Combine(fixtureDirectory, "Meu aplicativo de teste com nome bastante longo.lnk");
        string settingsPath = Path.Combine(fixtureDirectory, "settings.json");
        string executable = Path.Combine(AppContext.BaseDirectory, "AIHub.Tests.exe");
        object? shell = null, link = null;
        var settings = new Settings();
        var fake = new FakeLauncher();
        var hub = new HubWindow(apps, fake, settings, false);
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            link = ((dynamic)shell!).CreateShortcut(shortcutPath);
            ((dynamic)link).TargetPath = executable;
            ((dynamic)link).Arguments = "--visual-review";
            ((dynamic)link).Save();
            hub.Show(); Pump(80);
            Leave(hub); Pump(420);
            var hover = FileDrag(hub, new[] { shortcutPath }, DragDrop.PreviewDragOverEvent);
            Check(hub.AllowDrop && hover.Effects == DragDropEffects.Link, "hub recolhido aceita arraste de atalho como vínculo");
            var drop = FileDrag(hub, new[] { shortcutPath }, DragDrop.PreviewDropEvent);
            Pump(300);
            Check(drop.Effects == DragDropEffects.Link, "soltar .lnk cadastra pelo evento de drop");
            var custom = settings.CustomShortcuts.Single();
            var entry = AppCatalog.FromFile(custom.Id, custom.Name, custom.Path);
            Check(entry.Target == shortcutPath && !entry.ActivateExisting && fake.Opened.Count == 0, "adicionar .lnk preserva seu destino e argumentos sem abrir o aplicativo");
            Check(hub.SelectedIndex == apps.Count && ((Canvas)hub.FindName("Label")).Width <= 396.1, "novo atalho é selecionado e nome longo cabe no balão");
            ((Button)hub.FindName("LaunchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(fake.Opened.Single() == custom.Id, "atalho personalizado abre pelo botão central");
            hub.AddShortcut(shortcutPath);
            Check(settings.CustomShortcuts.Count == 1, "adicionar o mesmo arquivo não duplica a opção");
            FileDrag(hub, new[] { executable, executable }, DragDrop.PreviewDropEvent);
            Check(settings.CustomShortcuts.Count == 2, "executável também pode ser adicionado diretamente");
            Check(fake.Opened.Count == 1, "drop de executável não inicia aplicativo");
            var rejectedDrop = FileDrag(hub, new[] { executable, settingsPath }, DragDrop.PreviewDropEvent);
            Check(rejectedDrop.Effects == DragDropEffects.None && settings.CustomShortcuts.Count == 2, "lote com extensão não suportada é recusado sem alterar atalhos");
            var moveOnly = FileDrag(hub, new[] { executable }, DragDrop.PreviewDropEvent, DragDropEffects.Move);
            Check(moveOnly.Effects == DragDropEffects.None, "drop nunca autoriza mover ou remover o arquivo original");
            var missingDrop = FileDrag(hub, new[] { Path.Combine(fixtureDirectory, "missing.exe") }, DragDrop.PreviewDropEvent);
            Check(missingDrop.Effects == DragDropEffects.None && ((Border)hub.FindName("ErrorPanel")).Visibility == Visibility.Visible, "arquivo removido durante arraste exibe erro sem sucesso falso");
            settings.Save(settingsPath);
            var loaded = Settings.Load(settingsPath);
            var restored = AppCatalog.Discover(loaded.CustomShortcuts);
            Check(loaded.CustomShortcuts.Count == 2 && restored.Count == apps.Count + 2 && loaded.SelectedId == settings.SelectedId, "atalhos e seleção são preservados ao salvar e carregar");
            for (int i = 0; i < restored.Count; i++) Wheel(hub, -120);
            Check(hub.SelectedIndex == restored.Count - 1, "roleta com sete opções completa a volta");
            hub.RemoveShortcut(custom.Id);
            Check(settings.CustomShortcuts.Count == 1 && File.Exists(shortcutPath), "remover do hub mantém o arquivo original");
            hub.RemoveShortcut(settings.CustomShortcuts.Single().Id);
            Check(hub.SelectedIndex == 0 && settings.SelectedId == "codex", "remover seleção atual retorna à primeira opção");
            bool rejected = false;
            try { hub.AddShortcut(settingsPath); } catch (ArgumentException) { rejected = true; }
            Check(rejected && settings.CustomShortcuts.Count == 0, "arquivos que não são aplicativos ou atalhos são recusados");
            var missing = new CustomShortcut("missing", "Atalho removido", Path.Combine(fixtureDirectory, "missing.exe"));
            Check(AppCatalog.Discover(new[] { missing }).Any(a => a.Id == missing.Id), "atalho ausente continua visível para correção ou remoção");
        }
        finally
        {
            hub.Close();
            if (link != null) Marshal.FinalReleaseComObject(link);
            if (shell != null) Marshal.FinalReleaseComObject(shell);
            File.Delete(shortcutPath);
            File.Delete(settingsPath);
            Directory.Delete(fixtureDirectory);
        }
    }

    private static DragEventArgs FileDrag(HubWindow hub, string[] files, RoutedEvent routedEvent, DragDropEffects effects = DragDropEffects.Link | DragDropEffects.Copy)
    {
        var data = new DataObject(DataFormats.FileDrop, files);
        // WPF creates these arguments internally during OLE drag/drop; exercise the same routed handlers.
        var args = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, new object[] { data, DragDropKeyStates.None, effects, hub, new Point(10, 10) }, null)!;
        args.RoutedEvent = routedEvent;
        hub.RaiseEvent(args);
        return args;
    }

    private static void TestFullScreen(IReadOnlyList<AppEntry> apps)
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen!;
        var hub = new HubWindow(apps, new FakeLauncher(), new Settings { Monitor = screen.DeviceName }, false);
        var full = new Window { Title = "AIHub fullscreen test", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Background = Brushes.Black, ShowInTaskbar = false };
        try
        {
            hub.Show(); full.Show();
            var handle = new WindowInteropHelper(full).Handle;
            var hubHandle = new WindowInteropHelper(hub).Handle;
            NativeMethods.SetWindowPos(handle, IntPtr.Zero, screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height, 0x0014);
            Pump(200);
            Check(FullScreenDetector.CoversMonitor(handle, hubHandle, screen.Bounds), "janela nativa sem borda cobrindo monitor é detectada como tela cheia");
            Check(!FullScreenDetector.CoversMonitor(hubHandle, hubHandle, screen.Bounds), "detector ignora o próprio hub");
            var other = new System.Drawing.Rectangle(screen.Bounds.Right, screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height);
            Check(!FullScreenDetector.CoversMonitor(handle, hubHandle, other), "tela cheia em outro monitor não oculta hub");
            full.Activate(); Pump(100);
            bool ownsForeground = NativeMethods.GetForegroundWindow() == handle;
            if (ownsForeground) hub.CheckFullScreen();
            else
            {
                Console.WriteLine("SKIP: Windows não concedeu primeiro plano ao teste; integração com foco real não validada.");
                hub.ApplyFullScreenState(FullScreenDetector.CoversMonitor(handle, hubHandle, screen.Bounds));
            }
            Check(!hub.IsVisible && !((Canvas)hub.FindName("NeonPulse")).HasAnimatedProperties, "detecção de tela cheia oculta hub e interrompe neon");
            NativeMethods.SetWindowPos(handle, IntPtr.Zero, screen.Bounds.Left + 100, screen.Bounds.Top + 100, 640, 480, 0x0014);
            Pump(100);
            var foregroundBefore = NativeMethods.GetForegroundWindow();
            if (ownsForeground) hub.CheckFullScreen();
            else hub.ApplyFullScreenState(FullScreenDetector.CoversMonitor(handle, hubHandle, screen.Bounds));
            Check(hub.IsVisible && NativeMethods.GetForegroundWindow() == foregroundBefore, "sair da tela cheia restaura hub sem roubar foco");
            full.WindowStyle = WindowStyle.SingleBorderWindow;
            full.WindowState = WindowState.Maximized; Pump(150);
            Check(!FullScreenDetector.CoversMonitor(handle, hubHandle, screen.Bounds), "janela comum maximizada não é tratada como tela cheia");
            ((Button)hub.FindName("HideButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            hub.ApplyFullScreenState(true); hub.ApplyFullScreenState(false);
            Check(!hub.IsVisible, "ocultação manual permanece após entrar e sair de tela cheia");
            full.Hide();
            Check(!FullScreenDetector.CoversMonitor(handle, hubHandle, screen.Bounds), "janela oculta não conta como tela cheia");
        }
        finally { full.Close(); hub.Close(); }
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        checks++; Console.WriteLine("PASS: " + name);
    }
    private static void Wheel(Window window, int delta) => window.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Capture(Window window, string file)
    {
        var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(AppContext.BaseDirectory, file)); encoder.Save(output);
    }
    private sealed class FakeLauncher : IAppLauncher
    {
        public List<string> Opened { get; } = new();
        public void Launch(AppEntry entry) => Opened.Add(entry.Id);
    }
    private sealed class FailingLauncher : IAppLauncher { public void Launch(AppEntry entry) => throw new FileNotFoundException("Atalho indisponível."); }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct PointI(int X, int Y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(PointI point);
}
