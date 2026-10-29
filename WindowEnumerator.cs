using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace YAHCW
{
    public class WindowItem : IDisposable
    {
        public IntPtr Handle { get; set; }
        public string Title { get; set; }
        public string ProcessName { get; set; }
        public uint ProcessId { get; set; }
        public Icon AppIcon { get; set; }
        public RECT Bounds { get; set; }
        public bool IsMinimized { get; set; }
        public IntPtr ThumbnailId { get; set; }
        public bool OwnsIcon { get; set; }

        public WindowItem()
        {
            Handle = IntPtr.Zero;
            Title = string.Empty;
            ProcessName = string.Empty;
            AppIcon = null;
            Bounds = new RECT();
            IsMinimized = false;
            ThumbnailId = IntPtr.Zero;
            OwnsIcon = false;
        }

        public void Dispose()
        {
            if (ThumbnailId != IntPtr.Zero)
            {
                try
                {
                    NativeMethods.DwmUnregisterThumbnail(ThumbnailId);
                }
                catch { }
                ThumbnailId = IntPtr.Zero;
            }

            if (OwnsIcon && AppIcon != null)
            {
                try
                {
                    AppIcon.Dispose();
                }
                catch { }
                AppIcon = null;
            }
        }
    }

    public static class WindowEnumerator
    {
        private static readonly IVirtualDesktopManager _vdm = NativeMethods.CreateVirtualDesktopManager();

        public static List<WindowItem> EnumerateEligibleWindows(IntPtr excludeHwnd, int maxCount)
        {
            List<WindowItem> results = new List<WindowItem>();

            NativeMethods.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (results.Count >= maxCount)
                {
                    return false; // Stop enumeration
                }

                if (hWnd == excludeHwnd || hWnd == IntPtr.Zero)
                {
                    return true;
                }

                if (!NativeMethods.IsWindowVisible(hWnd))
                {
                    return true;
                }

                int len = NativeMethods.GetWindowTextLength(hWnd);
                if (len <= 0)
                {
                    return true;
                }

                StringBuilder sbTitle = new StringBuilder(len + 1);
                NativeMethods.GetWindowText(hWnd, sbTitle, sbTitle.Capacity);
                string title = sbTitle.ToString().Trim();
                if (string.IsNullOrEmpty(title))
                {
                    return true;
                }

                StringBuilder sbClass = new StringBuilder(256);
                NativeMethods.GetClassName(hWnd, sbClass, sbClass.Capacity);
                string className = sbClass.ToString();

                // Skip known shell / desktop / overlay windows
                if (className == "Progman" ||
                    className == "WorkerW" ||
                    className == "Shell_TrayWnd" ||
                    className == "Shell_SecondaryTrayWnd" ||
                    className == "ForegroundStaging" ||
                    className == "MultitaskingViewFrame" ||
                    className == "Windows.UI.Core.CoreWindow" && title == "Windows Shell Experience Host")
                {
                    return true;
                }

                // Check styles
                long exStyle = (long)NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE);
                bool isAppWindow = (exStyle & NativeMethods.WS_EX_APPWINDOW) != 0;
                bool isToolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;

                if (isToolWindow && !isAppWindow)
                {
                    return true;
                }

                IntPtr owner = NativeMethods.GetWindow(hWnd, NativeMethods.GW_OWNER);
                if (owner != IntPtr.Zero && !isAppWindow)
                {
                    return true;
                }

                // Check cloaked (virtual desktops / suspended UWP)
                int cloaked = 0;
                int hr = NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED, out cloaked, sizeof(int));
                if (hr == 0 && cloaked != 0)
                {
                    return true;
                }

                // Check virtual desktop COM if available
                if (_vdm != null)
                {
                    try
                    {
                        bool onCurrent;
                        int hrVdm = _vdm.IsWindowOnCurrentVirtualDesktop(hWnd, out onCurrent);
                        if (hrVdm == 0 && !onCurrent)
                        {
                            return true;
                        }
                    }
                    catch
                    {
                        // Ignore COM errors
                    }
                }

                RECT rc;
                NativeMethods.GetWindowRect(hWnd, out rc);
                bool isMinimized = NativeMethods.IsIconic(hWnd);
                if (!isMinimized && (rc.Width <= 0 || rc.Height <= 0))
                {
                    return true;
                }

                uint pid;
                NativeMethods.GetWindowThreadProcessId(hWnd, out pid);
                string procName = "Aplicativo";
                string exePath = null;
                if (pid != 0)
                {
                    try
                    {
                        using (Process p = Process.GetProcessById((int)pid))
                        {
                            procName = p.ProcessName;
                            try
                            {
                                exePath = p.MainModule.FileName;
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                bool ownsIcon;
                Icon appIcon = ExtractWindowIcon(hWnd, exePath, out ownsIcon);

                WindowItem item = new WindowItem();
                item.Handle = hWnd;
                item.Title = title;
                item.ProcessName = procName;
                item.ProcessId = pid;
                item.AppIcon = appIcon;
                item.Bounds = rc;
                item.IsMinimized = isMinimized;
                item.OwnsIcon = ownsIcon;

                results.Add(item);
                return true;
            }, IntPtr.Zero);

            return results;
        }

        private static Icon ExtractWindowIcon(IntPtr hWnd, string exePath, out bool ownsIcon)
        {
            ownsIcon = false;
            try
            {
                IntPtr hIcon = IntPtr.Zero;
                IntPtr result;

                // Try WM_GETICON with timeout (50ms) to prevent hang on unresponsive processes
                if (NativeMethods.SendMessageTimeout(hWnd, NativeMethods.WM_GETICON, new IntPtr(NativeMethods.ICON_BIG), IntPtr.Zero, 2, 50, out result) != IntPtr.Zero && result != IntPtr.Zero)
                {
                    hIcon = result;
                }
                else if (NativeMethods.SendMessageTimeout(hWnd, NativeMethods.WM_GETICON, new IntPtr(NativeMethods.ICON_SMALL2), IntPtr.Zero, 2, 50, out result) != IntPtr.Zero && result != IntPtr.Zero)
                {
                    hIcon = result;
                }
                else if (NativeMethods.SendMessageTimeout(hWnd, NativeMethods.WM_GETICON, new IntPtr(NativeMethods.ICON_SMALL), IntPtr.Zero, 2, 50, out result) != IntPtr.Zero && result != IntPtr.Zero)
                {
                    hIcon = result;
                }

                if (hIcon == IntPtr.Zero)
                {
                    hIcon = NativeMethods.GetClassLongPtr(hWnd, NativeMethods.GCLP_HICON);
                    if (hIcon == IntPtr.Zero)
                    {
                        hIcon = NativeMethods.GetClassLongPtr(hWnd, NativeMethods.GCLP_HICONSM);
                    }
                }

                if (hIcon != IntPtr.Zero)
                {
                    Icon ico = Icon.FromHandle(hIcon);
                    // Clone to detach from native handle
                    Icon cloned = (Icon)ico.Clone();
                    ownsIcon = true;
                    return cloned;
                }

                if (!string.IsNullOrEmpty(exePath))
                {
                    IntPtr[] large = new IntPtr[1];
                    IntPtr[] small = new IntPtr[1];
                    uint count = NativeMethods.ExtractIconEx(exePath, 0, large, small, 1);
                    if (count > 0 && large[0] != IntPtr.Zero)
                    {
                        Icon ico = Icon.FromHandle(large[0]);
                        Icon cloned = (Icon)ico.Clone();
                        NativeMethods.DestroyIcon(large[0]);
                        if (small[0] != IntPtr.Zero) NativeMethods.DestroyIcon(small[0]);
                        ownsIcon = true;
                        return cloned;
                    }
                }
            }
            catch
            {
                // Fallback below
            }

            return SystemIcons.Application;
        }
    }
}
