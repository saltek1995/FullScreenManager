using System.Runtime.InteropServices;

namespace FullScreenManager;

internal sealed class DesktopService
{
    private static readonly Guid ClsidImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid ServiceVirtualDesktopManagerInternal = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
    private static readonly Guid ClsidVirtualDesktopManager = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
    private readonly IVirtualDesktopManagerInternal _internal;
    private readonly IApplicationViewCollection _views;
    private readonly IVirtualDesktopManager _public;

    public DesktopService()
    {
        var shellType = Type.GetTypeFromCLSID(ClsidImmersiveShell, true)!;
        var shell = (IServiceProvider10)Activator.CreateInstance(shellType)!;
        var service = ServiceVirtualDesktopManagerInternal;
        var iid = typeof(IVirtualDesktopManagerInternal).GUID;
        _internal = (IVirtualDesktopManagerInternal)shell.QueryService(ref service, ref iid);

        var viewService = typeof(IApplicationViewCollection).GUID;
        var viewIid = viewService;
        _views = (IApplicationViewCollection)shell.QueryService(ref viewService, ref viewIid);

        var managerType = Type.GetTypeFromCLSID(ClsidVirtualDesktopManager, true)!;
        _public = (IVirtualDesktopManager)Activator.CreateInstance(managerType)!;

        // Fail during initialization instead of on the first maximize if an Insider
        // build changes the internal interface again.
        if (_internal.GetCount() < 1)
            throw new InvalidOperationException("Windows не вернула список виртуальных рабочих столов.");
    }

    public Desktop Current() => new(_internal.GetCurrentDesktop());

    public Desktop Create() => new(_internal.CreateDesktop());

    public void MoveAfterPrimary(Desktop desktop)
    {
        if (_internal.GetCount() > 1) _internal.MoveDesktop(desktop.Value, 1);
    }

    public Desktop? Find(Guid id)
    {
        return GetAll().FirstOrDefault(desktop => desktop.Id == id);
    }

    public IReadOnlyList<Desktop> GetAll()
    {
        var result = new List<Desktop>();
        _internal.GetDesktops(out var desktops);
        desktops.GetCount(out var count);
        var iid = typeof(IVirtualDesktop).GUID;
        for (uint index = 0; index < count; index++)
        {
            desktops.GetAt(index, ref iid, out var item);
            var desktop = (IVirtualDesktop)item;
            result.Add(new Desktop(desktop));
        }
        return result;
    }

    public void MoveWindow(IntPtr hwnd, Desktop desktop)
    {
        var result = _views.GetViewForHwnd(hwnd, out var view);
        if (result != 0 || view is null)
            Marshal.ThrowExceptionForHR(result != 0 ? result : unchecked((int)0x80004005));
        _internal.MoveViewToDesktop(view!, desktop.Value);
    }

    public void Switch(Desktop desktop) => _internal.SwitchDesktop(desktop.Value);

    public void Remove(Desktop desktop, Desktop fallback) =>
        _internal.RemoveDesktop(desktop.Value, fallback.Value);

