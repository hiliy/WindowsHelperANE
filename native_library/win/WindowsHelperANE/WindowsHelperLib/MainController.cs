﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using FreSharp;
using static WindowsHelperLib.ShowWindowCommands;
using FREObject = System.IntPtr;
using FREContext = System.IntPtr;
using Hwnd = System.IntPtr;

namespace WindowsHelperLib {
    public class MainController : FreSharpController {
        private Hwnd _airWindow;
        private Hwnd _foundWindow;
        private readonly Dictionary<string, DisplayDevice> _displayDeviceMap = new Dictionary<string, DisplayDevice>();
        private bool _isHotKeyManagerRegistered;

        public string[] GetFunctions() {
            FunctionsDict =
                new Dictionary<string, Func<FREObject, uint, FREObject[], FREObject>>
                {
                    {"init", InitController},
                    {"findWindowByTitle", FindWindowByTitle},
                    {"showWindow", ShowWindow},
                    {"hideWindow", HideWindow},
                    {"setForegroundWindow", SetForegroundWindow},
                    {"getDisplayDevices", GetDisplayDevices},
                    {"setDisplayResolution", SetDisplayResolution},
                    {"restartApp", RestartApp},
                    {"registerHotKey", RegisterHotKey},
                    {"unregisterHotKey", UnregisterHotKey},
                };

            return FunctionsDict.Select(kvp => kvp.Key).ToArray();
        }

        public FREObject InitController(FREContext ctx, uint argc, FREObject[] argv) {
            _airWindow = Process.GetCurrentProcess().MainWindowHandle;
            return FREObject.Zero;
        }

        private static void HotKeyManager_HotKeyPressed(object sender, HotKeyEventArgs e) {
            var key = (int) e.Key;
            var modifier = (int)e.Modifiers;
            var sf = $"{{\"key\": { key}, \"modifier\": { modifier}}}";
            FreHelper.DispatchEvent("ON_HOT_KEY", sf);
            /*
             Alt = 1,
        Control = 2,
        Shift = 4,
        Windows = 8,
        NoRepeat = 0x4000
            */
        }

        public FREObject RegisterHotKey(FREContext ctx, uint argc, FREObject[] argv) {
            var key = new FreObjectSharp(argv[0]).GetAsInt();
            var modifier = new FreObjectSharp(argv[1]).GetAsInt();
            var id = HotKeyManager.RegisterHotKey((Keys)key, (KeyModifiers)modifier);
            if (!_isHotKeyManagerRegistered) {
                HotKeyManager.HotKeyPressed += HotKeyManager_HotKeyPressed;
            }
            _isHotKeyManagerRegistered = true;
            return new FreObjectSharp(id).Get();
        }

        public FREObject UnregisterHotKey(FREContext ctx, uint argc, FREObject[] argv) {
            var id = new FreObjectSharp(argv[0]).GetAsInt();
            HotKeyManager.UnregisterHotKey(id);
            return FREObject.Zero;
        }
        

        public FREObject FindWindowByTitle(FREContext ctx, uint argc, FREObject[] argv) {
            var searchTerm = new FreObjectSharp(argv[0]).GetAsString();
            // ReSharper disable once SuggestVarOrType_SimpleTypes
            foreach (var pList in Process.GetProcesses()) {
                if (!pList.MainWindowTitle.Contains(searchTerm)) continue;
                _foundWindow = pList.MainWindowHandle;
                return new FreObjectSharp(pList.MainWindowTitle).Get();
            }
            return FREObject.Zero;
        }

        public FREObject ShowWindow(FREContext ctx, uint argc, FREObject[] argv) {
            var maximise = new FreObjectSharp(argv[0]).GetAsBool();
            if (WinApi.IsWindow(_foundWindow)) {
                WinApi.ShowWindow(_foundWindow, maximise ? SW_SHOWMAXIMIZED : SW_RESTORE);
            }
            return FREObject.Zero;
        }

        public FREObject HideWindow(FREContext ctx, uint argc, FREObject[] argv) {
            if (WinApi.IsWindow(_foundWindow)) {
                WinApi.ShowWindow(_foundWindow, SW_HIDE);
            }
            return FREObject.Zero;
        }

