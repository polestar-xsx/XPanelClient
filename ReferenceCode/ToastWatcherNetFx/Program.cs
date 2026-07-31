using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Microsoft.Win32;

namespace ToastWatcherNetFx
{
    internal static class Program
    {
        private const string EventLogName =
            "Microsoft-Windows-PushNotification-Platform/Operational";
        private static readonly object _uiLock = new object();
        private static string _lastUiSignature = "";
        private static DateTime _lastUiTime = DateTime.MinValue;

        // Teams process names (new Teams = ms-teams, classic = Teams)
        private static readonly HashSet<string> TeamsProcessNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ms-teams", "Teams", "MSTeams", "msedgewebview2", "ShellExperienceHost"
            };

        private static HashSet<string> _appFilter = null; // null = no filter (all apps)

        private static WinEventDelegate _winEventProc;
        private static readonly List<IntPtr> _winEventHooks = new List<IntPtr>();
        private static Thread _uiHookThread;
        private static uint _uiHookThreadId;
        private static volatile bool _uiHookStarted;

        private static int Main(string[] args)
        {
            bool showRawXml = args.Any(a =>
                string.Equals(a, "--raw-xml", StringComparison.OrdinalIgnoreCase));
            bool debugUi = args.Any(a =>
                string.Equals(a, "--debug-ui", StringComparison.OrdinalIgnoreCase));
            bool listApps = args.Any(a =>
                string.Equals(a, "--list-apps", StringComparison.OrdinalIgnoreCase));

            if (listApps)
            {
                var apps = QueryNotificationApps();
                Console.WriteLine("Notification-registered apps ("+apps.Count+" total):");
                Console.WriteLine(new string('-', 72));
                Console.WriteLine(string.Format("{0,4}  {1,-5}  {2,-30}  {3}", "No.", "State", "Display Name", "AUMID"));
                Console.WriteLine(new string('-', 72));
                for (int i = 0; i < apps.Count; i++)
                {
                    var app = apps[i];
                    string state = app.NotificationsEnabled ? "[ON] " : "[OFF]";
                    string disp = app.DisplayName.Length > 30
                        ? app.DisplayName.Substring(0, 28) + ".."
                        : app.DisplayName;
                    Console.WriteLine(string.Format("{0,4}. {1}  {2,-30}  {3}", i + 1, state, disp, app.Aumid));
                }
                Console.WriteLine(new string('-', 72));
                Console.WriteLine("Use --filter-apps=<n,n,...> or --filter-apps=<AUMID,...> to filter.");
                return 0;
            }

            // Parse --filter-apps
            var filterArg = args.FirstOrDefault(a =>
                a.StartsWith("--filter-apps=", StringComparison.OrdinalIgnoreCase));
            if (filterArg != null)
            {
                var parts = filterArg.Substring("--filter-apps=".Length)
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToArray();

                bool hasIndexes = parts.Any(p => p.All(char.IsDigit));
                if (hasIndexes)
                {
                    var allApps = QueryNotificationApps();
                    _appFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var part in parts)
                    {
                        int idx;
                        if (int.TryParse(part, out idx) && idx >= 1 && idx <= allApps.Count)
                            _appFilter.Add(allApps[idx - 1].Aumid);
                        else if (!part.All(char.IsDigit))
                            _appFilter.Add(part);
                    }
                }
                else
                {
                    _appFilter = new HashSet<string>(parts, StringComparer.OrdinalIgnoreCase);
                }

                if (_appFilter.Count > 0)
                    Console.WriteLine("[Config] App filter: " + string.Join(", ", _appFilter));
            }

            Console.WriteLine("[ToastWatcher] Starting (dual-channel)...");
            Console.WriteLine("[ToastWatcher] Press Ctrl+C to stop.");
            Console.WriteLine();