    public void SetName(Desktop desktop, string name)
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out var hstring));
        var managerPointer = Marshal.GetComInterfaceForObject(_internal, typeof(IVirtualDesktopManagerInternal));
        var desktopPointer = Marshal.GetComInterfaceForObject(desktop.Value, typeof(IVirtualDesktop));
        try
        {
            var vtable = Marshal.ReadIntPtr(managerPointer);
            var method = Marshal.ReadIntPtr(vtable, 16 * IntPtr.Size);
            var setName = Marshal.GetDelegateForFunctionPointer<SetDesktopNameNative>(method);
            Marshal.ThrowExceptionForHR(setName(managerPointer, desktopPointer, hstring));
        }
        finally
        {
            Marshal.Release(desktopPointer);
            Marshal.Release(managerPointer);
            WindowsDeleteString(hstring);
        }
    }

    public string GetName(Desktop desktop)
    {
        var hstring = desktop.Value.GetName();
        if (hstring == IntPtr.Zero) return "";
        var buffer = WindowsGetStringRawBuffer(hstring, out var length);
        return buffer == IntPtr.Zero || length == 0
            ? ""
            : Marshal.PtrToStringUni(buffer, checked((int)length)) ?? "";
    }

    public bool IsCurrent(Desktop desktop) => Current().Id == desktop.Id;

    public bool IsWindowOnDesktop(IntPtr hwnd, Desktop desktop)
    {
        try { return _public.GetWindowDesktopId(hwnd) == desktop.Id; }
        catch { return false; }
    }

    public void RemoveById(Guid desktopId, Guid fallbackId)
    {
        var desktop = Find(desktopId)
            ?? throw new InvalidOperationException($"Рабочий стол {desktopId} не найден.");
        var fallback = Find(fallbackId)
            ?? throw new InvalidOperationException($"Стол возврата {fallbackId} не найден.");
        if (desktop.Id == fallback.Id)
            throw new InvalidOperationException("Удаляемый стол не может быть столом возврата.");
        if (IsCurrent(desktop)) Switch(fallback);
        Remove(desktop, fallback);
        Thread.Sleep(500);
        if (Find(desktopId) is not null)
            throw new InvalidOperationException($"Windows не удалила рабочий стол {desktopId}.");
    }

    [DllImport("combase.dll")]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetDesktopNameNative(IntPtr instance, IntPtr desktop, IntPtr name);

    public static void RunSelfTest(IntPtr externalWindow)
    {
        var service = new DesktopService();
        var origin = service.Current();
        Desktop? created = null;
        using var ownedWindow = externalWindow == IntPtr.Zero ? new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(1, 1),
            Opacity = 0
        } : null;
        ownedWindow?.Show();
        var hwnd = externalWindow != IntPtr.Zero ? externalWindow : ownedWindow!.Handle;
        created = service.Create();
        try { ExecuteSelfTest(service, created, origin, hwnd); created = null; }
        finally { RemoveTestDesktop(service, created, origin); }
    }

    private static void ExecuteSelfTest(DesktopService service, Desktop created, Desktop origin, IntPtr hwnd)
    {
        service.MoveAfterPrimary(created);
        if (service.GetAll().ElementAtOrDefault(1)?.Id != created.Id)
            throw new InvalidOperationException("Не удалось поместить тестовый Space после главного стола.");
        service.SetName(created, "FullScreenManager Test");
        if (service.GetName(created) != "FullScreenManager Test")
            throw new InvalidOperationException($"Windows сохранила неверное имя рабочего стола: '{service.GetName(created)}'.");
        var found = service.Find(created.Id)
            ?? throw new InvalidOperationException("Созданный стол не найден в системной коллекции.");
        service.SetName(found, "FullScreenManager Found Test");
        service.MoveWindow(hwnd, created);
        service.MoveWindow(hwnd, origin);
        service.Remove(created, origin);
    }

    private static void RemoveTestDesktop(DesktopService service, Desktop? created, Desktop origin)
    {
        if (created is null) return;
        try { service.Remove(created, origin); }
        catch (Exception ex) { AppLogger.Error("Не удалось удалить тестовый Space", ex); }
    }

    internal sealed class Desktop
    {
        internal IVirtualDesktop Value { get; }
        internal Guid Id => Value.GetId();
        internal Desktop(IVirtualDesktop value) => Value = value;
    }
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
internal interface IVirtualDesktop
{
    [return: MarshalAs(UnmanagedType.Bool)] bool IsViewVisible(IntPtr view);
    Guid GetId();
    IntPtr GetName();
    IntPtr GetWallpaperPath();
    [return: MarshalAs(UnmanagedType.Bool)] bool IsRemote();
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("53F5CA0B-158F-4124-900C-057158060B27")]
internal interface IVirtualDesktopManagerInternal
{
    int GetCount();
    void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop);
    [return: MarshalAs(UnmanagedType.Bool)] bool CanViewMoveDesktops(IApplicationView view);
    IVirtualDesktop GetCurrentDesktop();
    void GetDesktops(out IObjectArray desktops);
    [PreserveSig] int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);
    void SwitchDesktop(IVirtualDesktop desktop);
    void SwitchDesktopAndMoveForegroundView(IVirtualDesktop desktop);
    IVirtualDesktop CreateDesktop();
    void MoveDesktop(IVirtualDesktop desktop, int index);
    void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);
    IVirtualDesktop FindDesktop(ref Guid desktopId);
    void GetDesktopSwitchIncludeExcludeViews(IVirtualDesktop desktop, out IntPtr include, out IntPtr exclude);
    void SetDesktopName(IVirtualDesktop desktop, [MarshalAs(UnmanagedType.HString)] string name);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
internal interface IObjectArray
{
    void GetCount(out uint count);
    void GetAt(uint index, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object item);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513")]
internal interface IApplicationView
{
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5")]
internal interface IApplicationViewCollection
{
    int GetViews(out IntPtr array);
    int GetViewsByZOrder(out IntPtr array);
    int GetViewsByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr array);
    int GetViewForHwnd(IntPtr hwnd, out IApplicationView view);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
internal interface IVirtualDesktopManager
{
    [return: MarshalAs(UnmanagedType.Bool)] bool IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow);
    Guid GetWindowDesktopId(IntPtr topLevelWindow);
    void MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
internal interface IServiceProvider10
{
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object QueryService(ref Guid service, ref Guid riid);
}
