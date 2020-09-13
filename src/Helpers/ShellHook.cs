using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace DialControllerTools.Helpers
{

    internal enum WH_SHELL_MESSAGES : int
    {
        HSHELL_WINDOWCREATED = 1,
        HSHELL_WINDOWDESTROYED = 2,
        HSHELL_ACTIVATESHELLWINDOW = 3,
        HSHELL_WINDOWACTIVATED = 4,
        HSHELL_GETMINRECT = 5,
        HSHELL_REDRAW = 6,
        HSHELL_TASKMAN = 7,
        HSHELL_LANGUAGE = 8,
        HSHELL_SYSMENU = 9,
        HSHELL_ENDTASK = 10,
        HSHELL_ACCESSIBILITYSTATE = 11,
        HSHELL_APPCOMMAND = 12
    }

    internal class ShellHook
    {
        private delegate IntPtr HCallback(int nCode, IntPtr wParam, IntPtr lParam);

        private const int WH_SHELL = 10;

        private static IntPtr _hHook = IntPtr.Zero;
        private static HCallback _hookCallback = HookCallback;

        private static Dictionary<int, Action<IntPtr>> _callbacks = new Dictionary<int, Action<IntPtr>>();

        #region Dll imports
        //[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        //private static extern IntPtr SetWindowsHookEx(int idHook, HCallback lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHook(int idHook, HCallback lpfn);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        //[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        //static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        //[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        //static extern IntPtr GetTopWindow(IntPtr hWnd);

        //[DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        //private static extern IntPtr GetModuleHandle(string lpModuleName);
        #endregion


        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (_callbacks.ContainsKey(nCode))
            {
                var hWnd = wParam;
                var className = new StringBuilder(256);
                var length = GetClassName(hWnd, className, className.Capacity);

                if (length != 0)
                {
                    var classNameStr = className.ToString();

                    if (classNameStr.StartsWith("HwndWrapper"))
                    {
                        //"Hidden Window"

                        //var caption = new StringBuilder(256);
                        //length = GetWindowText(hWnd, caption, caption.Capacity);

                        _callbacks[nCode](hWnd);
                    }
                }
            }

            return CallNextHookEx(_hHook, nCode, wParam, lParam);
        }


        public bool Set(Dictionary<WH_SHELL_MESSAGES, Action<IntPtr>> messageCallbacks)
        {
            if (_hHook != IntPtr.Zero)
            {
                this.Unset();
            }

            foreach (var msCallback in messageCallbacks)
            {
                _callbacks[(int)msCallback.Key] = msCallback.Value;
            }

            //using var process = System.Diagnostics.Process.GetCurrentProcess();
            //using var currentModule = process.MainModule;

            //var hModule = GetModuleHandle(currentModule.ModuleName);

            _hHook = SetWindowsHook(WH_SHELL, _hookCallback);

            // var error = Marshal.GetLastWin32Error(); // error codes https://users.freebasic-portal.de/freebasicru/er_rorread.html

            return _hHook != IntPtr.Zero;
        }

        public bool Unset()
        {
            var success = UnhookWindowsHookEx(_hHook);
            //if (!success)
            //{
            //    Logger.Instance.Log("UnhookWindowsHookEx error.");
            //}
            _hHook = IntPtr.Zero;
            return success;
        }
    }
}
