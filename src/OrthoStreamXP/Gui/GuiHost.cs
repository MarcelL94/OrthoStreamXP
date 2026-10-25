using System.Runtime.InteropServices;

namespace OrthoStreamXP.Gui;

/// <summary>Runs the window: one at a time, on its own UI thread, without a console of its own.</summary>
internal static partial class GuiHost
{
    private const string InstanceName = @"Local\OrthoStreamXP.Window";
    private const string ActivateName = @"Local\OrthoStreamXP.Window.Activate";

    public static int Run()
    {
        DetachOwnConsole();
        using var instance = new Mutex(initiallyOwned: true, InstanceName, out bool first);
        if (!first)
        {
            // Already open: bring that window to the front instead of opening a second one.
            if (EventWaitHandle.TryOpenExisting(ActivateName, out EventWaitHandle? activate))
                using (activate) activate.Set();
            return 0;
        }

        using var activateRequests = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateName);
        int exitCode = 0;
        var ui = new Thread(() =>
        {
            var app = new App();
            app.InitializeComponent();
            app.WatchActivation(activateRequests);
            exitCode = app.Run();
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();
        ui.Join();
        Log.Close();
        return exitCode;
    }

    /// <summary>
    /// Windows before 11 24H2 ignore the manifest's "no console" setting and open a console window for a
    /// double-click; let go of it so it closes. A console the program was started from stays.
    /// </summary>
    private static void DetachOwnConsole()
    {
        if (GetConsoleWindow() == 0) return;
        uint[] processes = new uint[2];
        if (GetConsoleProcessList(processes, (uint)processes.Length) == 1) FreeConsole();
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetConsoleWindow();

    [LibraryImport("kernel32.dll")]
    private static partial uint GetConsoleProcessList([Out] uint[] processList, uint processCount);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();
}
