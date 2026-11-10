using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace OrthoStreamXP.Setup;

/// <summary>
/// Starts programs through the desktop's Explorer, as a double-click there would, so they get the person's own
/// environment instead of inheriting this program's. Started by the installer's "Launch" box, this program's
/// environment is the installer's, and an X-Plane started from it left the mounted packages out of its scenery.
/// </summary>
internal static class DesktopShell
{
    private const int CsidlDesktop = 0, SwcDesktop = 8, SwfoNeedDispatch = 1, SvgioBackground = 0, SwShowNormal = 1;
    private static readonly Guid ShellWindowsClass = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid TopLevelBrowserService = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowserInterface = typeof(IShellBrowser).GUID;
    private static readonly Guid DispatchInterface = new("00020400-0000-0000-C000-000000000046");

    /// <summary>
    /// Asks the desktop's Explorer to start <paramref name="program"/>. Returns false, after logging why, when it could not
    /// (no Explorer running as the desktop); call this from an STA thread such as the window's.
    /// </summary>
    public static bool TryStart(string program, string arguments, string workingDirectory)
    {
        var pending = new List<object>();
        try
        {
            object shellWindows = Keep(pending, Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClass, throwOnError: true)!)!);
            object location = CsidlDesktop;
            object? root = null; // an empty VARIANT
            object? desktop = ((IShellWindows)shellWindows).FindWindowSW(ref location, ref root, SwcDesktop, out _, SwfoNeedDispatch);
            if (desktop is null)
            {
                Log.Warning("The desktop's Explorer is not running; X-Plane is started directly");
                return false;
            }
            Keep(pending, desktop);
            Guid service = TopLevelBrowserService, shellBrowser = ShellBrowserInterface, dispatch = DispatchInterface;
            var browser = (IShellBrowser)Keep(pending, ((IServiceProvider)desktop).QueryService(ref service, ref shellBrowser));
            IShellView view = browser.QueryActiveShellView();
            Keep(pending, view);
            // The desktop view's Shell.Application lives in Explorer, so its ShellExecute starts the program from there.
            dynamic folderView = Keep(pending, view.GetItemObject(SvgioBackground, ref dispatch));
            dynamic application = Keep(pending, folderView.Application);
            application.ShellExecute(program, arguments, workingDirectory, "", SwShowNormal);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or RuntimeBinderException)
        {
            Log.Warning($"The desktop's Explorer could not start {program} ({ex.Message}); it is started directly");
            return false;
        }
        finally
        {
            foreach (object comObject in pending)
                if (Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
        }
    }

    private static object Keep(List<object> pending, object comObject)
    {
        pending.Add(comObject);
        return comObject;
    }

    // Only the methods used are called; the Slot methods keep the others' places in the interfaces' tables.
#pragma warning disable CA1707, IDE1006 // the slot names mark unused table entries
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        void Slot_Count();
        void Slot_Item();
        void Slot_NewEnum();
        void Slot_Register();
        void Slot_RegisterPending();
        void Slot_Revoke();
        void Slot_OnNavigate();
        void Slot_OnActivated();

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object? FindWindowSW(ref object location, ref object? root, int windowClass, out int window, int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid service, ref Guid interfaceId);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void Slot_GetWindow();
        void Slot_ContextSensitiveHelp();
        void Slot_InsertMenusSB();
        void Slot_SetMenuSB();
        void Slot_RemoveMenusSB();
        void Slot_SetStatusTextSB();
        void Slot_EnableModelessSB();
        void Slot_TranslateAcceleratorSB();
        void Slot_BrowseObject();
        void Slot_GetViewStateStream();
        void Slot_GetControlWindow();
        void Slot_SendControlMsg();

        IShellView QueryActiveShellView();
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void Slot_GetWindow();
        void Slot_ContextSensitiveHelp();
        void Slot_TranslateAccelerator();
        void Slot_EnableModeless();
        void Slot_UIActivate();
        void Slot_Refresh();
        void Slot_CreateViewWindow();
        void Slot_DestroyViewWindow();
        void Slot_GetCurrentInfo();
        void Slot_AddPropertySheetPages();
        void Slot_SaveViewState();
        void Slot_SelectItem();

        [return: MarshalAs(UnmanagedType.IUnknown)]
        object GetItemObject(int item, ref Guid interfaceId);
    }
#pragma warning restore CA1707, IDE1006
}
