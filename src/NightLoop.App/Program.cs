using System.IO;
using Velopack;

namespace NightLoop.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // CI: build every screen once and save a picture of each, then exit (see Views/SmokeTest.cs).
        if (args.Length == 2 && args[0] == "--smoke") return Smoke(args[1]);

        // Must run first: during install, update and uninstall Velopack starts the exe with special arguments,
        // does its work here and exits. It also installs an update downloaded last session before the window opens.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static int Smoke(string folder)
    {
        Directory.CreateDirectory(folder);
        try
        {
            VelopackApp.Build().Run();
            var app = new App();
            app.InitializeComponent();
            return Views.SmokeTest.Run(app, folder); // app.Run() is never called, so no window opens
        }
        catch (Exception ex)
        {
            // Startup itself failed (theme, App.xaml or Velopack), before any screen was tried.
            File.WriteAllText(Path.Combine(folder, "startup.error.txt"), ex.ToString());
            return 2;
        }
    }
}
