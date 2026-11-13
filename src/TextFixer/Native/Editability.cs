using System.Runtime.InteropServices;

namespace TextFixer.Native;

/// <summary>
/// Asks Windows UI Automation whether the focused element of a window accepts typing.
/// Returns true (editable), false (read-only) or null (unknown). Never throws and never blocks longer than the timeout.
/// </summary>
internal static class Editability
{
    private const int ControlTypeProperty = 30003, ProcessIdProperty = 30002;
    private const int IsValuePatternAvailableProperty = 30043, ValueIsReadOnlyProperty = 30046;
    private const int IsTextPatternAvailableProperty = 30040;
    private const int EditControl = 50004, DocumentControl = 50030, WindowControl = 50032, PaneControl = 50033;
    private const int MaxDepth = 4;

    internal static bool? Detect(nint window, TimeSpan timeout)
    {
        bool? result = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = DetectCore(window);
                // Chromium enables its accessibility tree on the first request and answers that one with a bare pane.
                if (result is null) { Thread.Sleep(80); result = DetectCore(window); }
            }
            catch { result = null; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return thread.Join(timeout) ? result : null;
    }

    /// <summary>One line per inspected level, for diagnostics without any text content.</summary>
    internal static string Describe(nint window)
    {
        var lines = new List<string>();
        try
        {
            Walk(window, (depth, e) =>
            {
                lines.Add($"{depth}: type={Int(e, ControlTypeProperty)} value={Bool(e, IsValuePatternAvailableProperty)} readonly={Bool(e, ValueIsReadOnlyProperty)} text={Bool(e, IsTextPatternAvailableProperty)}");
                return null;
            });
        }
        catch (Exception ex) { lines.Add("error: " + ex.GetType().Name); }
        return string.Join(Environment.NewLine, lines);
    }

    // Conservative: "read-only" only when focus sits on a read-only document or field (selected page text).
    // Content-editable regions report a plain element inside a read-only document, so they stay unknown, never read-only.
    private static bool? DetectCore(nint window) => Walk(window, (depth, e) =>
    {
        var type = Int(e, ControlTypeProperty);
        var hasValue = Bool(e, IsValuePatternAvailableProperty) == true;
        var readOnly = hasValue ? Bool(e, ValueIsReadOnlyProperty) : null;
        if (hasValue && readOnly == false) return true;
        if (depth == 0 && type == EditControl) return readOnly != true;
        if (depth == 0 && type == DocumentControl && readOnly == true) return false;
        return null; // keep walking up; Walk stops at the window or pane
    });

    private static bool? Walk(nint window, Func<int, IUIAutomationElement, bool?> visit)
    {
        GetWindowThreadProcessId(window, out var targetProcess);
        var automation = (IUIAutomation)new CUIAutomation();
        try
        {
            if (automation.GetFocusedElement(out var element) != 0 || element is null) return null;
            if (Int(element, ProcessIdProperty) != (int)targetProcess) return null;
            automation.get_ControlViewWalker(out var walker);
            for (var depth = 0; element is not null && depth < MaxDepth; depth++)
            {
                var verdict = visit(depth, element);
                if (verdict is not null) return verdict;
                var type = Int(element, ControlTypeProperty);
                if (type is WindowControl or PaneControl) return null;
                if (walker is null || walker.GetParentElement(element, out var parent) != 0) return null;
                element = parent;
            }
            return null;
        }
        finally { Marshal.FinalReleaseComObject(automation); }
    }

    private static int? Int(IUIAutomationElement e, int property) =>
        e.GetCurrentPropertyValue(property, out var value) == 0 && value is int i ? i : null;

    private static bool? Bool(IUIAutomationElement e, int property) =>
        e.GetCurrentPropertyValue(property, out var value) == 0 && value is bool b ? b : null;

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    private class CUIAutomation { }

    // Only the vtable slots up to the members used are declared; order matters.
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void GetRootElement();
        void ElementFromHandle();
        void ElementFromPoint();
        [PreserveSig] int GetFocusedElement(out IUIAutomationElement? element);
        void GetRootElementBuildCache();
        void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache();
        void GetFocusedElementBuildCache();
        void CreateTreeWalker();
        [PreserveSig] int get_ControlViewWalker(out IUIAutomationTreeWalker? walker);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId();
        void FindFirst();
        void FindAll();
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        [PreserveSig] int GetCurrentPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object? value);
    }

    [ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTreeWalker
    {
        [PreserveSig] int GetParentElement(IUIAutomationElement element, out IUIAutomationElement? parent);
    }
}