        public FREObject SetForegroundWindow(FREContext ctx, uint argc, FREObject[] argv) {
            if (WinApi.IsWindow(_foundWindow)) {
                WinApi.SetForegroundWindow(_foundWindow);
            }
            return FREObject.Zero;
        }

        private struct DisplaySettings {
            public int Width;
            public int Height;
            public int BitDepth;
            public int RefreshRate;
        }

        private static bool HasDisplaySetting(IEnumerable<DisplaySettings> availableDisplaySettings, DisplaySettings check) {
            return availableDisplaySettings.Any(item => item.Width == check.Width
            && item.BitDepth == check.BitDepth && item.Height == check.Height
            && item.RefreshRate == check.RefreshRate);
        }

        public FREObject GetDisplayDevices(FREContext ctx, uint argc, FREObject[] argv) {
            var tmp = new FreObjectSharp("Vector.<com.tuarua.DisplayDevice>", null);
            var vecDisplayDevices = new FreArraySharp(tmp.Get());

            var dd = new DisplayDevice();
            dd.cb = Marshal.SizeOf(dd);

            _displayDeviceMap.Clear();

            try {
                uint index = 0;
                uint cnt = 0;
                while (WinApi.EnumDisplayDevices(null, index++, ref dd, 0)) {
                    var displayDevice = new FreObjectSharp("com.tuarua.DisplayDevice", null);
                    var displayMonitor = new FreObjectSharp("com.tuarua.Monitor", null);

                    displayDevice.SetProperty("isPrimary", dd.StateFlags.HasFlag(DisplayDeviceStateFlags.PrimaryDevice));
                    displayDevice.SetProperty("isActive", dd.StateFlags.HasFlag(DisplayDeviceStateFlags.AttachedToDesktop));
                    displayDevice.SetProperty("isRemovable", dd.StateFlags.HasFlag(DisplayDeviceStateFlags.Removable));
                    displayDevice.SetProperty("isVgaCampatible", dd.StateFlags.HasFlag(DisplayDeviceStateFlags.VgaCompatible));

                    var monitor = new DisplayDevice();
                    monitor.cb = Marshal.SizeOf(monitor);

                    if (!WinApi.EnumDisplayDevices(dd.DeviceName, index - 1, ref monitor, 0)) {
                        continue;
                    }

                    var dm = new Devmode();
                    dm.dmSize = (short)Marshal.SizeOf(dm);
                    if (WinApi.EnumDisplaySettings(dd.DeviceName, WinApi.EnumCurrentSettings, ref dm) == 0) {
                        continue;
                    }

                    var availdm = new Devmode();
                    availdm.dmSize = (short)Marshal.SizeOf(availdm);
                    IList<DisplaySettings> availableDisplaySettings = new List<DisplaySettings>();

                    var tmp2 = displayDevice.GetProperty("availableDisplaySettings");
                    var freAvailableDisplaySettings = new FreArraySharp(tmp2.Get());

                    uint cntAvailableSettings = 0;
                    for (var iModeNum = 0; WinApi.EnumDisplaySettings(dd.DeviceName, iModeNum, ref availdm) != 0; iModeNum++) {
                        var settings = new DisplaySettings {
                            Width = availdm.dmPelsWidth,
                            Height = availdm.dmPelsHeight,
                            BitDepth = availdm.dmBitsPerPel,
                            RefreshRate = availdm.dmDisplayFrequency
                        };

                        if (HasDisplaySetting(availableDisplaySettings, settings)) continue;
                        availableDisplaySettings.Add(settings);

                        var displaySettings = new FreObjectSharp("com.tuarua.DisplaySettings", null);

                        displaySettings.SetProperty("width", availdm.dmPelsWidth);
                        displaySettings.SetProperty("height", availdm.dmPelsHeight);
                        displaySettings.SetProperty("refreshRate", availdm.dmDisplayFrequency);
                        displaySettings.SetProperty("bitDepth", availdm.dmBitsPerPel);
                        freAvailableDisplaySettings.SetObjectAt(displaySettings, cntAvailableSettings);
                        cntAvailableSettings++;
                    }

                    displayMonitor.SetProperty("friendlyName", monitor.DeviceString);
                    displayMonitor.SetProperty("name", monitor.DeviceName);
                    displayMonitor.SetProperty("id", monitor.DeviceID);
                    displayMonitor.SetProperty("key", monitor.DeviceKey);

                    displayDevice.SetProperty("friendlyName", dd.DeviceString);
                    displayDevice.SetProperty("name", dd.DeviceName);
                    displayDevice.SetProperty("id", dd.DeviceID);
                    displayDevice.SetProperty("key", dd.DeviceKey);

                    var currentDisplaySettings = new FreObjectSharp("com.tuarua.DisplaySettings", null);
                    currentDisplaySettings.SetProperty("width", dm.dmPelsWidth);
                    currentDisplaySettings.SetProperty("height", dm.dmPelsHeight);
                    currentDisplaySettings.SetProperty("refreshRate", dm.dmDisplayFrequency);
                    currentDisplaySettings.SetProperty("bitDepth", dm.dmBitsPerPel);

                    displayDevice.SetProperty("currentDisplaySettings", currentDisplaySettings);
                    displayDevice.SetProperty("monitor", displayMonitor);

                    vecDisplayDevices.SetObjectAt(displayDevice, cnt);

                    _displayDeviceMap.Add(dd.DeviceKey,dd);

                    cnt++;
                }
            }
            catch (Exception) {
                // ignored
            }

            return vecDisplayDevices.Get();
        }

