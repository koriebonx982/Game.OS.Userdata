using Avalonia;
using Avalonia.Skia;
using System;
using System.Runtime.InteropServices;

namespace GameLauncher;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        DemoMode.DetectAndEnable(args);

        // Start dev logging as early as possible so that any startup crash is captured.
        var settings = Services.AppSettingsService.Load();
        if (settings.DevLogs)
            Services.DevLogService.Enable();

        Services.DevLogService.Log($"[Startup] OS: {RuntimeInformation.OSDescription}");
        Services.DevLogService.Log($"[Startup] Runtime: {RuntimeInformation.FrameworkDescription}");
        Services.DevLogService.Log($"[Startup] Demo mode: {DemoMode.IsEnabled}");
        Services.DevLogService.Log($"[Startup] DevLogs: {settings.DevLogs}");
        Services.DevLogService.Log($"[Startup] ShowIntroVideo: {settings.ShowIntroVideo}  IntroVideoPath: '{settings.IntroVideoPath}'");
        Services.DevLogService.Log($"[Startup] AutoUpdate: {settings.AutoUpdate}  ReadSwitchLog: {settings.ReadSwitchLog}");

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        Services.DevLogService.Log("[Startup] Application shutdown — framework lifetime ended.");
        Services.DevLogService.Disable();
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        // When running under Xvfb (headless CI / Linux screenshot mode) force software
        // rendering so the app doesn't crash when it receives pointer/keyboard events.
        // Set GAMEOS_DISABLE_GPU=1  OR  LIBGL_ALWAYS_SOFTWARE=1 to activate.
        bool forceSwRenderer =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GAMEOS_DISABLE_GPU")) ||
            Environment.GetEnvironmentVariable("LIBGL_ALWAYS_SOFTWARE") == "1" ||
            Environment.GetEnvironmentVariable("AVALONIA_USE_RASTER_RENDERER") == "1";

        var builder = AppBuilder.Configure<App>()
                                .UsePlatformDetect()
                                .WithInterFont()
                                .LogToTrace();

        if (forceSwRenderer)
        {
            builder = builder
                .With(new SkiaOptions { MaxGpuResourceSizeBytes = 0 });

            // On Linux explicitly request the X11 software framebuffer path so
            // no EGL/GLX/Vulkan context is created — preventing the native crash
            // that Xvfb triggers when an OpenGL context is invalidated by input events.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                builder = builder.With(new X11PlatformOptions
                {
                    RenderingMode = new[] { X11RenderingMode.Software },
                });
            }
        }

        return builder;
    }
}
