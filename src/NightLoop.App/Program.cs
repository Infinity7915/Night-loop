using Velopack;

namespace NightLoop.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Must run first: during install, update and uninstall Velopack starts the exe with special arguments,
        // does its work here and exits. It also installs an update downloaded last session before the window opens.
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();

        // CI: build every screen once and save a picture of each, then exit (see Views/SmokeTest.cs).
        if (args.Length == 2 && args[0] == "--smoke")
        {
            app.StartupUri = null;
            return Views.SmokeTest.Run(app, args[1]);
        }

        return app.Run();
    }
}
