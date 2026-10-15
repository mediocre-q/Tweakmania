using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Tweak.Runtime;

namespace Tweak.App;

public sealed class App : Application
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--recover-legacy-hiding")
        {
            try { Console.WriteLine(HidHideService.RecoverStaleLease() ? "Recovered stale legacy HidHide lease." : "No stale legacy HidHide lease found."); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        if (args.Contains("--diagnose"))
        {
            try
            {
                using var catalog = new DeviceCatalog();
                Console.WriteLine(JsonSerializer.Serialize(new { Devices = catalog.Enumerate() }, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        var selfTest = args.Contains("--self-test-ui");
        var app = new App();
        app.DispatcherUnhandledException += (_, e) =>
        {
            if (args.Contains("--render-preview") || selfTest)
            {
                Console.Error.WriteLine(e.Exception);
                e.Handled = true;
                app.Shutdown(1);
                return;
            }
            MessageBox.Show(e.Exception.Message, "Tweakmania", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        app.Startup += async (_, _) =>
        {
            MainWindow? window = null;
            var isDiagnostic = args.Contains("--render-preview") || selfTest;
            var viewModel = new MainViewModel(() => window is null ? 0 : new WindowInteropHelper(window).Handle, !isDiagnostic);
            window = new MainWindow(viewModel);
            app.MainWindow = window;
            if (selfTest)
            {
                try
                {
#if UI_SMOKE_TESTS
                    viewModel.InitializeProfileLibraryForTest();
                    await UiSmokeTests.RunAsync(viewModel);
                    app.Shutdown(0);
#else
                    throw new InvalidOperationException("UI smoke tests require the local test source and EnableUiSmokeTests=true.");
#endif
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                    app.Shutdown(1);
                }
                return;
            }
            var renderIndex = Array.IndexOf(args, "--render-preview");
            if (renderIndex >= 0)
            {
                if (renderIndex + 1 >= args.Length) throw new ArgumentException("--render-preview requires a PNG path.");
                // Render the real WPF window offscreen without starting devices or routing.
                viewModel.SetScreenshotState();
                var content = (FrameworkElement)window.Content;
                content.Measure(new Size(1240, 850)); content.Arrange(new Rect(0, 0, 1240, 850)); content.UpdateLayout();
                await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                content.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1240, 850, 96, 96, PixelFormats.Pbgra32);
                var background = new DrawingVisual();
                using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new Rect(0, 0, 1240, 850));
                bitmap.Render(background);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var path = Path.GetFullPath(args[renderIndex + 1]); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using (var file = File.Create(path)) encoder.Save(file);
                app.Shutdown();
                return;
            }
            var shutdownStarted = false;
            var shutdownComplete = false;
            window.Closing += async (_, e) =>
            {
                if (shutdownComplete) return;
                e.Cancel = true;
                if (shutdownStarted) return;
                shutdownStarted = true;
                await viewModel.ShutdownAsync();
                await window.Dispatcher.InvokeAsync(() =>
                {
                    shutdownComplete = true;
                    window.Close();
                }, DispatcherPriority.Background);
            };
            window.Show();
            await viewModel.InitializeAsync();
        };
        return app.Run();
    }
}