            var quit = new ManualResetEvent(false);
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; quit.Set(); };

            // Channel 1: EventLog 3052 (Toast) - proven to work for Outlook
            EventLogWatcher evtWatcher = null;
            try
            {
                var query = new EventLogQuery(
                    EventLogName, PathType.LogName, "*[System[(EventID=3052)]]");
                evtWatcher = new EventLogWatcher(query);
                evtWatcher.EventRecordWritten += (s, e) =>
                {
                    if (e == null || e.EventRecord == null) return;
                    using (var rec = e.EventRecord)
                    {
                        var data = ParseEventData(rec.ToXml());
                        var aumid = GetVal(data, "AppUserModelId");
                        if (_appFilter != null && _appFilter.Count > 0 && !_appFilter.Contains(aumid))
                            return;
                        Console.WriteLine();
                        Console.WriteLine("=== [EventLog] Toast Notification ===");
                        Console.WriteLine("Time      : " + rec.TimeCreated.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                        Console.WriteLine("App       : " + GetVal(data, "AppUserModelId"));
                        Console.WriteLine("TrackingId: " + GetVal(data, "TrackingId"));
                        Console.WriteLine("MessageId : " + GetVal(data, "MessageId"));
                        if (showRawXml) Console.WriteLine(rec.ToXml());
                    }
                };
                evtWatcher.Enabled = true;
                Console.WriteLine("[Channel 1] EventLog 3052 watching - OK");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Channel 1] EventLog init failed: " + ex.Message);
            }

            // Channel 2: WinEventHook - captures Teams notification signal events
            try
            {
                _uiHookThread = new Thread(() => UiHookThreadMain(debugUi));
                _uiHookThread.IsBackground = true;
                _uiHookThread.Name = "UIHookThread";
                _uiHookThread.SetApartmentState(ApartmentState.STA);
                _uiHookThread.Start();

                for (int i = 0; i < 40 && !_uiHookStarted; i++) Thread.Sleep(50);
                if (!_uiHookStarted)
                    throw new InvalidOperationException("UI hook thread did not start");

                Console.WriteLine("[Channel 2] Teams WinEvent hook       - OK");
                if (debugUi) Console.WriteLine("[Channel 2] Debug mode                - ON");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[Channel 2] Teams hook init failed: " + ex.Message);
            }

            Console.WriteLine();
            quit.WaitOne();

            if (evtWatcher != null) { evtWatcher.Enabled = false; evtWatcher.Dispose(); }
            StopUiHookThread();
            return 0;
        }

        private static void UiHookThreadMain(bool debugUi)
        {
            try
            {
                _uiHookThreadId = GetCurrentThreadId();
                _winEventProc = (hook, evt, hwnd, idObject, idChild, thread, time) =>
                {
                    if (hwnd == IntPtr.Zero) return;
                    if (idObject != OBJID_WINDOW && idObject != OBJID_CLIENT) return;
                    OnUiEvent(hwnd, evt, debugUi, idObject);
                };

                RegisterEventHook(EVENT_OBJECT_SHOW);
                if (_winEventHooks.Count == 0)
                    throw new InvalidOperationException("No WinEventHook registered");

                _uiHookStarted = true;

                MSG msg;
                while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            catch
            {
            }
            finally
            {
                foreach (var hook in _winEventHooks)
                {
                    try { UnhookWinEvent(hook); }
                    catch { }
                }
                _winEventHooks.Clear();
                _uiHookStarted = false;
            }
        }

        private static void StopUiHookThread()
        {
            if (_uiHookThreadId != 0)
            {
                try { PostThreadMessage(_uiHookThreadId, WM_QUIT, UIntPtr.Zero, IntPtr.Zero); }
                catch { }
            }
            if (_uiHookThread != null)
            {
                try { _uiHookThread.Join(1500); }
                catch { }
            }
        }

        private static void RegisterEventHook(uint evt)
        {
            var h = SetWinEventHook(
                evt,
                evt,
                IntPtr.Zero,
                _winEventProc,
                0,
                0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            if (h != IntPtr.Zero) _winEventHooks.Add(h);
        }

        private static void OnUiEvent(IntPtr hwnd, uint evt, bool debugUi, int objectId)
        {
            try
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid == 0) return;

                Process proc;
                try { proc = Process.GetProcessById((int)pid); }
                catch { return; }

                string winTitle = GetWindowTitle(hwnd);
                string className = GetWindowClassName(hwnd);
                bool maybeTeams =
                    TeamsProcessNames.Contains(proc.ProcessName) ||
                    (!string.IsNullOrWhiteSpace(winTitle) &&
                     winTitle.IndexOf("teams", StringComparison.OrdinalIgnoreCase) >= 0);

                if (debugUi && maybeTeams)
                {
                    Console.WriteLine("[UI-Debug] proc=" + proc.ProcessName +
                                      " evt=" + EventName(evt) +
                                      " obj=" + objectId +
                                      " class=" + className +
                                      " title=" + winTitle);
                }

                if (!maybeTeams) return;
                EmitTeamsSignal(proc.ProcessName, className, winTitle, evt);
            }
            catch { }
        }

        private static void EmitTeamsSignal(string processName, string className, string winTitle, uint evt)
        {
            if (!string.Equals(processName, "ms-teams", StringComparison.OrdinalIgnoreCase)) return;
            if (string.IsNullOrWhiteSpace(winTitle)) winTitle = "(no-title)";
            if (string.IsNullOrWhiteSpace(className)) className = "(no-class)";

            var signature = processName + "|" + className + "|" + winTitle + "|" + EventName(evt);
            lock (_uiLock)
            {
                if (signature == _lastUiSignature && (DateTime.Now - _lastUiTime).TotalSeconds < 2)
                    return;
                _lastUiSignature = signature;
                _lastUiTime = DateTime.Now;
            }

            Console.WriteLine();
            Console.WriteLine("=== [TeamsEvent] Notification Signal ===");
            Console.WriteLine("Time  : " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            Console.WriteLine("Proc  : " + processName);
            Console.WriteLine("Class : " + className);
            Console.WriteLine("Event : " + EventName(evt));
            Console.WriteLine("Title : " + winTitle);
        }

        private static string GetWindowTitle(IntPtr hwnd)
        {
            var sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString().Trim();
        }

        private static string GetWindowClassName(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString().Trim();
        }

        private static string EventName(uint evt)
        {
            if (evt == EVENT_OBJECT_SHOW) return "OBJ_SHOW";
            return "0x" + evt.ToString("X");
        }

        private static Dictionary<string, string> ParseEventData(string xml)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            foreach (var n in XDocument.Parse(xml).Descendants(ns + "EventData").Descendants(ns + "Data"))
            {
                var a = n.Attribute("Name");
                if (a != null && !string.IsNullOrWhiteSpace(a.Value))
                    map[a.Value] = n.Value ?? "";
            }
            return map;
        }

        private static string GetVal(Dictionary<string, string> d, string k)
        { string v; return d.TryGetValue(k, out v) ? v : ""; }

        // ── Notification app registry enumeration ──────────────────────────

        private class NotificationApp
        {
            public string Aumid;
            public string DisplayName;
            public bool NotificationsEnabled;
        }

        private static List<NotificationApp> QueryNotificationApps()
        {
            var result = new List<NotificationApp>();
            const string settingsPath =
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings";
            using (var settingsKey = Registry.CurrentUser.OpenSubKey(settingsPath))
            {
                if (settingsKey == null) return result;
                foreach (var name in settingsKey.GetSubKeyNames())
                {
                    bool enabled = true;
                    using (var sub = settingsKey.OpenSubKey(name))
                    {
                        if (sub != null)
                        {
                            var val = sub.GetValue("Enabled");
                            if (val != null && val is int && (int)val == 0)
                                enabled = false;
                        }
                    }
                    result.Add(new NotificationApp
                    {
                        Aumid = name,
                        DisplayName = LookupDisplayName(name),
                        NotificationsEnabled = enabled
                    });
                }
            }
            return result.OrderBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string LookupDisplayName(string aumid)
        {
            const string classesPath = @"SOFTWARE\Classes\AppUserModelId\";
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using (var key = root.OpenSubKey(classesPath + aumid))
                    {
                        if (key == null) continue;
                        var v = key.GetValue("DisplayName") as string;
                        if (!string.IsNullOrWhiteSpace(v) && !v.StartsWith("@"))
                            return v;
                    }
                }
                catch { }
            }
            return DeriveDisplayName(aumid);
        }

        private static string DeriveDisplayName(string aumid)
        {
            // Strip !AppId suffix  (UWP: FamilyName!AppId)
            var bang = aumid.IndexOf('!');
            var s = bang >= 0 ? aumid.Substring(0, bang) : aumid;

            // Strip package hash suffix  _xxxxxxxxxx
            var under = s.LastIndexOf('_');
            if (under > 0)
            {
                var suffix = s.Substring(under + 1);
                if (suffix.Length >= 8 && suffix.All(char.IsLetterOrDigit))
                    s = s.Substring(0, under);
            }

            // com.squirrel.Teams.Teams  →  "Teams"
            if (s.StartsWith("com.", StringComparison.OrdinalIgnoreCase))
                return s.Split('.').Last();

            // Remove pure-digit segments (version numbers) and "EXE"
            var parts = s.Split('.');
            var cleaned = parts
                .Where(p => !p.All(char.IsDigit))
                .Where(p => !string.Equals(p, "EXE", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            return string.Join(" ", cleaned);
        }

        private const uint EVENT_OBJECT_SHOW = 0x8002;
        private const uint WM_QUIT = 0x0012;
        private const int OBJID_WINDOW = 0;
        private const int OBJID_CLIENT = -4;
        private const uint WINEVENT_OUTOFCONTEXT = 0;
        private const uint WINEVENT_SKIPOWNPROCESS = 2;

        private delegate void WinEventDelegate(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc,
            uint idProcess,
            uint idThread,
            uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public UIntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern sbyte GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage([In] ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage([In] ref MSG lpmsg);

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(uint idThread, uint Msg, UIntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    }
}