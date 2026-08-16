using System;
using System.Windows;
using System.IO;
using System.Drawing;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Win32;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Diagnostics.Eventing.Reader;
using WinForms = System.Windows.Forms;
using Windows.ApplicationModel;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using XPanel.Communication.Bluetooth;
using XPanel.Communication.MQTT;
using XPanel.Communication.Serial;
using XPanel.Core.Communication;
using XPanel.Core.Protocol;

namespace XPanel.Application
{
    /// <summary>
    /// Synchronization Item Model
    /// </summary>
    public class SyncItem
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsEnabled { get; set; }
        public string Category { get; set; } = string.Empty;
        public int Priority { get; set; }

        public SyncItem()
        {
            Id = Guid.NewGuid().ToString("N");
            Priority = 0;
        }

        public SyncItem(string name, string category, bool isEnabled = false) : this()
        {
            Name = name;
            Category = category;
            IsEnabled = isEnabled;
        }
    }

    /// <summary>
    /// Sync Item Service - Business Logic Layer
    /// </summary>
    public interface ISyncItemProvider
    {
        List<SyncItem> GetAllItems();
        void SaveItems(List<SyncItem> items);
        void OnItemToggled(SyncItem item);
    }

    public class SyncItemService : ISyncItemProvider
    {
        private static readonly string SyncConfigPath = Path.Combine(
            AppContext.BaseDirectory,
            "sync-config.json");
        private static readonly SemaphoreSlim NotificationIconDumpLock = new(1, 1);
        private const int NotificationIconSize = 32;
        private const int NotificationIconContentSize = 28;
        private const string NotificationSettingsRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings";

        private List<SyncItem> _syncItems;
        private UserNotificationListener _notificationListener;
        private CancellationTokenSource _notificationListenerCts;
        private Func<string, string, string, string, byte[], uint, Task<bool>> _sendNotificationCallback;
        private Func<string, bool>? _isNotificationEnabledForDevice;
        private CancellationTokenSource? _teamsListenerCts;
        private Func<string, string, string, string, byte[], uint, Task<bool>>? _sendTeamsNotificationCallback;
        private Func<string, bool>? _isTeamsEnabledForDevice;
        private byte[]? _teamsIconData;
        private readonly object _teamsEventLock = new();
        private string _lastTeamsEventSignature = string.Empty;
        private DateTime _lastTeamsEventTime = DateTime.MinValue;
        private WinEventDelegate? _teamsWinEventProc;
        private readonly List<IntPtr> _teamsWinEventHooks = new();
        private Thread? _teamsHookThread;
        private uint _teamsHookThreadId;
        private volatile bool _teamsHookStarted;
        private dynamic _connectedDevicesRef;

        private const string TeamsAppId = "com.squirrel.Teams.Teams";
        private const string TeamsDisplayTitle = "Teams";
        private const uint EVENT_OBJECT_SHOW = 0x8002;
        private const uint WM_QUIT = 0x0012;
        private const int OBJID_WINDOW = 0;
        private const int OBJID_CLIENT = -4;
        private const uint WINEVENT_OUTOFCONTEXT = 0;
        private const uint WINEVENT_SKIPOWNPROCESS = 2;

        public SyncItemService()
        {
            _syncItems = LoadSyncItemsFromConfig();
            if (_syncItems.Count == 0)
            {
                _syncItems = GetDefaultSyncItems();
            }
        }

        public List<SyncItem> GetAllItems()
        {
            return _syncItems.OrderBy(x => x.Priority).ToList();
        }

        public void SaveItems(List<SyncItem> items)
        {
            _syncItems = items;
            try
            {
                var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SyncConfigPath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save sync config: {ex.Message}");
            }
        }

        public void OnItemToggled(SyncItem item)
        {
            // 这是业务逻辑扩展点，可以根据不同的item执行相应的操作
            switch (item.Category)
            {
                case "Time":
                    HandleTimeSync(item);
                    break;
                case "Weather":
                    HandleWeatherSync(item);
                    break;
                case "Teams":
                    HandleTeamsSync(item);
                    break;
                case "Notification":
                    HandleNotificationSync(item);
                    break;
                default:
                    System.Diagnostics.Debug.WriteLine($"Unknown sync category: {item.Category}");
                    break;
            }
        }

        private void HandleTimeSync(SyncItem item)
        {
            System.Diagnostics.Debug.WriteLine($"Time Sync: {item.Name} - {(item.IsEnabled ? "Enabled" : "Disabled")}");
            // TODO: Implement time sync logic
        }

        private void HandleWeatherSync(SyncItem item)
        {
            System.Diagnostics.Debug.WriteLine($"Weather Sync: {item.Name} - {(item.IsEnabled ? "Enabled" : "Disabled")}");
            // TODO: Implement weather sync logic
        }

        private void HandleTeamsSync(SyncItem item)
        {
            System.Diagnostics.Debug.WriteLine($"Teams Sync: {item.Name} - {(item.IsEnabled ? "Enabled" : "Disabled")}");
            // TODO: Implement Teams sync logic
        }

        private void HandleNotificationSync(SyncItem item)
        {
            // Delegate to MainWindow for notification handling
            // This will be called via SyncItemService.OnItemToggled
            System.Diagnostics.Debug.WriteLine($"SyncItemService: NotificationSync requested - delegating to MainWindow");
        }

        public async Task CacheNotificationAppIconsAsync()
        {
            await NotificationIconDumpLock.WaitAsync();
            try
            {
                string tempDir = Path.Combine(AppContext.BaseDirectory, "temp");
                Directory.CreateDirectory(tempDir);

                var reportLines = new List<string>
                {
                    $"GeneratedAt={DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    "AppId\tStatus\tSource\tDetail",
                };

                // Query all registered notification apps from registry (not just active notifications)
                List<string> allAppIds = QueryNotificationAppIds();
                System.Diagnostics.Debug.WriteLine($"[CacheNotificationAppIconsAsync] Found {allAppIds.Count} apps from registry");

                int successCount = 0;

                foreach (string appId in allAppIds.OrderBy(x => x))
                {
                    string outputPath = Path.Combine(tempDir, $"{BuildSafeFileName(appId)}.png");
                    IconSaveResult result = await TrySaveAppIconAsync(appId, outputPath);
                    reportLines.Add($"{appId}\t{(result.Success ? "OK" : "FAIL")}\t{result.Source}\t{result.Detail}");
                    System.Diagnostics.Debug.WriteLine($"[CacheNotificationAppIconsAsync] {appId}: {(result.Success ? "OK" : "FAIL")} ({result.Source})");

                    if (result.Success)
                    {
                        successCount++;
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"Notification icon cache failed: {appId}, reason={result.Detail}");
                    }
                }

                string reportPath = Path.Combine(tempDir, "icon-cache-report.txt");
                File.WriteAllLines(reportPath, reportLines);

                System.Diagnostics.Debug.WriteLine($"Notification icon cache completed: {successCount}/{allAppIds.Count} saved to {tempDir}, report={reportPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Notification icon cache error: {ex.Message}");
            }
            finally
            {
                NotificationIconDumpLock.Release();
            }
        }

        private static List<string> QueryNotificationAppIds()
        {
            using var settingsKey = Registry.CurrentUser.OpenSubKey(NotificationSettingsRegistryPath);
            if (settingsKey == null)
            {
                return new List<string>();
            }

            return settingsKey
                .GetSubKeyNames()
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static async Task<HashSet<string>> QueryActiveNotificationAppIdsAsync()
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                UserNotificationListener listener = UserNotificationListener.Current;
                UserNotificationListenerAccessStatus accessStatus = listener.GetAccessStatus();
                if (accessStatus != UserNotificationListenerAccessStatus.Allowed)
                {
                    accessStatus = await listener.RequestAccessAsync();
                }

                if (accessStatus != UserNotificationListenerAccessStatus.Allowed)
                {
                    return result;
                }

                var notifications = await listener.GetNotificationsAsync(NotificationKinds.Toast);
                foreach (var notification in notifications)
                {
                    string appId = notification.AppInfo?.AppUserModelId ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(appId))
                    {
                        result.Add(appId);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"QueryActiveNotificationAppIdsAsync failed: {ex.Message}");
            }

            return result;
        }

        private static async Task<IconSaveResult> TrySaveAppIconAsync(string appId, string outputPath)
        {
            try
            {
                if (TryGetIconFromAppInfo(appId, out AppInfo? appInfo, out string appInfoError) && appInfo != null)
                {
                    bool appInfoSaved = await TrySaveAppInfoLogoAsync(appInfo, outputPath);
                    if (appInfoSaved)
                    {
                        return IconSaveResult.Ok("AppInfo", "AppInfo.DisplayInfo.Logo");
                    }
                }

                if (TryQueryRegistryForIconPath(appId, out string registryIcon, out string registrySource))
                {
                    if (TrySaveIconFromPathToken(registryIcon, outputPath, out string tokenDetail))
                    {
                        return IconSaveResult.Ok(registrySource, tokenDetail);
                    }
                }

                if (TryQueryUninstallRegistryForIcon(appId, outputPath, out string uninstallDetail))
                {
                    return IconSaveResult.Ok("UninstallRegistry", uninstallDetail);
                }

                if (TrySaveFallbackExeIcon(appId, outputPath, out string exeDetail))
                {
                    return IconSaveResult.Ok("ExeFallback", exeDetail);
                }

                string reason = string.IsNullOrWhiteSpace(appInfoError)
                    ? "No supported icon source"
                    : appInfoError;
                return IconSaveResult.Fail("None", reason);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"TrySaveAppIconAsync failed for {appId}: {ex.Message}");
                return IconSaveResult.Fail("Exception", ex.Message);
            }
        }

        private static bool TryQueryRegistryForIconPath(string appId, out string iconPath, out string source)
        {
            iconPath = string.Empty;
            source = string.Empty;

            string[] registryPaths =
            {
                $@"HKEY_CURRENT_USER\Software\Classes\AppUserModelId\{appId}",
                $@"HKEY_LOCAL_MACHINE\Software\Classes\AppUserModelId\{appId}",
                $@"HKEY_LOCAL_MACHINE\Software\WOW6432Node\Classes\AppUserModelId\{appId}",
                $@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\{appId}",
            };

            string[] valueNames = { "Icon", "IconUri", "DefaultIcon", "DisplayIcon" };

            foreach (var path in registryPaths)
            {
                string rootStr = path.Split('\\')[0];
                string subPath = path.Substring(rootStr.Length + 1);

                RegistryKey? rootKey = rootStr switch
                {
                    "HKEY_CURRENT_USER" => Registry.CurrentUser,
                    "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
                    _ => null,
                };

                if (rootKey == null)
                {
                    continue;
                }

                try
                {
                    using var key = rootKey.OpenSubKey(subPath);
                    if (key == null)
                    {
                        continue;
                    }

                    foreach (var valueName in valueNames)
                    {
                        object? raw = key.GetValue(valueName);
                        if (raw == null)
                        {
                            continue;
                        }

                        string value = raw.ToString() ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            continue;
                        }

                        iconPath = value;
                        source = $"RegistryIcon_{valueName}";
                        return true;
                    }
                }
                catch
                {
                    continue;
                }
            }

            return false;
        }

        private static bool TryGetIconFromAppInfo(string appId, out AppInfo? appInfo, out string detail)
        {
            appInfo = null;
            detail = "";
            try
            {
                appInfo = AppInfo.GetFromAppUserModelId(appId);
                if (appInfo == null)
                {
                    detail = "AppInfo not found";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                return false;
            }
        }

        private static async Task<bool> TrySaveAppInfoLogoAsync(AppInfo appInfo, string outputPath)
        {
            var logoRef = appInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(NotificationIconSize, NotificationIconSize));
            if (logoRef == null)
            {
                return false;
            }

            using var stream = await logoRef.OpenReadAsync();
            using var sourceStream = stream.AsStreamForRead();
            using var sourceImage = System.Drawing.Image.FromStream(sourceStream, useEmbeddedColorManagement: false, validateImageData: false);
            return SaveProcessedImage(sourceImage, outputPath);
        }

        private static bool SaveProcessedImage(System.Drawing.Image sourceImage, string outputPath)
        {
            using var sourceBitmap = new Bitmap(sourceImage);
            using var outputBitmap = new Bitmap(NotificationIconSize, NotificationIconSize, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            Rectangle sourceRect = FindOpaqueBounds(sourceBitmap);
            if (sourceRect.Width <= 0 || sourceRect.Height <= 0)
            {
                sourceRect = new Rectangle(0, 0, sourceBitmap.Width, sourceBitmap.Height);
            }

            float scale = Math.Min(
                NotificationIconContentSize / (float)sourceRect.Width,
                NotificationIconContentSize / (float)sourceRect.Height);

            int targetWidth = Math.Max(1, (int)Math.Round(sourceRect.Width * scale));
            int targetHeight = Math.Max(1, (int)Math.Round(sourceRect.Height * scale));
            int targetX = (NotificationIconSize - targetWidth) / 2;
            int targetY = (NotificationIconSize - targetHeight) / 2;

            using (var graphics = Graphics.FromImage(outputBitmap))
            {
                graphics.CompositingMode = CompositingMode.SourceOver;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.Clear(System.Drawing.Color.Transparent);
                graphics.DrawImage(
                    sourceBitmap,
                    new Rectangle(targetX, targetY, targetWidth, targetHeight),
                    sourceRect,
                    GraphicsUnit.Pixel);
            }

            outputBitmap.Save(outputPath, System.Drawing.Imaging.ImageFormat.Png);
            return true;
        }

        private static bool TryGetNotificationSettingValue(string appId, string valueName, out string value)
        {
            value = string.Empty;
            try
            {
                using var appKey = Registry.CurrentUser.OpenSubKey($"{NotificationSettingsRegistryPath}\\{appId}");
                if (appKey == null)
                {
                    return false;
                }

                object? raw = appKey.GetValue(valueName);
                if (raw == null)
                {
                    return false;
                }

                value = raw.ToString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(value);
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySaveIconFromPathToken(string token, string outputPath, out string detail)
        {
            detail = "icon token unresolved";
            if (!TryResolveIconTokenToPath(token, out string iconPath, out string resolveDetail))
            {
                detail = resolveDetail;
                return false;
            }

            if (IsLikelyGenericInstallerIcon(iconPath))
            {
                detail = $"filtered generic icon:{iconPath}";
                return false;
            }

            try
            {
                string ext = Path.GetExtension(iconPath);
                if (string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".bmp", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".gif", StringComparison.OrdinalIgnoreCase))
                {
                    using var image = System.Drawing.Image.FromFile(iconPath);
                    SaveProcessedImage(image, outputPath);
                    detail = $"image:{iconPath}";
                    return true;
                }

                using var icon = Icon.ExtractAssociatedIcon(iconPath);
                if (icon == null)
                {
                    detail = $"associated icon missing:{iconPath}";
                    return false;
                }

                using var bitmap = icon.ToBitmap();
                SaveProcessedImage(bitmap, outputPath);
                detail = $"associated:{iconPath}";
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                return false;
            }
        }

        private static bool IsLikelyGenericInstallerIcon(string iconPath)
        {
            string fileName = Path.GetFileName(iconPath) ?? string.Empty;
            string fullPath = iconPath.ToLowerInvariant();

            if (string.Equals(fileName, "msiexec.exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(fileName, "ginstall.exe", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (fullPath.Contains("\\difx\\icons\\", StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        private static bool TryResolveIconTokenToPath(string token, out string path, out string detail)
        {
            path = string.Empty;
            detail = "";
            if (string.IsNullOrWhiteSpace(token))
            {
                detail = "empty token";
                return false;
            }

            string value = token.Trim().Trim('"');
            if (value.StartsWith("@", StringComparison.Ordinal))
            {
                value = value.Substring(1);
            }

            value = Environment.ExpandEnvironmentVariables(value);

            if (value.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(value, UriKind.Absolute, out Uri? fileUri) &&
                fileUri.IsFile)
            {
                value = fileUri.LocalPath;
            }

            int commaIndex = value.IndexOf(',');
            if (commaIndex > 0)
            {
                value = value.Substring(0, commaIndex).Trim();
            }

            if (value.StartsWith("ms-appx", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase))
            {
                detail = $"unsupported uri:{value}";
                return false;
            }

            if (!File.Exists(value))
            {
                detail = $"file missing:{value}";
                return false;
            }

            path = value;
            detail = "resolved";
            return true;
        }

        private static bool TrySaveFallbackExeIcon(string appId, string outputPath, out string detail)
        {
            detail = "exe not resolved";
            if (!TryResolveExePathFromAppId(appId, out string exePath, out string resolveDetail))
            {
                detail = resolveDetail;
                return false;
            }

            try
            {
                using var icon = Icon.ExtractAssociatedIcon(exePath);
                if (icon == null)
                {
                    detail = $"associated icon missing:{exePath}";
                    return false;
                }

                using var bitmap = icon.ToBitmap();
                SaveProcessedImage(bitmap, outputPath);
                detail = exePath;
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                return false;
            }
        }

        private static bool TryQueryUninstallRegistryForIcon(string appId, string outputPath, out string detail)
        {
            detail = "uninstall not found";
            string[] uninstallPaths =
            {
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
                @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            };

            RegistryKey[] hives = { Registry.CurrentUser, Registry.LocalMachine };

            foreach (var hive in hives)
            {
                foreach (string basePath in uninstallPaths)
                {
                    using var key = hive.OpenSubKey(basePath);
                    if (key == null)
                    {
                        continue;
                    }

                    foreach (string subKeyName in key.GetSubKeyNames())
                    {
                        using var appKey = key.OpenSubKey(subKeyName);
                        if (appKey == null)
                        {
                            continue;
                        }

                        object? displayNameObj = appKey.GetValue("DisplayName");
                        string displayName = displayNameObj?.ToString() ?? string.Empty;

                        if (!MatchesAppId(appId, displayName, subKeyName))
                        {
                            continue;
                        }

                        object? displayIconObj = appKey.GetValue("DisplayIcon");
                        string displayIcon = displayIconObj?.ToString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(displayIcon))
                        {
                            if (TrySaveIconFromPathToken(displayIcon, outputPath, out string iconDetail))
                            {
                                detail = iconDetail;
                                return true;
                            }
                        }

                        object? installLocObj = appKey.GetValue("InstallLocation");
                        string installLocation = installLocObj?.ToString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(installLocation))
                        {
                            if (TryExtractIconFromInstallLocation(installLocation, outputPath, out string extractDetail))
                            {
                                detail = extractDetail;
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        private static bool MatchesAppId(string appId, string displayName, string regKeyName)
        {
            if (string.IsNullOrWhiteSpace(appId))
            {
                return false;
            }

            string normalizedDisplay = NormalizeText(displayName);
            string normalizedReg = NormalizeText(regKeyName);
            string compactDisplay = normalizedDisplay.Replace(" ", string.Empty, StringComparison.Ordinal);
            string compactReg = normalizedReg.Replace(" ", string.Empty, StringComparison.Ordinal);

            var distinctiveTokens = ExtractDistinctiveTokens(appId);
            if (distinctiveTokens.Count == 0)
            {
                return false;
            }

            int displayHits = distinctiveTokens.Count(token =>
                normalizedDisplay.Contains(token, StringComparison.Ordinal) ||
                compactDisplay.Contains(token, StringComparison.Ordinal));
            int regHits = distinctiveTokens.Count(token =>
                normalizedReg.Contains(token, StringComparison.Ordinal) ||
                compactReg.Contains(token, StringComparison.Ordinal));

            // Keep matching permissive enough for cache preloading, but avoid generic-vendor-only matches.
            return displayHits > 0 || regHits > 0;
        }

        private static string NormalizeText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var chars = value
                .ToLowerInvariant()
                .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
                .ToArray();
            return new string(chars);
        }

        private static HashSet<string> ExtractDistinctiveTokens(string appId)
        {
            string normalized = NormalizeText(appId);
            string[] rawTokens = normalized
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            var genericTokens = new HashSet<string>(StringComparer.Ordinal)
            {
                "microsoft",
                "windows",
                "explorer",
                "notification",
                "system",
                "systemtoast",
                "toast",
                "app",
                "stable",
                "desktop",
                "exe",
                "cw5n1h2txyewy",
                "8wekyb3d8bbwe",
            };

            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (string token in rawTokens)
            {
                if (token.Length < 4)
                {
                    continue;
                }

                if (genericTokens.Contains(token))
                {
                    continue;
                }

                if (token.All(char.IsDigit))
                {
                    continue;
                }

                result.Add(token);
            }

            return result;
        }

        private static bool TryExtractIconFromInstallLocation(string installLocation, string outputPath, out string detail)
        {
            detail = "no exe found in location";
            if (!Directory.Exists(installLocation))
            {
                detail = "install location not found";
                return false;
            }

            var exeFiles = Directory.EnumerateFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly)
                .Take(5)
                .ToList();

            if (exeFiles.Count == 0)
            {
                return false;
            }

            foreach (string exePath in exeFiles)
            {
                try
                {
                    using var icon = Icon.ExtractAssociatedIcon(exePath);
                    if (icon == null)
                    {
                        continue;
                    }

                    using var bitmap = icon.ToBitmap();
                    SaveProcessedImage(bitmap, outputPath);
                    detail = Path.GetFileName(exePath);
                    return true;
                }
                catch
                {
                    continue;
                }
            }

            return false;
        }

        private static bool TryResolveExePathFromAppId(string appId, out string exePath, out string detail)
        {
            exePath = string.Empty;
            detail = "";

            int exeIndex = appId.IndexOf(".EXE", StringComparison.OrdinalIgnoreCase);
            if (exeIndex < 0)
            {
                detail = "no .EXE token";
                return false;
            }

            int start = appId.LastIndexOf('.', exeIndex - 1);
            string exeName = start >= 0
                ? appId.Substring(start + 1, exeIndex - start - 1) + ".exe"
                : appId.Substring(0, exeIndex + 4);

            if (TryResolveExePathFromAppPaths(exeName, out exePath))
            {
                detail = $"App Paths:{exeName}";
                return true;
            }

            detail = $"App Paths unresolved:{exeName}";
            return false;
        }

        private static bool TryResolveExePathFromAppPaths(string exeName, out string exePath)
        {
            exePath = string.Empty;
            string[] roots =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths",
            };

            RegistryKey[] hives = { Registry.CurrentUser, Registry.LocalMachine };

            foreach (var hive in hives)
            {
                foreach (string root in roots)
                {
                    using var key = hive.OpenSubKey($"{root}\\{exeName}");
                    if (key == null)
                    {
                        continue;
                    }

                    object? raw = key.GetValue(string.Empty);
                    string? path = raw?.ToString();
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    {
                        exePath = path;
                        return true;
                    }
                }
            }

            return false;
        }

        private static Rectangle FindOpaqueBounds(Bitmap bitmap)
        {
            int minX = bitmap.Width;
            int minY = bitmap.Height;
            int maxX = -1;
            int maxY = -1;

            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y).A <= 8)
                    {
                        continue;
                    }

                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < minX || maxY < minY)
            {
                return Rectangle.Empty;
            }

            return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        }

        private static string BuildSafeFileName(string appId)
        {
            if (string.IsNullOrWhiteSpace(appId))
            {
                return "unknown-app";
            }

            var invalidChars = Path.GetInvalidFileNameChars();
            var chars = appId.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray();
            return new string(chars);
        }

        private sealed class IconSaveResult
        {
            public bool Success { get; private set; }
            public string Source { get; private set; } = string.Empty;
            public string Detail { get; private set; } = string.Empty;

            public static IconSaveResult Ok(string source, string detail)
            {
                return new IconSaveResult
                {
                    Success = true,
                    Source = source,
                    Detail = detail,
                };
            }

            public static IconSaveResult Fail(string source, string detail)
            {
                return new IconSaveResult
                {
                    Success = false,
                    Source = source,
                    Detail = detail,
                };
            }
        }

        private List<SyncItem> GetDefaultSyncItems()
        {
            return new List<SyncItem>
            {
                new SyncItem("时间", "Time", false) { Priority = 0 },
                new SyncItem("天气", "Weather", false) { Priority = 1 },
                new SyncItem("Teams", "Teams", false) { Priority = 2 },
                new SyncItem("系统通知", "Notification", false) { Priority = 3 },
            };
        }

        private List<SyncItem> LoadSyncItemsFromConfig()
        {
            try
            {
                if (File.Exists(SyncConfigPath))
                {
                    var json = File.ReadAllText(SyncConfigPath);
                    var items = JsonSerializer.Deserialize<List<SyncItem>>(json);
                    return items ?? new List<SyncItem>();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load sync config: {ex.Message}");
            }
            return new List<SyncItem>();
        }

        public void StartNotificationForwarding(
            dynamic connectedDevices,
            Func<string, string, string, string, byte[], uint, Task<bool>> sendNotificationCallback,
            Func<string, bool>? isNotificationEnabledForDevice = null)
        {
            // Stop existing listener before replacing runtime references.
            StopNotificationForwarding();

            _connectedDevicesRef = connectedDevices;
            _sendNotificationCallback = sendNotificationCallback;
            _isNotificationEnabledForDevice = isNotificationEnabledForDevice;

            _notificationListenerCts = new CancellationTokenSource();

            // Start background task to listen for notifications
            _ = Task.Run(async () => await ListenForNotificationsAsync(_notificationListenerCts.Token));

            System.Diagnostics.Debug.WriteLine("Notification forwarding started");
        }

        public void StopNotificationForwarding()
        {
            try
            {
                _notificationListenerCts?.Cancel();
                _notificationListenerCts?.Dispose();
                _notificationListenerCts = null;
                _isNotificationEnabledForDevice = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error stopping notification forwarding: {ex.Message}");
            }

            System.Diagnostics.Debug.WriteLine("Notification forwarding stopped");
        }

        public void StartTeamsForwarding(
            dynamic connectedDevices,
            Func<string, string, string, string, byte[], uint, Task<bool>> sendNotificationCallback,
            Func<string, bool>? isTeamsEnabledForDevice = null)
        {
            StopTeamsForwarding();

            _connectedDevicesRef = connectedDevices;
            _sendTeamsNotificationCallback = sendNotificationCallback;
            _isTeamsEnabledForDevice = isTeamsEnabledForDevice;

            _teamsListenerCts = new CancellationTokenSource();
            _teamsIconData = LoadTeamsIconData();
            _ = Task.Run(async () => await ListenForTeamsSignalsAsync(_teamsListenerCts.Token));

            WriteNotificationLog("Teams forwarding started", "TeamsListener");
        }

        public void StopTeamsForwarding()
        {
            try
            {
                _teamsListenerCts?.Cancel();
                _teamsListenerCts?.Dispose();
                _teamsListenerCts = null;
                _isTeamsEnabledForDevice = null;
                StopTeamsHookThread();
            }
            catch (Exception ex)
            {
                WriteNotificationLog($"Error stopping Teams forwarding: {ex.Message}", "TeamsListener");
            }

            WriteNotificationLog("Teams forwarding stopped", "TeamsListener");
        }

        public static void WriteNotificationLog(string message, string category = "Notification")
        {
            try
            {
                string logDir = Path.Combine(AppContext.BaseDirectory, "logs");
                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                string logFile = Path.Combine(logDir, $"notification-{DateTime.Now:yyyy-MM-dd}.log");
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string logMessage = $"[{timestamp}] [{category}] {message}";

                lock (typeof(SyncItemService))
                {
                    File.AppendAllText(logFile, logMessage + Environment.NewLine);
                }

                System.Diagnostics.Debug.WriteLine(logMessage);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to write notification log: {ex.Message}");
            }
        }

        private async Task ListenForNotificationsAsync(
            CancellationToken cancellationToken)
        {
            const string logName = "Microsoft-Windows-PushNotification-Platform/Operational";
            SyncItemService.WriteNotificationLog("=== Notification Listener Started ===", "Listener");

            EventLogWatcher watcher = null;
            try
            {
                // Verify event log exists
                try
                {
                    var testQuery = new EventLogQuery(logName, PathType.LogName, "*[System[(EventID=3052)]]");
                    using (var reader = new EventLogReader(testQuery))
                    {
                        // Just verify we can read
                    }
                    SyncItemService.WriteNotificationLog($"✓ Event log available: {logName}", "Listener");
                }
                catch (Exception ex)
                {
                    SyncItemService.WriteNotificationLog($"✗ Event log not found: {logName}. Error: {ex.Message}", "Listener");
                    return;
                }

                var sentNotificationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Create query for real-time event watching
                string queryText = "*[System[(EventID=3052)]]";
                EventLogQuery query = new EventLogQuery(logName, PathType.LogName, queryText);
                
                watcher = new EventLogWatcher(query);

                // Register event handler
                watcher.EventRecordWritten += (sender, args) =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        EventRecord record = args.EventRecord;
                        if (record == null)
                        {
                            return;
                        }

                        ProcessNotificationEvent(record, sentNotificationIds);
                    }
                    catch (Exception ex)
                    {
                        SyncItemService.WriteNotificationLog($"✗ Error in event handler: {ex.Message}", "Listener");
                    }
                };

                SyncItemService.WriteNotificationLog("Event handler registered", "Listener");

                // Enable watcher
                watcher.Enabled = true;
                SyncItemService.WriteNotificationLog("✓ EventLogWatcher enabled, listening for new events...", "Listener");

                // Keep the listener alive
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(500, cancellationToken);
                }

                watcher.Enabled = false;
                SyncItemService.WriteNotificationLog("EventLogWatcher disabled", "Listener");
            }
            catch (OperationCanceledException)
            {
                SyncItemService.WriteNotificationLog("Listener cancelled", "Listener");
            }
            catch (Exception ex)
            {
                SyncItemService.WriteNotificationLog($"✗ FATAL ERROR: {ex.Message}\n{ex.StackTrace}", "Listener");
            }
            finally
            {
                if (watcher != null)
                {
                    try
                    {
                        watcher.Enabled = false;
                        watcher.Dispose();
                    }
                    catch { }
                }
            }

            SyncItemService.WriteNotificationLog("=== Notification Listener Stopped ===", "Listener");
        }

        private void ProcessNotificationEvent(EventRecord record, HashSet<string> sentNotificationIds)
        {
            try
            {
                SyncItemService.WriteNotificationLog($"Event received: ID={record.Id}, Time={record.TimeCreated:yyyy-MM-dd HH:mm:ss.fff}", "Listener");

                string appId = ExtractAppIdFromEventRecord(record);
                if (string.IsNullOrEmpty(appId))
                {
                    SyncItemService.WriteNotificationLog("No AppUserModelId found in event", "Listener");
                    return;
                }

                SyncItemService.WriteNotificationLog($"AppId extracted: {appId}", "Listener");

                // Create unique notification ID
                string notificationId = $"{appId}_{record.TimeCreated?.Ticks ?? DateTime.UtcNow.Ticks}";
                if (sentNotificationIds.Contains(notificationId))
                {
                    return;
                }

                sentNotificationIds.Add(notificationId);
                if (sentNotificationIds.Count > 1000)
                {
                    sentNotificationIds.Clear();
                }

                // Get app display name
                string title = GetApplicationDisplayName(appId) ?? appId;

                // Load cached icon
                byte[] iconData = LoadCachedNotificationIcon(appId);

                SyncItemService.WriteNotificationLog($"✓ Captured: {title} ({appId}), Icon: {(iconData?.Length ?? 0)} bytes", "Listener");

                // Send to connected devices - use real-time _connectedDevicesRef member variable
                try
                {
                    if (_connectedDevicesRef == null)
                    {
                        SyncItemService.WriteNotificationLog("Connected devices reference is null", "Listener");
                        return;
                    }

                    // Safely get count using reflection
                    int deviceCount = 0;
                    try
                    {
                        var countProp = _connectedDevicesRef.GetType().GetProperty("Count");
                        if (countProp != null)
                        {
                            deviceCount = (int)countProp.GetValue(_connectedDevicesRef);
                        }
                    }
                    catch
                    {
                        SyncItemService.WriteNotificationLog("Failed to get device count via reflection", "Listener");
                        return;
                    }

                    SyncItemService.WriteNotificationLog($"Connected devices count: {deviceCount}", "Listener");
                    
                    if (deviceCount > 0 && _sendNotificationCallback != null)
                    {
                        // Get device keys
                        var keysProp = _connectedDevicesRef.GetType().GetProperty("Keys");
                        var keys = keysProp?.GetValue(_connectedDevicesRef) as dynamic;
                        
                        if (keys != null)
                        {
                            // Get the Values property to access device entries
                            var valuesProp = _connectedDevicesRef.GetType().GetProperty("Values");
                            var values = valuesProp?.GetValue(_connectedDevicesRef) as dynamic;
                            
                            // Filter only Connected devices (Status == 2)
                            var keysList = ((System.Collections.IEnumerable)keys).Cast<string>().ToList();
                            var valuesList = ((System.Collections.IEnumerable)values).Cast<dynamic>().ToList();
                            
                            var connectedDevices = new List<(string key, dynamic value)>();
                            for (int i = 0; i < keysList.Count && i < valuesList.Count; i++)
                            {
                                var deviceEntry = valuesList[i];
                                string deviceKey = keysList[i];
                                // Check if Status == Connected (2)
                                var statusProp = deviceEntry.GetType().GetProperty("Status");
                                if (statusProp != null)
                                {
                                    try
                                    {
                                        var statusObj = statusProp.GetValue(deviceEntry);
                                        int status = Convert.ToInt32(statusObj);
                                        bool notificationEnabled = _isNotificationEnabledForDevice?.Invoke(deviceKey) ?? true;
                                        if (status == 2 && notificationEnabled) // DeviceConnectionVisualState.Connected = 2
                                        {
                                            connectedDevices.Add((deviceKey, deviceEntry));
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        SyncItemService.WriteNotificationLog($"Error checking device status for {deviceKey}: {ex.Message}", "Listener");
                                    }
                                }
                            }
                            
                            SyncItemService.WriteNotificationLog($"Connected devices (status=Connected & NotificationEnabled): {string.Join(", ", connectedDevices.Select(d => d.key))} ({connectedDevices.Count} of {deviceCount})", "Listener");
                            
                            if (connectedDevices.Count > 0)
                            {
                                uint notifyIdHash = (uint)notificationId.GetHashCode();

                                int sentCount = 0;
                                var sendStartTime = DateTime.Now;
                                SyncItemService.WriteNotificationLog($"[PERF] Starting to send notification with icon ({iconData?.Length ?? 0} bytes) to {connectedDevices.Count} device(s)...", "Listener");
                                
                                foreach (var (deviceKey, _) in connectedDevices)
                                {
                                    var deviceSendStart = DateTime.Now;
                                    _ = _sendNotificationCallback(deviceKey, appId, title, "New notification", iconData, notifyIdHash);
                                    sentCount++;
                                }

                                var totalElapsed = DateTime.Now - sendStartTime;
                                SyncItemService.WriteNotificationLog($"✓ Sent to {sentCount} connected device(s) in {totalElapsed.TotalMilliseconds:F2}ms", "Listener");
                            }
                            else
                            {
                                SyncItemService.WriteNotificationLog("No connected devices to forward (all devices disconnected or handshaking)", "Listener");
                            }
                        }
                    }
                    else
                    {
                        SyncItemService.WriteNotificationLog($"No devices to forward (count: {deviceCount}, callback: {(_sendNotificationCallback != null ? "ready" : "null")})", "Listener");
                    }
                }
                catch (Exception ex)
                {
                    SyncItemService.WriteNotificationLog($"✗ Error sending to devices: {ex.Message}\n{ex.StackTrace}", "Listener");
                }
            }
            catch (Exception ex)
            {
                SyncItemService.WriteNotificationLog($"✗ Error in ProcessNotificationEvent: {ex.Message}", "Listener");
            }
        }

        private string ExtractAppIdFromEventRecord(EventRecord record)
        {
            try
            {
                string xmlStr = record.ToXml();
                
                // Log raw XML for debugging (first 500 chars)
                SyncItemService.WriteNotificationLog($"Event XML (first 500 chars): {xmlStr.Substring(0, Math.Min(500, xmlStr.Length))}", "Listener");
                
                var xml = System.Xml.Linq.XDocument.Parse(xmlStr);
                var data = xml.Descendants("Data");

                int dataCount = 0;
                foreach (var element in data)
                {
                    dataCount++;
                    var nameAttr = element.Attribute("Name");
                    string elementName = nameAttr?.Value ?? "(no name)";
                    string elementValue = element.Value ?? "(empty)";
                    
                    // Log first few data elements for debugging
                    if (dataCount <= 5)
                    {
                        SyncItemService.WriteNotificationLog($"  Data[{dataCount}]: {elementName} = {elementValue}", "Listener");
                    }
                    
                    if (nameAttr?.Value == "AppUserModelId")
                    {
                        return element.Value;
                    }
                }
                
                SyncItemService.WriteNotificationLog($"  Total {dataCount} data elements, but no AppUserModelId found", "Listener");
                
                // Try alternative paths
                SyncItemService.WriteNotificationLog($"Root element: {xml.Root?.Name}", "Listener");
                var allElements = xml.Descendants().ToList();
                SyncItemService.WriteNotificationLog($"Total descendants: {allElements.Count}", "Listener");
                
                // Look for AppUserModelId anywhere in the XML
                var appIdElement = xml.Descendants().FirstOrDefault(e => 
                    e.Attribute("Name")?.Value == "AppUserModelId" ||
                    e.Name.LocalName == "AppUserModelId");
                    
                if (appIdElement != null)
                {
                    SyncItemService.WriteNotificationLog($"Found AppUserModelId via alternative search: {appIdElement.Value}", "Listener");
                    return appIdElement.Value;
                }
            }
            catch (Exception ex)
            {
                SyncItemService.WriteNotificationLog($"Error parsing event record: {ex.Message}\n{ex.StackTrace}", "Listener");
            }

            return null;
        }

        private string GetApplicationDisplayName(string appId)
        {
            try
            {
                // Try to get from registry
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings\{appId}"))
                {
                    if (key != null)
                    {
                        object displayName = key.GetValue("DisplayName");
                        if (displayName != null)
                        {
                            return displayName.ToString();
                        }
                    }
                }
            }
            catch { }

            // Fall back to using the app ID
            return appId;
        }

        private EventRecord[] GetWinEvent(EventLogQuery query, int maxEvents)
        {
            using (var reader = new EventLogReader(query))
            {
                var records = new List<EventRecord>();
                EventRecord record;
                int count = 0;
                while ((record = reader.ReadEvent()) != null && count < maxEvents)
                {
                    records.Add(record);
                    count++;
                }
                return records.ToArray();
            }
        }

        private byte[] LoadCachedNotificationIcon(string appId)
        {
            try
            {
                string tempDir = Path.Combine(AppContext.BaseDirectory, "temp");
                string iconPath = Path.Combine(tempDir, $"{BuildSafeFileName(appId)}.png");

                if (File.Exists(iconPath))
                {
                    return File.ReadAllBytes(iconPath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading cached icon for {appId}: {ex.Message}");
            }

            return null;
        }

        private async Task ListenForTeamsSignalsAsync(CancellationToken cancellationToken)
        {
            WriteNotificationLog("=== Teams Listener Started ===", "TeamsListener");
            try
            {
                StartTeamsHookThread(cancellationToken);

                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(500, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                WriteNotificationLog("Teams listener cancelled", "TeamsListener");
            }
            catch (Exception ex)
            {
                WriteNotificationLog($"Teams listener fatal error: {ex.Message}", "TeamsListener");
            }
            finally
            {
                StopTeamsHookThread();
                WriteNotificationLog("=== Teams Listener Stopped ===", "TeamsListener");
            }
        }

        private byte[]? LoadTeamsIconData()
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "resources", "Teams.png"),
                Path.Combine(AppContext.BaseDirectory, "Resources", "Teams.png"),
            };

            foreach (string path in candidates)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        byte[] bytes = File.ReadAllBytes(path);
                        WriteNotificationLog($"Teams icon loaded: {path} ({bytes.Length} bytes)", "TeamsListener");
                        return bytes;
                    }
                }
                catch (Exception ex)
                {
                    WriteNotificationLog($"Failed to load Teams icon from {path}: {ex.Message}", "TeamsListener");
                }
            }

            WriteNotificationLog("Teams icon not found, forwarding without icon", "TeamsListener");
            return null;
        }

        private void StartTeamsHookThread(CancellationToken cancellationToken)
        {
            _teamsHookThread = new Thread(TeamsHookThreadMain)
            {
                IsBackground = true,
                Name = "TeamsUIHookThread",
            };
            _teamsHookThread.SetApartmentState(ApartmentState.STA);
            _teamsHookThread.Start();

            for (int i = 0; i < 40 && !_teamsHookStarted && !cancellationToken.IsCancellationRequested; i++)
            {
                Thread.Sleep(50);
            }

            if (!_teamsHookStarted)
            {
                throw new InvalidOperationException("Teams UI hook thread did not start");
            }

            WriteNotificationLog("Teams WinEvent hook started", "TeamsListener");
        }

        private void TeamsHookThreadMain()
        {
            try
            {
                _teamsHookThreadId = GetCurrentThreadId();
                _teamsWinEventProc = (hook, evt, hwnd, idObject, idChild, thread, time) =>
                {
                    if (hwnd == IntPtr.Zero)
                    {
                        return;
                    }

                    if (idObject != OBJID_WINDOW && idObject != OBJID_CLIENT)
                    {
                        return;
                    }

                    OnTeamsUiEvent(hwnd, evt);
                };

                RegisterTeamsEventHook(EVENT_OBJECT_SHOW);
                if (_teamsWinEventHooks.Count == 0)
                {
                    throw new InvalidOperationException("No Teams WinEventHook registered");
                }

                _teamsHookStarted = true;

                MSG msg;
                while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                WriteNotificationLog($"Teams hook thread error: {ex.Message}", "TeamsListener");
            }
            finally
            {
                foreach (IntPtr hook in _teamsWinEventHooks)
                {
                    try
                    {
                        UnhookWinEvent(hook);
                    }
                    catch
                    {
                        // Ignore hook cleanup errors.
                    }
                }

                _teamsWinEventHooks.Clear();
                _teamsHookStarted = false;
                _teamsHookThreadId = 0;
            }
        }

        private void RegisterTeamsEventHook(uint evt)
        {
            if (_teamsWinEventProc == null)
            {
                return;
            }

            IntPtr handle = SetWinEventHook(
                evt,
                evt,
                IntPtr.Zero,
                _teamsWinEventProc,
                0,
                0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

            if (handle != IntPtr.Zero)
            {
                _teamsWinEventHooks.Add(handle);
            }
        }

        private void StopTeamsHookThread()
        {
            if (_teamsHookThreadId != 0)
            {
                try
                {
                    PostThreadMessage(_teamsHookThreadId, WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
                }
                catch
                {
                    // Ignore quit message failures.
                }
            }

            if (_teamsHookThread != null)
            {
                try
                {
                    _teamsHookThread.Join(1500);
                }
                catch
                {
                    // Ignore thread join failures.
                }
                finally
                {
                    _teamsHookThread = null;
                }
            }
        }

        private void OnTeamsUiEvent(IntPtr hwnd, uint evt)
        {
            try
            {
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0)
                {
                    return;
                }

                System.Diagnostics.Process proc;
                try
                {
                    proc = System.Diagnostics.Process.GetProcessById((int)pid);
                }
                catch
                {
                    return;
                }

                if (!string.Equals(proc.ProcessName, "ms-teams", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string windowTitle = GetWindowTitle(hwnd);
                string className = GetWindowClassName(hwnd);
                EmitTeamsSignal(proc.ProcessName, className, windowTitle, evt);
            }
            catch (Exception ex)
            {
                WriteNotificationLog($"OnTeamsUiEvent error: {ex.Message}", "TeamsListener");
            }
        }

        private void EmitTeamsSignal(string processName, string className, string windowTitle, uint evt)
        {
            if (!string.Equals(processName, "ms-teams", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string safeTitle = string.IsNullOrWhiteSpace(windowTitle) ? "(no-title)" : windowTitle;
            string safeClass = string.IsNullOrWhiteSpace(className) ? "(no-class)" : className;
            string signature = processName + "|" + safeClass + "|" + safeTitle + "|" + evt;

            lock (_teamsEventLock)
            {
                if (signature == _lastTeamsEventSignature && (DateTime.Now - _lastTeamsEventTime).TotalSeconds < 2)
                {
                    return;
                }

                _lastTeamsEventSignature = signature;
                _lastTeamsEventTime = DateTime.Now;
            }

            string messageText = string.IsNullOrWhiteSpace(windowTitle)
                ? "New Teams notification"
                : windowTitle.Trim();

            WriteNotificationLog($"Teams signal captured: class={safeClass}, title={safeTitle}", "TeamsListener");
            _ = ForwardTeamsNotificationAsync(messageText);
        }

        private async Task ForwardTeamsNotificationAsync(string messageText)
        {
            try
            {
                if (_connectedDevicesRef == null || _sendTeamsNotificationCallback == null)
                {
                    return;
                }

                var keysProp = _connectedDevicesRef.GetType().GetProperty("Keys");
                var valuesProp = _connectedDevicesRef.GetType().GetProperty("Values");
                var keys = keysProp?.GetValue(_connectedDevicesRef) as dynamic;
                var values = valuesProp?.GetValue(_connectedDevicesRef) as dynamic;
                if (keys == null || values == null)
                {
                    return;
                }

                var keysList = ((System.Collections.IEnumerable)keys).Cast<string>().ToList();
                var valuesList = ((System.Collections.IEnumerable)values).Cast<dynamic>().ToList();

                var enabledDeviceKeys = new List<string>();
                for (int i = 0; i < keysList.Count && i < valuesList.Count; i++)
                {
                    string deviceKey = keysList[i];
                    var entry = valuesList[i];
                    var statusProp = entry.GetType().GetProperty("Status");
                    if (statusProp == null)
                    {
                        continue;
                    }

                    int status;
                    try
                    {
                        status = Convert.ToInt32(statusProp.GetValue(entry));
                    }
                    catch
                    {
                        continue;
                    }

                    bool teamsEnabled = _isTeamsEnabledForDevice?.Invoke(deviceKey) ?? true;
                    if (status == 2 && teamsEnabled)
                    {
                        enabledDeviceKeys.Add(deviceKey);
                    }
                }

                if (enabledDeviceKeys.Count == 0)
                {
                    WriteNotificationLog("No connected devices with Teams sync enabled", "TeamsListener");
                    return;
                }

                uint notifyId = (uint)$"{TeamsAppId}_{DateTime.UtcNow.Ticks}".GetHashCode();
                byte[] iconData = _teamsIconData ?? Array.Empty<byte>();

                foreach (string deviceKey in enabledDeviceKeys)
                {
                    await _sendTeamsNotificationCallback(
                        deviceKey,
                        TeamsAppId,
                        TeamsDisplayTitle,
                        messageText,
                        iconData,
                        notifyId);
                }

                WriteNotificationLog($"Teams notification forwarded to {enabledDeviceKeys.Count} device(s)", "TeamsListener");
            }
            catch (Exception ex)
            {
                WriteNotificationLog($"ForwardTeamsNotificationAsync error: {ex.Message}", "TeamsListener");
            }
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

        private delegate void WinEventDelegate(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime);

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

    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        private static readonly string DeviceConfigPath = Path.Combine(
            AppContext.BaseDirectory,
            "devices.json");

        private WinForms.NotifyIcon _trayIcon;
        private bool _isShuttingDown = false;
        private bool _startupBootstrapTriggered;
        private readonly BluetoothDeviceManager _bluetoothDeviceManager = new();
        private readonly Dictionary<string, ConnectedDeviceEntry> _connectedDevices = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PersistedDeviceItem> _savedDevices = new(StringComparer.OrdinalIgnoreCase);
        private readonly ISyncItemProvider _syncItemService = new SyncItemService();
        private List<SyncItem> _currentSyncItems = new();
        private string _selectedDeviceKey = string.Empty;
        private List<SyncItem> _selectedDeviceSyncItems = new();
        private readonly Dictionary<string, CancellationTokenSource> _timeSyncLoops = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _timeSyncLock = new();
        private static readonly TimeSpan TimeSyncInterval = TimeSpan.FromMinutes(30);

        // Static method for logging
        public static void WriteAppLog(string message, string category = "General")
        {
            try
            {
                string logDir = Path.Combine(AppContext.BaseDirectory, "logs");
                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                string logFile = Path.Combine(logDir, $"app-{DateTime.Now:yyyy-MM-dd}.log");
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                string logMessage = $"[{timestamp}] [{category}] {message}";

                lock (typeof(MainWindow))
                {
                    File.AppendAllText(logFile, logMessage + Environment.NewLine);
                }

                System.Diagnostics.Debug.WriteLine(logMessage);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to write app log: {ex.Message}");
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            WriteAppLog("=== MainWindow Initialized ===", "Startup");
            InitializeTrayIcon();
            // 启动时隐藏窗口到系统托盘
            this.Visibility = Visibility.Hidden;
            this.WindowState = WindowState.Minimized;
            
            // 绑定标签页切换事件
            LeftTabControl.SelectionChanged += (s, e) => UpdateTabContent();
            Loaded += MainWindow_Loaded;

            if (System.Windows.Application.Current is App app)
            {
                app.ChannelSessionStateChanged += App_ChannelSessionStateChanged;
            }

            // 托盘隐藏启动时，保证自动连接逻辑也会立即执行。
            _ = RunStartupBootstrapAsync();
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await RunStartupBootstrapAsync();
        }

        private async Task RunStartupBootstrapAsync()
        {
            if (_startupBootstrapTriggered)
            {
                return;
            }

            _startupBootstrapTriggered = true;
            WriteAppLog("Startup bootstrap begin", "Startup");

            LoadSavedDevicesIntoCache();
            InitializeDeviceEntriesFromSaved();
            RefreshConnectedDeviceUi();
            InitializeSyncItems();
            await AutoConnectSavedDevicesAsync();
            RefreshConnectedDeviceUi();

            int connectedCount = _connectedDevices.Values.Count(d => d.Status == DeviceConnectionVisualState.Connected);
            WriteAppLog($"Startup bootstrap done. saved={_savedDevices.Count}, connected={connectedCount}", "Startup");
        }

        private void InitializeTrayIcon()
        {
            // 创建系统托盘图标
            _trayIcon = new WinForms.NotifyIcon();
            _trayIcon.Text = "XPanelClient";
            
            // 尝试加载自定义图标文件，否则使用默认图标
            string iconPath = @"Resources/tray-icon.ico";
            if (File.Exists(iconPath))
            {
                _trayIcon.Icon = new System.Drawing.Icon(iconPath);
            }
            else
            {
                _trayIcon.Icon = CreateDefaultTrayIcon();
            }

            // 创建托盘菜单
            var contextMenu = new WinForms.ContextMenuStrip();
            contextMenu.Items.Add("显示", null, (s, e) => ShowWindow());
            contextMenu.Items.Add("隐藏", null, (s, e) => HideWindow());
            contextMenu.Items.Add("-");
            contextMenu.Items.Add("退出", null, (s, e) => ExitApplication());

            _trayIcon.ContextMenuStrip = contextMenu;
            _trayIcon.DoubleClick += (s, e) => ToggleWindowVisibility();
            _trayIcon.Visible = true;
        }

        private System.Drawing.Icon CreateDefaultTrayIcon()
        {
            // 创建一个 16x16 的蓝色圆形图标
            var bitmap = new System.Drawing.Bitmap(16, 16);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(System.Drawing.Color.White);
                graphics.DrawEllipse(new System.Drawing.Pen(System.Drawing.Color.Blue, 2), 2, 2, 12, 12);
            }
            return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
        }

        private void ShowWindow()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Visibility = Visibility.Visible;
            this.Activate();
            this.BringIntoView();
        }

        private void HideWindow()
        {
            this.Visibility = Visibility.Hidden;
            this.WindowState = WindowState.Minimized;
        }

        private void ToggleWindowVisibility()
        {
            if (this.Visibility == Visibility.Visible && this.WindowState == WindowState.Normal)
                HideWindow();
            else
                ShowWindow();
        }

        private async void ExitApplication()
        {
            _isShuttingDown = true;
            _trayIcon?.Dispose();

            if (System.Windows.Application.Current is App app)
            {
                try
                {
                    await app.ShutdownConnectionsAsync();
                }
                catch
                {
                    // 退出阶段忽略异常，继续关闭进程。
                }
            }

            System.Windows.Application.Current.Shutdown();
        }

        private void InitializeSyncItems()
        {
            _currentSyncItems = _syncItemService.GetAllItems();
            RefreshSyncItemsUi();
        }

        private void InitializeDeviceSelectorForSync()
        {
            DeviceSelector.Items.Clear();
            DeviceSelector.SelectionChanged -= DeviceSelector_SelectionChanged;

            foreach (var device in _connectedDevices.Values.OrderBy(d => d.DeviceName))
            {
                DeviceSelector.Items.Add(new ComboBoxItem { Content = device.DeviceName, Tag = device.ChannelKey });
            }

            DeviceSelector.SelectionChanged += DeviceSelector_SelectionChanged;
        }

        private void DeviceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DeviceSelector.SelectedItem is ComboBoxItem selectedItem && selectedItem.Tag is string channelKey)
            {
                // 保存前一个设备的配置
                if (!string.IsNullOrEmpty(_selectedDeviceKey) && _savedDevices.TryGetValue(_selectedDeviceKey, out var prevDevice))
                {
                    prevDevice.SyncConfig = new List<SyncItem>(_selectedDeviceSyncItems);
                }

                // 切换到新设备
                _selectedDeviceKey = channelKey;
                RefreshSyncItemsUi();
                SaveSavedDevicesToConfig();
            }
        }

        private void RefreshSyncItemsUi()
        {
            SyncItemsPanel.Children.Clear();

            // 获取当前设备的同步配置
            if (!string.IsNullOrEmpty(_selectedDeviceKey) && _savedDevices.TryGetValue(_selectedDeviceKey, out var device))
            {
                if (device.SyncConfig.Count > 0)
                {
                    _selectedDeviceSyncItems = device.SyncConfig.OrderBy(x => x.Priority).ToList();
                }
                else
                {
                    // 初始化新设备的配置
                    _selectedDeviceSyncItems = new List<SyncItem>();
                    foreach (var defaultItem in _currentSyncItems)
                    {
                        _selectedDeviceSyncItems.Add(new SyncItem(defaultItem.Name, defaultItem.Category, defaultItem.IsEnabled)
                        {
                            Id = defaultItem.Id,
                            Priority = defaultItem.Priority,
                        });
                    }
                    device.SyncConfig = _selectedDeviceSyncItems;
                }
            }
            else
            {
                _selectedDeviceSyncItems = new List<SyncItem>(_currentSyncItems);
            }

            foreach (var item in _selectedDeviceSyncItems)
            {
                var rowBorder = new Border
                {
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255)),
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(224, 224, 224)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(0, 0, 0, 10),
                    Padding = new Thickness(12, 10, 12, 10),
                };

                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var nameText = new TextBlock
                {
                    Text = item.Name,
                    FontSize = 14,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 51, 51)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0),
                };
                Grid.SetColumn(nameText, 0);
                rowGrid.Children.Add(nameText);

                var toggleButton = new ToggleButton
                {
                    Style = FindResource("ToggleSwitchStyle") as System.Windows.Style,
                    IsChecked = item.IsEnabled,
                    Tag = item.Id,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                toggleButton.Checked += (s, e) =>
                {
                    HandleSyncItemToggled(item, true);
                };
                toggleButton.Unchecked += (s, e) =>
                {
                    HandleSyncItemToggled(item, false);
                };
                Grid.SetColumn(toggleButton, 1);
                rowGrid.Children.Add(toggleButton);

                rowBorder.Child = rowGrid;
                SyncItemsPanel.Children.Add(rowBorder);
            }
        }

        private void HandleSyncItemToggled(SyncItem item, bool isEnabled)
        {
            item.IsEnabled = isEnabled;
            _syncItemService.OnItemToggled(item);

            if (string.Equals(item.Category, "Notification", StringComparison.OrdinalIgnoreCase))
            {
                // Service layer currently only records the toggle; runtime start/stop is handled here.
                HandleNotificationSync(item);
            }

            if (string.Equals(item.Category, "Teams", StringComparison.OrdinalIgnoreCase))
            {
                // Service layer currently only records the toggle; runtime start/stop is handled here.
                HandleTeamsSync(item);
            }

            // 保存到当前选择的设备配置
            if (!string.IsNullOrEmpty(_selectedDeviceKey) && _savedDevices.TryGetValue(_selectedDeviceKey, out var device))
            {
                device.SyncConfig = new List<SyncItem>(_selectedDeviceSyncItems);
                SaveSavedDevicesToConfig();
            }

            if (string.Equals(item.Category, "Time", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(_selectedDeviceKey))
            {
                EnsureTimeSyncScheduleForDevice(_selectedDeviceKey);
            }
        }

        private void UpdateTabContent()
        {
            if (LeftTabControl.SelectedIndex >= 0)
            {
                // 更新标签页头部文字
                string[] tabHeaders = { "Device", "Synchronization", "System Settings" };
                TabHeader.Text = tabHeaders[LeftTabControl.SelectedIndex];

                // 显示/隐藏对应的内容面板
                DeviceInfoPanel.Visibility = LeftTabControl.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
                SyncPanel.Visibility = LeftTabControl.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
                SettingsPanel.Visibility = LeftTabControl.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
                AddDeviceBottomButton.Visibility = LeftTabControl.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
                DeviceSelectorPanel.Visibility = LeftTabControl.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;

                // 当切换到Synchronization页面时，刷新UI
                if (LeftTabControl.SelectedIndex == 1)
                {
                    InitializeDeviceSelectorForSync();
                    if (DeviceSelector.Items.Count > 0)
                    {
                        DeviceSelector.SelectedIndex = 0;
                    }
                }
            }
        }

        protected override void OnStateChanged(System.EventArgs e)
        {
            base.OnStateChanged(e);
            if (this.WindowState == WindowState.Minimized)
                HideWindow();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // 只有在真正关闭应用时才允许窗口关闭
            // 否则隐藏窗口，程序继续运行
            if (!_isShuttingDown)
            {
                e.Cancel = true;
                HideWindow();
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(System.EventArgs e)
        {
            if (System.Windows.Application.Current is App app)
            {
                app.ChannelSessionStateChanged -= App_ChannelSessionStateChanged;
            }

            StopAllTimeSyncSchedules();
            ((SyncItemService)_syncItemService).StopNotificationForwarding();
            ((SyncItemService)_syncItemService).StopTeamsForwarding();
            _trayIcon?.Dispose();
            _bluetoothDeviceManager.Dispose();
            base.OnClosed(e);
        }

        private void App_ChannelSessionStateChanged(object? sender, App.ChannelSessionStateChangedEventArgs e)
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (!_connectedDevices.TryGetValue(e.Key, out var existing))
                {
                    return;
                }

                if (e.State == App.ChannelSessionState.Disconnected)
                {
                    StopTimeSyncSchedule(e.Key);
                    _connectedDevices[e.Key] = existing with
                    {
                        Status = DeviceConnectionVisualState.Disconnected,
                        SessionId = null,
                    };
                    UpdateNotificationForwardingState("channel-disconnected");
                    UpdateTeamsForwardingState("channel-disconnected");
                    RefreshConnectedDeviceUi();
                    return;
                }

                _connectedDevices[e.Key] = existing with
                {
                    Status = DeviceConnectionVisualState.Connected,
                    SessionId = e.SessionId,
                    CommunicationChannel = e.Channel ?? existing.CommunicationChannel,
                };

                EnsureTimeSyncScheduleForDevice(e.Key);
                UpdateNotificationForwardingState("channel-connected");
                UpdateTeamsForwardingState("channel-connected");
                RefreshConnectedDeviceUi();
            });
        }

        private void AddDevice_Click(object sender, RoutedEventArgs e)
        {
            // 打开添加设备窗口
            AddDeviceWindow addDeviceWindow = new AddDeviceWindow();
            addDeviceWindow.Owner = this;
            bool? result = addDeviceWindow.ShowDialog();

            if (result == true && !string.IsNullOrWhiteSpace(addDeviceWindow.ConnectedDeviceName))
            {
                string methodDisplay = string.IsNullOrWhiteSpace(addDeviceWindow.ConnectedMethodDisplay)
                    ? "Unknown"
                    : addDeviceWindow.ConnectedMethodDisplay;
                string address = string.IsNullOrWhiteSpace(addDeviceWindow.ConnectedDeviceAddress)
                    ? Guid.NewGuid().ToString("N")
                    : addDeviceWindow.ConnectedDeviceAddress;
                string normalizedChannel = NormalizeChannelLabel(methodDisplay);
                string channelKey = BuildChannelKey(normalizedChannel, address);

                _connectedDevices[channelKey] = new ConnectedDeviceEntry(
                    channelKey,
                    addDeviceWindow.ConnectedDeviceName,
                    normalizedChannel,
                    addDeviceWindow.ConnectedSessionId,
                    DeviceConnectionVisualState.Connected,
                    null);

                var syncConfig = new List<SyncItem>();
                foreach (var item in _currentSyncItems)
                {
                    syncConfig.Add(new SyncItem(item.Name, item.Category, item.IsEnabled)
                    {
                        Id = item.Id,
                        Priority = item.Priority,
                    });
                }

                _savedDevices[channelKey] = new PersistedDeviceItem
                {
                    DeviceName = addDeviceWindow.ConnectedDeviceName,
                    DeviceAddress = address,
                    Channel = normalizedChannel,
                    BleAddressType = normalizedChannel == "BLE"
                        ? addDeviceWindow.ConnectedBleAddressType.ToString()
                        : BleAddressType.Unknown.ToString(),
                    SyncConfig = syncConfig,
                };

                WriteAppLog($"Manual add device success: key={channelKey}, name={addDeviceWindow.ConnectedDeviceName}, addr={address}", "Device");

                EnsureTimeSyncScheduleForDevice(channelKey);

                RefreshConnectedDeviceUi();
                SaveSavedDevicesToConfig();
            }
        }

        private async void RemoveDevice_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button removeButton || removeButton.Tag is not string channelKey)
            {
                return;
            }

            removeButton.IsEnabled = false;

            bool removedFromChannel = true;
            if (_connectedDevices.TryGetValue(channelKey, out var targetDevice) &&
                targetDevice.Status == DeviceConnectionVisualState.Connected &&
                targetDevice.SessionId.HasValue)
            {
                removedFromChannel = false;
                if (System.Windows.Application.Current is App app)
                {
                    removedFromChannel = await app.DisconnectAndRemoveChannelAsync(channelKey, targetDevice.SessionId.Value);
                }
            }

            bool needResumeTimeSync = IsTimeSyncEnabledForDevice(channelKey);
            StopTimeSyncSchedule(channelKey);

            if (!removedFromChannel)
            {
                if (needResumeTimeSync)
                {
                    EnsureTimeSyncScheduleForDevice(channelKey);
                }

                removeButton.IsEnabled = true;
                System.Windows.MessageBox.Show("Failed to disconnect device.", "Remove Device", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _connectedDevices.Remove(channelKey);
            _savedDevices.Remove(channelKey);
            UpdateNotificationForwardingState("device-removed");
            UpdateTeamsForwardingState("device-removed");
            RefreshConnectedDeviceUi();
            SaveSavedDevicesToConfig();
        }

        private async Task TryAutoStartNotificationForwardingAsync(string channelKey, PersistedDeviceItem deviceConfig)
        {
            try
            {
                WriteAppLog($"Checking sync config for device: {channelKey}", "AutoSync");
                
                if (deviceConfig?.SyncConfig == null || deviceConfig.SyncConfig.Count == 0)
                {
                    WriteAppLog($"No sync config found for device: {channelKey}", "AutoSync");
                    return;
                }

                // Find notification sync item
                var notificationConfig = deviceConfig.SyncConfig.FirstOrDefault(x => x.Category == "Notification");
                if (notificationConfig != null)
                {
                    WriteAppLog($"Notification config found: IsEnabled={notificationConfig.IsEnabled}, DeviceKey={channelKey}", "AutoNotification");

                    if (notificationConfig.IsEnabled)
                    {
                        WriteAppLog($"Auto-starting notification forwarding for device: {channelKey}", "AutoNotification");
                        UpdateNotificationForwardingState("auto-start");
                        await ((SyncItemService)_syncItemService).CacheNotificationAppIconsAsync();
                        WriteAppLog($"Notification forwarding auto-started for device: {channelKey}", "AutoNotification");
                    }
                }

                var teamsConfig = deviceConfig.SyncConfig.FirstOrDefault(x => x.Category == "Teams");
                if (teamsConfig != null)
                {
                    WriteAppLog($"Teams config found: IsEnabled={teamsConfig.IsEnabled}, DeviceKey={channelKey}", "AutoTeams");
                    if (teamsConfig.IsEnabled)
                    {
                        WriteAppLog($"Auto-starting Teams forwarding for device: {channelKey}", "AutoTeams");
                        UpdateTeamsForwardingState("auto-start");
                    }
                }
            }
            catch (Exception ex)
            {
                WriteAppLog($"ERROR in TryAutoStartNotificationForwardingAsync: {ex.Message}\n{ex.StackTrace}", "AutoNotification");
            }
        }

        private void HandleNotificationSync(SyncItem item)
        {
            System.Diagnostics.Debug.WriteLine($"Notification Sync: {item.Name} - {(item.IsEnabled ? "Enabled" : "Disabled")}");
            WriteAppLog($"HandleNotificationSync called: IsEnabled={item.IsEnabled}, Connected devices={_connectedDevices.Count}", "NotificationSync");

            UpdateNotificationForwardingState("ui-toggle");

            if (item.IsEnabled)
            {
                WriteAppLog("Starting icon cache update", "NotificationSync");
                _ = ((SyncItemService)_syncItemService).CacheNotificationAppIconsAsync();
            }
        }

        private void HandleTeamsSync(SyncItem item)
        {
            System.Diagnostics.Debug.WriteLine($"Teams Sync: {item.Name} - {(item.IsEnabled ? "Enabled" : "Disabled")}");
            WriteAppLog($"HandleTeamsSync called: IsEnabled={item.IsEnabled}, Connected devices={_connectedDevices.Count}", "TeamsSync");
            UpdateTeamsForwardingState("ui-toggle");
        }

        private bool IsNotificationSyncEnabledForDevice(string channelKey)
        {
            if (!_savedDevices.TryGetValue(channelKey, out var device) || device.SyncConfig == null)
            {
                return false;
            }

            return device.SyncConfig.Any(item =>
                string.Equals(item.Category, "Notification", StringComparison.OrdinalIgnoreCase) && item.IsEnabled);
        }

        private bool IsTeamsSyncEnabledForDevice(string channelKey)
        {
            if (!_savedDevices.TryGetValue(channelKey, out var device) || device.SyncConfig == null)
            {
                return false;
            }

            return device.SyncConfig.Any(item =>
                string.Equals(item.Category, "Teams", StringComparison.OrdinalIgnoreCase) && item.IsEnabled);
        }

        private void UpdateNotificationForwardingState(string reason)
        {
            int enabledConnectedCount = _connectedDevices.Values.Count(device =>
                device.Status == DeviceConnectionVisualState.Connected &&
                device.SessionId.HasValue &&
                IsNotificationSyncEnabledForDevice(device.ChannelKey));

            if (enabledConnectedCount <= 0)
            {
                WriteAppLog($"Stopping notification forwarding (reason={reason}, enabledConnectedDevices=0)", "NotificationSync");
                ((SyncItemService)_syncItemService).StopNotificationForwarding();
                return;
            }

            WriteAppLog($"Starting notification forwarding (reason={reason}, enabledConnectedDevices={enabledConnectedCount})", "NotificationSync");
            ((SyncItemService)_syncItemService).StartNotificationForwarding(
                connectedDevices: _connectedDevices,
                sendNotificationCallback: SendNotificationToDeviceAsync,
                isNotificationEnabledForDevice: IsNotificationSyncEnabledForDevice);
        }

        private void UpdateTeamsForwardingState(string reason)
        {
            int enabledConnectedCount = _connectedDevices.Values.Count(device =>
                device.Status == DeviceConnectionVisualState.Connected &&
                device.SessionId.HasValue &&
                IsTeamsSyncEnabledForDevice(device.ChannelKey));

            if (enabledConnectedCount <= 0)
            {
                WriteAppLog($"Stopping Teams forwarding (reason={reason}, enabledConnectedDevices=0)", "TeamsSync");
                ((SyncItemService)_syncItemService).StopTeamsForwarding();
                return;
            }

            WriteAppLog($"Starting Teams forwarding (reason={reason}, enabledConnectedDevices={enabledConnectedCount})", "TeamsSync");
            ((SyncItemService)_syncItemService).StartTeamsForwarding(
                connectedDevices: _connectedDevices,
                sendNotificationCallback: SendNotificationToDeviceAsync,
                isTeamsEnabledForDevice: IsTeamsSyncEnabledForDevice);
        }

        /// <summary>
        /// Send notification to a specific connected device
        /// </summary>
        private async Task<bool> SendNotificationToDeviceAsync(string deviceKey, string appId, 
            string title, string text, byte[] iconData, uint notifyId)
        {
            var methodStartTime = DateTime.Now;
            
            if (!_connectedDevices.TryGetValue(deviceKey, out var deviceEntry))
            {
                System.Diagnostics.Debug.WriteLine($"Device {deviceKey} not found");
                return false;
            }

            try
            {
                var channel = deviceEntry.CommunicationChannel;
                if (channel?.State != ConnectionState.Connected)
                {
                    System.Diagnostics.Debug.WriteLine($"Device {deviceKey} not connected");
                    return false;
                }

                // Get session ID from device entry
                if (!deviceEntry.SessionId.HasValue)
                {
                    System.Diagnostics.Debug.WriteLine($"Device {deviceKey} has no session ID");
                    return false;
                }

                var buildStartTime = DateTime.Now;
                
                // Build XPF notification frame
                var frame = new XpfFrame
                {
                    MessageType = XpfMessageType.Cmd,
                    Flags = 0x01, // need_ack
                    QosLevel = 1,
                    AppId = XpfProtocolConstants.AppIdNotificationMgr,
                    OpCode = XpfProtocolConstants.OpNotifyPush,
                    MsgId = (uint)Environment.TickCount,
                    TimestampSec = (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                };

                // Add TLVs - session_id is required by protocol
                frame.Tlvs[XpfProtocolConstants.TlvSessionId] = XpfCodec.EncodeUInt32(deviceEntry.SessionId.Value);
                frame.Tlvs[XpfProtocolConstants.TlvNotifyId] = XpfCodec.EncodeUInt32(notifyId);
                frame.Tlvs[XpfProtocolConstants.TlvNotifyTitle] = XpfCodec.EncodeUtf8(title);
                frame.Tlvs[XpfProtocolConstants.TlvNotifyText] = XpfCodec.EncodeUtf8(text);
                frame.Tlvs[XpfProtocolConstants.TlvNotifyChannel] = new byte[] { 1 }; // general
                frame.Tlvs[XpfProtocolConstants.TlvNotifyPriority] = new byte[] { 1 }; // normal

                // Add icon if available
                int iconSize = 0;
                string iconFileName = null;
                if (iconData != null && iconData.Length > 0)
                {
                    iconSize = iconData.Length;
                    // Generate safe file name for icon
                    var invalidChars = Path.GetInvalidFileNameChars();
                    var chars = appId.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray();
                    string safeFileName = new string(chars);
                    iconFileName = $"{safeFileName}.png";
                    
                    frame.Tlvs[XpfProtocolConstants.TlvNotifyImageMode] = new byte[] { 2 }; // inline
                    frame.Tlvs[XpfProtocolConstants.TlvNotifyImageFormat] = new byte[] { 1 }; // PNG
                    frame.Tlvs[XpfProtocolConstants.TlvNotifyImageData] = iconData;
                    frame.Tlvs[XpfProtocolConstants.TlvNotifyImageSize] = XpfCodec.EncodeUInt32((uint)iconData.Length);
                }

                var buildElapsed = DateTime.Now - buildStartTime;
                
                var serializeStartTime = DateTime.Now;
                byte[] payload = XpfCodec.Serialize(frame);
                var serializeElapsed = DateTime.Now - serializeStartTime;

                // Log XPF raw data in hex format for device comparison
                string hexPayload = BitConverter.ToString(payload).Replace("-", " ");
                SyncItemService.WriteNotificationLog(
                    $"[XPF_RAW] {deviceKey}: sessionId=0x{deviceEntry.SessionId.Value:X8}, appId={appId}, iconFile={iconFileName ?? "none"}, " +
                    $"payloadSize={payload.Length}B, hex=[{hexPayload}]", "Listener");

                var sendStartTime = DateTime.Now;
                bool result = await channel.SendAsync(payload);
                var sendElapsed = DateTime.Now - sendStartTime;
                
                var totalElapsed = DateTime.Now - methodStartTime;
                
                SyncItemService.WriteNotificationLog(
                    $"[PERF] {deviceKey}: icon={iconSize}B, build={buildElapsed.TotalMilliseconds:F2}ms, " +
                    $"serialize={serializeElapsed.TotalMilliseconds:F2}ms, send={sendElapsed.TotalMilliseconds:F2}ms, " +
                    $"total={totalElapsed.TotalMilliseconds:F2}ms, result={result}", "Listener");
                
                return result;
            }
            catch (Exception ex)
            {
                var errorElapsed = DateTime.Now - methodStartTime;
                System.Diagnostics.Debug.WriteLine($"Error sending notification to device {deviceKey}: {ex.Message}");
                SyncItemService.WriteNotificationLog($"[ERROR] {deviceKey}: {ex.Message} (elapsed: {errorElapsed.TotalMilliseconds:F2}ms)", "Listener");
                return false;
            }
        }

        private void RefreshConnectedDeviceUi()
        {
            ConnectedDevicesPanel.Children.Clear();

            if (_connectedDevices.Count == 0)
            {
                NoConnectedDevicesText.Visibility = Visibility.Visible;
                ConnectedDevicesPanel.Visibility = Visibility.Collapsed;

                NoDeviceStatusText.Visibility = Visibility.Visible;
                DeviceStatusGroupPanel.Visibility = Visibility.Collapsed;
                DeviceStatusGroupPanel.Children.Clear();
                return;
            }

            NoConnectedDevicesText.Visibility = Visibility.Collapsed;
            ConnectedDevicesPanel.Visibility = Visibility.Visible;

            var orderedDevices = _connectedDevices.Values
                .OrderBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.ChannelKey, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var device in orderedDevices)
            {
                var rowBorder = new Border
                {
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 255, 255)),
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(224, 224, 224)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(0, 0, 0, 10),
                    Padding = new Thickness(12, 10, 12, 10),
                };

                var rowGrid = new Grid();
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var leftGroup = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0),
                };
                Grid.SetColumn(leftGroup, 0);

                leftGroup.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 10,
                    Height = 10,
                    Fill = GetStatusBrush(device.Status),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });

                var deviceText = new TextBlock
                {
                    Text = $"{device.DeviceName} ({device.MethodDisplay})",
                    FontSize = 20,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 51, 51)),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                };
                leftGroup.Children.Add(deviceText);

                var removeButton = new System.Windows.Controls.Button
                {
                    Content = "Remove",
                    Style = (Style)FindResource("RoundedRectButtonStyle"),
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 67, 54)),
                    Foreground = System.Windows.Media.Brushes.White,
                    Padding = new Thickness(16, 8, 16, 8),
                    FontSize = 12,
                    Tag = device.ChannelKey,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                removeButton.Click += RemoveDevice_Click;
                Grid.SetColumn(removeButton, 1);

                rowGrid.Children.Add(leftGroup);
                rowGrid.Children.Add(removeButton);
                rowBorder.Child = rowGrid;
                ConnectedDevicesPanel.Children.Add(rowBorder);
            }

            NoDeviceStatusText.Visibility = Visibility.Collapsed;
            DeviceStatusGroupPanel.Visibility = Visibility.Visible;
            DeviceStatusGroupPanel.Children.Clear();

            int index = 0;
            foreach (var device in orderedDevices)
            {
                if (index > 0)
                {
                    DeviceStatusGroupPanel.Children.Add(new Border
                    {
                        Width = 1,
                        Height = 14,
                        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(204, 204, 204)),
                        Margin = new Thickness(12, 0, 12, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                }

                var group = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                group.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 10,
                    Height = 10,
                    Fill = GetStatusBrush(device.Status),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });

                group.Children.Add(new TextBlock
                {
                    Text = $"{device.DeviceName} ({device.MethodDisplay})",
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(102, 102, 102)),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                });

                DeviceStatusGroupPanel.Children.Add(group);
                index++;
            }
        }

        private static string NormalizeChannelLabel(string methodDisplay)
        {
            if (string.IsNullOrWhiteSpace(methodDisplay))
            {
                return "BLE";
            }

            string text = methodDisplay.Trim().ToUpperInvariant();
            if (text.Contains("UART") || text.Contains("SERIAL") || text.Contains("COM"))
            {
                return "UART";
            }

            if (text.Contains("ETH") || text.Contains("MQTT") || text.Contains("ETHERNET"))
            {
                return "ETH";
            }

            return "BLE";
        }

        private static SolidColorBrush GetStatusBrush(DeviceConnectionVisualState status)
        {
            return status switch
            {
                DeviceConnectionVisualState.Connected => new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80)),
                DeviceConnectionVisualState.Handshaking => new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 193, 7)),
                _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 67, 54)),
            };
        }

        private void InitializeDeviceEntriesFromSaved()
        {
            _connectedDevices.Clear();

            foreach (var item in _savedDevices.Values)
            {
                string channelLabel = NormalizeChannelLabel(item.Channel);
                string channelKey = BuildChannelKey(channelLabel, item.DeviceAddress);
                string displayName = string.IsNullOrWhiteSpace(item.DeviceName)
                    ? item.DeviceAddress
                    : item.DeviceName;

                _connectedDevices[channelKey] = new ConnectedDeviceEntry(
                    channelKey,
                    displayName,
                    channelLabel,
                    SessionId: null,
                    Status: DeviceConnectionVisualState.Disconnected,
                    CommunicationChannel: null);
            }
        }

        private async Task AutoConnectSavedDevicesAsync()
        {
            if (_savedDevices.Count == 0)
            {
                WriteAppLog("Auto-connect skipped: no saved devices", "AutoConnect");
                return;
            }

            WriteAppLog($"Auto-connect begin. saved={_savedDevices.Count}", "AutoConnect");

            bool hasSavedDeviceMutation = false;
            int successCount = 0;
            int failedCount = 0;

            foreach (var saved in _savedDevices.Values.ToList())
            {
                string channelLabel = NormalizeChannelLabel(saved.Channel);
                string originalChannelKey = BuildChannelKey(channelLabel, saved.DeviceAddress);
                string channelKey = originalChannelKey;
                string displayName = string.IsNullOrWhiteSpace(saved.DeviceName)
                    ? saved.DeviceAddress
                    : saved.DeviceName;

                WriteAppLog($"Auto-connect device: key={channelKey}, name={displayName}, channel={channelLabel}, addrType={saved.BleAddressType}", "AutoConnect");

                if (channelLabel == "BLE")
                {
                    var resolved = await ResolveBleReconnectCandidateAsync(saved);
                    if (resolved != null)
                    {
                        string resolvedAddress = resolved.Value.DeviceAddress.Trim();
                        string resolvedType = resolved.Value.AddressType.ToString();
                        string resolvedName = resolved.Value.DeviceName?.Trim() ?? string.Empty;

                        if (!string.Equals(saved.DeviceAddress, resolvedAddress, StringComparison.OrdinalIgnoreCase))
                        {
                            WriteAppLog($"BLE address remap: {saved.DeviceAddress} -> {resolvedAddress} ({displayName})", "AutoConnect");
                            saved.DeviceAddress = resolvedAddress;
                            hasSavedDeviceMutation = true;
                        }

                        if (!string.Equals(saved.BleAddressType, resolvedType, StringComparison.OrdinalIgnoreCase))
                        {
                            saved.BleAddressType = resolvedType;
                            hasSavedDeviceMutation = true;
                        }

                        if (!string.IsNullOrWhiteSpace(resolvedName) &&
                            !string.Equals(saved.DeviceName, resolvedName, StringComparison.OrdinalIgnoreCase))
                        {
                            saved.DeviceName = resolvedName;
                            hasSavedDeviceMutation = true;
                            displayName = resolvedName;
                        }

                        channelKey = BuildChannelKey(channelLabel, saved.DeviceAddress);
                    }
                    else
                    {
                        WriteAppLog($"BLE resolve miss, fallback to saved address: {saved.DeviceAddress} ({displayName})", "AutoConnect");
                    }
                }

                if (!string.Equals(channelKey, originalChannelKey, StringComparison.OrdinalIgnoreCase))
                {
                    _savedDevices.Remove(originalChannelKey);
                    if (_savedDevices.ContainsKey(channelKey))
                    {
                        WriteAppLog($"Auto-connect key collision after remap: {channelKey}", "AutoConnect");
                    }
                    else
                    {
                        _savedDevices[channelKey] = saved;
                    }

                    if (_connectedDevices.TryGetValue(originalChannelKey, out var oldEntry))
                    {
                        _connectedDevices.Remove(originalChannelKey);
                        _connectedDevices[channelKey] = oldEntry with
                        {
                            ChannelKey = channelKey,
                            DeviceName = displayName,
                            MethodDisplay = channelLabel,
                        };
                    }
                }

                if (string.IsNullOrWhiteSpace(saved.DeviceAddress))
                {
                    WriteAppLog($"Skip auto-connect: empty address ({displayName})", "AutoConnect");
                    continue;
                }

                if (!_connectedDevices.ContainsKey(channelKey))
                {
                    _connectedDevices[channelKey] = new ConnectedDeviceEntry(
                        channelKey,
                        displayName,
                        channelLabel,
                        SessionId: null,
                        Status: DeviceConnectionVisualState.Disconnected,
                        CommunicationChannel: null);
                }

                _connectedDevices[channelKey] = _connectedDevices[channelKey] with
                {
                    Status = DeviceConnectionVisualState.Handshaking,
                    SessionId = null,
                };
                RefreshConnectedDeviceUi();

                try
                {
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    WriteAppLog($"Connecting transport: key={channelKey}, addr={saved.DeviceAddress}, addrType={saved.BleAddressType}", "AutoConnect");
                    ICommunicationChannel channel = await ConnectBySavedChannelAsync(saved, timeoutCts.Token);

                    SessionHandshakeResult handshake;
                    try
                    {
                        handshake = await PerformSessionHelloHandshakeAsync(
                            channel,
                            endpointId: Environment.MachineName,
                            keepaliveMs: 3000,
                            cancellationToken: timeoutCts.Token);
                    }
                    catch
                    {
                        SafeDisposeChannel(channel);
                        throw;
                    }

                    if (System.Windows.Application.Current is not App app)
                    {
                        SafeDisposeChannel(channel);
                        continue;
                    }

                    bool registered = app.RegisterConnectedChannel(
                        channelKey,
                        channel,
                        handshake.SessionId,
                        handshake.KeepaliveMs);

                    if (!registered)
                    {
                        SafeDisposeChannel(channel);
                        _connectedDevices[channelKey] = _connectedDevices[channelKey] with
                        {
                            Status = DeviceConnectionVisualState.Disconnected,
                            SessionId = null,
                        };
                        WriteAppLog($"RegisterConnectedChannel rejected: key={channelKey}", "AutoConnect");
                        RefreshConnectedDeviceUi();
                        failedCount++;
                        continue;
                    }

                    if (_savedDevices.TryGetValue(channelKey, out var persisted))
                    {
                        persisted.DeviceAddress = saved.DeviceAddress;
                        persisted.BleAddressType = saved.BleAddressType;
                        persisted.Channel = channelLabel;
                    }

                    _connectedDevices[channelKey] = new ConnectedDeviceEntry(
                        channelKey,
                        displayName,
                        channelLabel,
                        handshake.SessionId,
                        DeviceConnectionVisualState.Connected,
                        channel);
                    EnsureTimeSyncScheduleForDevice(channelKey);

                    WriteAppLog($"Auto-connect success: key={channelKey}, session={handshake.SessionId}", "AutoConnect");
                    successCount++;

                    // Check device config and start notification forwarding if enabled
                    await TryAutoStartNotificationForwardingAsync(channelKey, saved);

                    RefreshConnectedDeviceUi();
                }
                catch (Exception ex)
                {
                    failedCount++;
                    WriteAppLog($"Auto-connect failed: key={channelKey}, reason={ex.Message}", "AutoConnect");
                    // 单个设备自动连接失败时继续处理其他设备。
                    if (_connectedDevices.TryGetValue(channelKey, out var existing))
                    {
                        StopTimeSyncSchedule(channelKey);
                        _connectedDevices[channelKey] = existing with
                        {
                            Status = DeviceConnectionVisualState.Disconnected,
                            SessionId = null,
                        };
                        RefreshConnectedDeviceUi();
                    }
                }
            }

            if (hasSavedDeviceMutation)
            {
                SaveSavedDevicesToConfig();
                WriteAppLog("Auto-connect updated saved records (name/address/addressType)", "AutoConnect");
            }

            WriteAppLog($"Auto-connect done. success={successCount}, failed={failedCount}", "AutoConnect");
        }

        private async Task<ICommunicationChannel> ConnectBySavedChannelAsync(PersistedDeviceItem saved, CancellationToken cancellationToken)
        {
            string channelLabel = NormalizeChannelLabel(saved.Channel);
            return channelLabel switch
            {
                "BLE" => await _bluetoothDeviceManager.ConnectBleDeviceAsync(
                    saved.DeviceAddress,
                    ParseBleAddressType(saved.BleAddressType)),
                "UART" => await ConnectUartChannelAsync(saved.DeviceAddress, cancellationToken),
                "ETH" => await ConnectEthChannelAsync(saved.DeviceAddress, cancellationToken),
                _ => throw new InvalidOperationException($"Unsupported channel: {saved.Channel}"),
            };
        }

        private async Task<(string DeviceAddress, BleAddressType AddressType, string DeviceName)?> ResolveBleReconnectCandidateAsync(PersistedDeviceItem saved)
        {
            try
            {
                var scanned = await _bluetoothDeviceManager.ScanBleDevicesAsync(TimeSpan.FromSeconds(4));
                WriteAppLog($"BLE scan for reconnect: name={saved.DeviceName}, address={saved.DeviceAddress}, found={scanned.Length}", "AutoConnect");

                string savedName = saved.DeviceName?.Trim() ?? string.Empty;
                string savedAddress = saved.DeviceAddress?.Trim() ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(savedName))
                {
                    var byName = scanned
                        .Where(d => !string.IsNullOrWhiteSpace(d.DeviceName) &&
                            string.Equals(d.DeviceName.Trim(), savedName, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(d => string.Equals(d.DeviceAddress, savedAddress, StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(d => d.SignalStrength)
                        .FirstOrDefault();

                    if (byName != null)
                    {
                        return (byName.DeviceAddress, byName.AddressType, byName.DeviceName);
                    }
                }

                if (!string.IsNullOrWhiteSpace(savedAddress))
                {
                    var byAddress = scanned.FirstOrDefault(d =>
                        string.Equals(d.DeviceAddress, savedAddress, StringComparison.OrdinalIgnoreCase));
                    if (byAddress != null)
                    {
                        return (byAddress.DeviceAddress, byAddress.AddressType, byAddress.DeviceName);
                    }
                }
            }
            catch (Exception ex)
            {
                WriteAppLog($"BLE resolve scan failed: {ex.Message}", "AutoConnect");
            }

            return null;
        }

        private static BleAddressType ParseBleAddressType(string raw)
        {
            if (Enum.TryParse(raw, true, out BleAddressType parsed))
            {
                return parsed;
            }

            return BleAddressType.Unknown;
        }

        private static async Task<ICommunicationChannel> ConnectUartChannelAsync(string portName, CancellationToken cancellationToken)
        {
            var serialChannel = new SerialDeviceDriver(portName, baudRate: 9600);
            bool connected = await serialChannel.ConnectAsync(cancellationToken);
            if (connected)
            {
                return serialChannel;
            }

            serialChannel.Dispose();
            throw new InvalidOperationException($"UART auto-connect failed: {portName}");
        }

        private static async Task<ICommunicationChannel> ConnectEthChannelAsync(string endpoint, CancellationToken cancellationToken)
        {
            ParseMqttEndpoint(endpoint, out string host, out int port);

            var mqttChannel = new MqttDeviceDriver(new MqttConfiguration
            {
                BrokerHost = host,
                BrokerPort = port,
                ClientId = $"xpanel-client-{Environment.MachineName}-{Guid.NewGuid():N}",
            });

            bool connected = await mqttChannel.ConnectAsync(cancellationToken);
            if (connected)
            {
                return mqttChannel;
            }

            mqttChannel.Dispose();
            throw new InvalidOperationException($"ETH auto-connect failed: {endpoint}");
        }

        private static void ParseMqttEndpoint(string endpoint, out string host, out int port)
        {
            host = "localhost";
            port = 1883;

            if (string.IsNullOrWhiteSpace(endpoint))
            {
                return;
            }

            string text = endpoint.Trim();
            int index = text.LastIndexOf(':');
            if (index <= 0 || index >= text.Length - 1)
            {
                host = text;
                return;
            }

            host = text[..index];
            if (!int.TryParse(text[(index + 1)..], out int parsedPort) || parsedPort <= 0 || parsedPort > 65535)
            {
                port = 1883;
                return;
            }

            port = parsedPort;
        }

        private static string BuildChannelKey(string channelLabel, string deviceAddress)
        {
            string normalizedAddress = string.IsNullOrWhiteSpace(deviceAddress)
                ? Guid.NewGuid().ToString("N")
                : deviceAddress.Trim();
            return $"{channelLabel}:{normalizedAddress}";
        }

        private static void SafeDisposeChannel(ICommunicationChannel channel)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    channel.Dispose();
                }
                catch
                {
                    // 失败路径清理异常不影响主流程。
                }
            });
        }

        private static async Task<SessionHandshakeResult> PerformSessionHelloHandshakeAsync(
            ICommunicationChannel channel,
            string endpointId,
            ushort keepaliveMs,
            CancellationToken cancellationToken)
        {
            var responseTcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var receiveBuffer = new List<byte>(256);

            void OnDataReceived(object? sender, DataReceivedEventArgs args)
            {
                if (args.Data == null || args.Data.Length == 0)
                {
                    return;
                }

                lock (receiveBuffer)
                {
                    receiveBuffer.AddRange(args.Data);
                    if (TryExtractFirstXpfFrame(receiveBuffer, out var frameBytes))
                    {
                        responseTcs.TrySetResult(frameBytes);
                    }
                }
            }

            channel.DataReceived += OnDataReceived;

            try
            {
                await channel.StartReceivingAsync(cancellationToken);

                uint msgId = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
                uint clientNonce = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
                uint tsSec = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                var helloFrame = new XpfFrame
                {
                    MessageType = XpfMessageType.Cmd,
                    Flags = 0x01,
                    QosLevel = 1,
                    Hop = 0,
                    AppId = XpfProtocolConstants.AppIdProtocolMgr,
                    OpCode = XpfProtocolConstants.OpSessionHello,
                    MsgId = msgId,
                    TimestampSec = tsSec,
                };

                helloFrame.Tlvs[XpfProtocolConstants.TlvEndpointId] = XpfCodec.EncodeUtf8(endpointId);
                helloFrame.Tlvs[XpfProtocolConstants.TlvClientNonce] = XpfCodec.EncodeUInt32(clientNonce);
                helloFrame.Tlvs[XpfProtocolConstants.TlvKeepaliveMs] = XpfCodec.EncodeUInt16(keepaliveMs);

                bool sent = await channel.SendAsync(XpfCodec.Serialize(helloFrame), cancellationToken);
                if (!sent)
                {
                    throw new InvalidOperationException("HELLO frame 发送失败");
                }

                using var reg = cancellationToken.Register(() => responseTcs.TrySetCanceled(cancellationToken));
                byte[] responseBytes = await responseTcs.Task;
                XpfFrame responseFrame = XpfCodec.Deserialize(responseBytes);

                if (responseFrame.OpCode != XpfProtocolConstants.OpSessionHello)
                {
                    throw new InvalidDataException($"收到非 HELLO 响应 op_code: 0x{responseFrame.OpCode:X4}");
                }

                if (!XpfCodec.TryReadUInt32(responseFrame.Tlvs, XpfProtocolConstants.TlvAckForMsgId, out uint ackForMsgId) || ackForMsgId != msgId)
                {
                    throw new InvalidDataException("HELLO 响应中 ack_for_msg_id 无效");
                }

                if (responseFrame.MessageType == XpfMessageType.Error)
                {
                    throw new InvalidOperationException("设备返回 ERROR 响应");
                }

                if (!XpfCodec.TryReadUInt32(responseFrame.Tlvs, XpfProtocolConstants.TlvSessionId, out uint sessionId))
                {
                    throw new InvalidDataException("HELLO 响应缺少 session_id");
                }

                ushort negotiatedKeepalive = keepaliveMs;
                if (XpfCodec.TryReadUInt16(responseFrame.Tlvs, XpfProtocolConstants.TlvKeepaliveMs, out ushort deviceKeepalive))
                {
                    negotiatedKeepalive = deviceKeepalive;
                }

                return new SessionHandshakeResult(sessionId, negotiatedKeepalive);
            }
            finally
            {
                channel.DataReceived -= OnDataReceived;
            }
        }

        private static bool TryExtractFirstXpfFrame(List<byte> buffer, out byte[] frameBytes)
        {
            frameBytes = Array.Empty<byte>();
            const int headerLength = 24;

            if (buffer.Count < headerLength)
            {
                return false;
            }

            int start = -1;
            for (int i = 0; i <= buffer.Count - 2; i++)
            {
                if (buffer[i] == 0x58 && buffer[i + 1] == 0x50)
                {
                    start = i;
                    break;
                }
            }

            if (start < 0)
            {
                buffer.Clear();
                return false;
            }

            if (start > 0)
            {
                buffer.RemoveRange(0, start);
            }

            if (buffer.Count < headerLength)
            {
                return false;
            }

            ushort bodyLen = (ushort)((buffer[20] << 8) | buffer[21]);
            int frameLen = headerLength + bodyLen;
            if (buffer.Count < frameLen)
            {
                return false;
            }

            frameBytes = buffer.Take(frameLen).ToArray();
            buffer.RemoveRange(0, frameLen);
            return true;
        }

        private bool IsTimeSyncEnabledForDevice(string channelKey)
        {
            if (!_savedDevices.TryGetValue(channelKey, out var device) || device.SyncConfig == null)
            {
                return false;
            }

            return device.SyncConfig.Any(item =>
                string.Equals(item.Category, "Time", StringComparison.OrdinalIgnoreCase) && item.IsEnabled);
        }

        private bool IsDeviceConnectedForTimeSync(string channelKey)
        {
            return _connectedDevices.TryGetValue(channelKey, out var device)
                && device.Status == DeviceConnectionVisualState.Connected
                && device.SessionId.HasValue;
        }

        private void EnsureTimeSyncScheduleForDevice(string channelKey)
        {
            if (string.IsNullOrWhiteSpace(channelKey))
            {
                return;
            }

            if (!IsTimeSyncEnabledForDevice(channelKey) || !IsDeviceConnectedForTimeSync(channelKey))
            {
                StopTimeSyncSchedule(channelKey);
                return;
            }

            StartTimeSyncSchedule(channelKey);
        }

        private void StartTimeSyncSchedule(string channelKey)
        {
            CancellationTokenSource loopCts;

            lock (_timeSyncLock)
            {
                if (_timeSyncLoops.ContainsKey(channelKey))
                {
                    return;
                }

                loopCts = new CancellationTokenSource();
                _timeSyncLoops[channelKey] = loopCts;
            }

            _ = RunTimeSyncLoopAsync(channelKey, loopCts);
        }

        private void StopTimeSyncSchedule(string channelKey)
        {
            CancellationTokenSource? loopCts = null;

            lock (_timeSyncLock)
            {
                if (_timeSyncLoops.TryGetValue(channelKey, out var existing))
                {
                    loopCts = existing;
                    _timeSyncLoops.Remove(channelKey);
                }
            }

            if (loopCts == null)
            {
                return;
            }

            try
            {
                loopCts.Cancel();
            }
            catch
            {
                // 取消失败不阻断流程。
            }
        }

        private void StopAllTimeSyncSchedules()
        {
            List<string> keys;
            lock (_timeSyncLock)
            {
                keys = _timeSyncLoops.Keys.ToList();
            }

            foreach (var key in keys)
            {
                StopTimeSyncSchedule(key);
            }
        }

        private async Task RunTimeSyncLoopAsync(string channelKey, CancellationTokenSource loopCts)
        {
            try
            {
                while (!loopCts.Token.IsCancellationRequested)
                {
                    if (!IsTimeSyncEnabledForDevice(channelKey) || !IsDeviceConnectedForTimeSync(channelKey))
                    {
                        break;
                    }

                    bool sent = await SendTimeSyncNowAsync(channelKey, loopCts.Token);
                    if (!sent)
                    {
                        System.Diagnostics.Debug.WriteLine($"Time Sync send failed for {channelKey}");
                    }

                    await Task.Delay(TimeSyncInterval, loopCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // 正常停止。
            }
            finally
            {
                lock (_timeSyncLock)
                {
                    if (_timeSyncLoops.TryGetValue(channelKey, out var existing) && ReferenceEquals(existing, loopCts))
                    {
                        _timeSyncLoops.Remove(channelKey);
                    }
                }

                loopCts.Dispose();
            }
        }

        private static async Task<bool> SendTimeSyncNowAsync(string channelKey, CancellationToken cancellationToken)
        {
            if (System.Windows.Application.Current is not App app)
            {
                return false;
            }

            return await app.SendTimeSyncAsync(channelKey, cancellationToken: cancellationToken);
        }

        private void LoadSavedDevicesIntoCache()
        {
            try
            {
                if (!File.Exists(DeviceConfigPath))
                {
                    WriteAppLog($"Saved device config not found: {DeviceConfigPath}", "AutoConnect");
                    return;
                }

                string json = File.ReadAllText(DeviceConfigPath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }

                var config = JsonSerializer.Deserialize<PersistedDeviceConfig>(json);
                if (config?.Devices == null)
                {
                    return;
                }

                _savedDevices.Clear();
                foreach (var device in config.Devices)
                {
                    if (string.IsNullOrWhiteSpace(device.DeviceAddress))
                    {
                        continue;
                    }

                    string channel = NormalizeChannelLabel(device.Channel);
                    string key = BuildChannelKey(channel, device.DeviceAddress);
                    _savedDevices[key] = new PersistedDeviceItem
                    {
                        DeviceName = device.DeviceName,
                        DeviceAddress = device.DeviceAddress,
                        Channel = channel,
                        BleAddressType = string.IsNullOrWhiteSpace(device.BleAddressType)
                            ? BleAddressType.Unknown.ToString()
                            : device.BleAddressType,
                        SyncConfig = device.SyncConfig ?? new List<SyncItem>(),
                    };
                }

                WriteAppLog($"Loaded saved devices: {_savedDevices.Count}", "AutoConnect");
            }
            catch (Exception ex)
            {
                _savedDevices.Clear();
                WriteAppLog($"Load saved devices failed: {ex.Message}", "AutoConnect");
            }
        }

        private void SaveSavedDevicesToConfig()
        {
            try
            {
                string? dir = Path.GetDirectoryName(DeviceConfigPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var config = new PersistedDeviceConfig
                {
                    Devices = _savedDevices.Values
                        .Select(d => new PersistedDeviceItem
                        {
                            DeviceName = d.DeviceName,
                            DeviceAddress = d.DeviceAddress,
                            Channel = NormalizeChannelLabel(d.Channel),
                            BleAddressType = string.IsNullOrWhiteSpace(d.BleAddressType)
                                ? BleAddressType.Unknown.ToString()
                                : d.BleAddressType,
                            SyncConfig = d.SyncConfig,
                        })
                        .Where(d => !string.IsNullOrWhiteSpace(d.DeviceAddress))
                        .OrderBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                };

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                };
                File.WriteAllText(DeviceConfigPath, JsonSerializer.Serialize(config, options));
            }
            catch
            {
                // 配置落盘失败时不阻断主流程。
            }
        }

        private sealed record ConnectedDeviceEntry(
            string ChannelKey,
            string DeviceName,
            string MethodDisplay,
            uint? SessionId,
            DeviceConnectionVisualState Status,
            ICommunicationChannel CommunicationChannel = null);

        private enum DeviceConnectionVisualState
        {
            Disconnected = 0,
            Handshaking = 1,
            Connected = 2,
        }

        private sealed class PersistedDeviceConfig
        {
            public List<PersistedDeviceItem> Devices { get; set; } = new();
        }

        private sealed class PersistedDeviceItem
        {
            public string DeviceName { get; set; } = string.Empty;
            public string DeviceAddress { get; set; } = string.Empty;
            public string Channel { get; set; } = "BLE";
            public string BleAddressType { get; set; } = "Unknown";
            public List<SyncItem> SyncConfig { get; set; } = new();
        }

        private sealed record SessionHandshakeResult(uint SessionId, ushort KeepaliveMs);
    }
}