        public FREObject SetDisplayResolution(FREContext ctx, uint argc, FREObject[] argv) {
            var key = new FreObjectSharp(argv[0]).GetAsString();
            var newWidth = new FreObjectSharp(argv[1]).GetAsInt();
            var newHeight = new FreObjectSharp(argv[2]).GetAsInt();
            var newRefreshRate = new FreObjectSharp(argv[3]).GetAsInt();

            var device = _displayDeviceMap[key];
            var dm = new Devmode();
            dm.dmSize = (short)Marshal.SizeOf(dm);

            if (WinApi.EnumDisplaySettings(device.DeviceName, WinApi.EnumCurrentSettings, ref dm) == 0) {
                return new FreObjectSharp(false).Get();
            }

            dm.dmPelsWidth = newWidth;
            dm.dmPelsHeight = newHeight;

            var flgs = DevModeFlags.DM_PELSWIDTH | DevModeFlags.DM_PELSHEIGHT;

            if (newRefreshRate > 0) {
                flgs |= DevModeFlags.DM_DISPLAYFREQUENCY;
                dm.dmDisplayFrequency = newRefreshRate;
            }

            dm.dmFields = (int)flgs;

            return WinApi.ChangeDisplaySettings(ref dm, (int) ChangeDisplaySettingsFlags.CdsTest) != 0 
                ? new FreObjectSharp(false).Get() 
                : new FreObjectSharp(WinApi.ChangeDisplaySettings(ref dm, 0) == 0).Get();
        }

        public FREObject RestartApp(FREContext ctx, uint argc, FREObject[] argv) {
            var delay = new FreObjectSharp(argv[0]).GetAsUInt();
            var wmiQuery =
                $"select CommandLine from Win32_Process where Name='{Process.GetCurrentProcess().ProcessName}.exe'";
            var searcher = new ManagementObjectSearcher(wmiQuery);
            var retObjectCollection = searcher.Get();
            var sf = (from ManagementObject retObject in retObjectCollection select $"{retObject["CommandLine"]}").FirstOrDefault();
            if (string.IsNullOrEmpty(sf)) return new FreObjectSharp(false).Get();
            var info = new ProcessStartInfo {
                Arguments = "/C ping 127.0.0.1 -n " + delay + " && " + sf,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                FileName = "cmd.exe"
            };
            Process.Start(info);
            return new FreObjectSharp(true).Get();
        }

        

    }
}