using System;
using System.Windows;
using System.IO;
using System.Drawing;
using System.Windows.Controls;
using System.Windows.Media;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinForms = System.Windows.Forms;
using XPanel.Communication.Bluetooth;
using XPanel.Communication.MQTT;
using XPanel.Communication.Serial;
using XPanel.Core.Communication;
using XPanel.Core.Protocol;

namespace XPanel.Application
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        private static readonly string DeviceConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XPanelClient",
            "devices.json");

        private WinForms.NotifyIcon _trayIcon;
        private bool _isShuttingDown = false;
        private readonly BluetoothDeviceManager _bluetoothDeviceManager = new();
        private readonly Dictionary<string, ConnectedDeviceEntry> _connectedDevices = new();
        private readonly Dictionary<string, PersistedDeviceItem> _savedDevices = new(StringComparer.OrdinalIgnoreCase);

        public MainWindow()
        {
            InitializeComponent();
            InitializeTrayIcon();
            // 启动时隐藏窗口到系统托盘
            this.Visibility = Visibility.Hidden;
            this.WindowState = WindowState.Minimized;
            
            // 绑定标签页切换事件
            LeftTabControl.SelectionChanged += (s, e) => UpdateTabContent();
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadSavedDevicesIntoCache();
            await AutoConnectSavedDevicesAsync();
            RefreshConnectedDeviceUi();
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

        private void ExitApplication()
        {
            _isShuttingDown = true;
            _trayIcon?.Dispose();
            System.Windows.Application.Current.Shutdown();
        }

        private void UpdateTabContent()
        {
            if (LeftTabControl.SelectedIndex >= 0)
            {
                // 更新标签页头部文字
                string[] tabHeaders = { "Device", "Device Control", "System Settings" };
                TabHeader.Text = tabHeaders[LeftTabControl.SelectedIndex];

                // 显示/隐藏对应的内容面板
                DeviceInfoPanel.Visibility = LeftTabControl.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
                ControlPanel.Visibility = LeftTabControl.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
                SettingsPanel.Visibility = LeftTabControl.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
                AddDeviceBottomButton.Visibility = LeftTabControl.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
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
            _trayIcon?.Dispose();
            _bluetoothDeviceManager.Dispose();
            base.OnClosed(e);
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
                    addDeviceWindow.ConnectedSessionId);

                _savedDevices[channelKey] = new PersistedDeviceItem
                {
                    DeviceName = addDeviceWindow.ConnectedDeviceName,
                    DeviceAddress = address,
                    Channel = normalizedChannel,
                };

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

            bool removedFromChannel = false;
            if (System.Windows.Application.Current is App app)
            {
                if (!_connectedDevices.TryGetValue(channelKey, out var targetDevice))
                {
                    removeButton.IsEnabled = true;
                    return;
                }

                removedFromChannel = await app.DisconnectAndRemoveChannelAsync(channelKey, targetDevice.SessionId);
            }

            if (!removedFromChannel)
            {
                removeButton.IsEnabled = true;
                System.Windows.MessageBox.Show("Failed to disconnect device.", "Remove Device", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _connectedDevices.Remove(channelKey);
            _savedDevices.Remove(channelKey);
            RefreshConnectedDeviceUi();
            SaveSavedDevicesToConfig();
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

            foreach (var device in _connectedDevices.Values)
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

                var deviceText = new TextBlock
                {
                    Text = device.DeviceName,
                    FontSize = 20,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 51, 51)),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 10, 0),
                };
                Grid.SetColumn(deviceText, 0);

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

                rowGrid.Children.Add(deviceText);
                rowGrid.Children.Add(removeButton);
                rowBorder.Child = rowGrid;
                ConnectedDevicesPanel.Children.Add(rowBorder);
            }

            NoDeviceStatusText.Visibility = Visibility.Collapsed;
            DeviceStatusGroupPanel.Visibility = Visibility.Visible;
            DeviceStatusGroupPanel.Children.Clear();

            int index = 0;
            foreach (var device in _connectedDevices.Values)
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
                    Fill = device.IsConnected
                        ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80))
                        : new SolidColorBrush(System.Windows.Media.Color.FromRgb(244, 67, 54)),
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

        private async Task AutoConnectSavedDevicesAsync()
        {
            if (_savedDevices.Count == 0)
            {
                return;
            }

            foreach (var saved in _savedDevices.Values)
            {
                string channelLabel = NormalizeChannelLabel(saved.Channel);
                string channelKey = BuildChannelKey(channelLabel, saved.DeviceAddress);
                if (string.IsNullOrWhiteSpace(saved.DeviceAddress) || _connectedDevices.ContainsKey(channelKey))
                {
                    continue;
                }

                try
                {
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    ICommunicationChannel channel = await ConnectBySavedChannelAsync(saved, timeoutCts.Token);

                    SessionHandshakeResult handshake;
                    try
                    {
                        handshake = await PerformSessionHelloHandshakeAsync(
                            channel,
                            endpointId: Environment.MachineName,
                            keepaliveMs: 25000,
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
                        continue;
                    }

                    string displayName = string.IsNullOrWhiteSpace(saved.DeviceName)
                        ? saved.DeviceAddress
                        : saved.DeviceName;

                    _connectedDevices[channelKey] = new ConnectedDeviceEntry(
                        channelKey,
                        displayName,
                        channelLabel,
                        handshake.SessionId,
                        IsConnected: true);
                }
                catch
                {
                    // 单个设备自动连接失败时继续处理其他设备。
                }
            }
        }

        private async Task<ICommunicationChannel> ConnectBySavedChannelAsync(PersistedDeviceItem saved, CancellationToken cancellationToken)
        {
            string channelLabel = NormalizeChannelLabel(saved.Channel);
            return channelLabel switch
            {
                "BLE" => await _bluetoothDeviceManager.ConnectBleDeviceAsync(
                    saved.DeviceAddress,
                    BleAddressType.Unknown),
                "UART" => await ConnectUartChannelAsync(saved.DeviceAddress, cancellationToken),
                "ETH" => await ConnectEthChannelAsync(saved.DeviceAddress, cancellationToken),
                _ => throw new InvalidOperationException($"Unsupported channel: {saved.Channel}"),
            };
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

        private void LoadSavedDevicesIntoCache()
        {
            try
            {
                if (!File.Exists(DeviceConfigPath))
                {
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
                    };
                }
            }
            catch
            {
                _savedDevices.Clear();
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

        private sealed record ConnectedDeviceEntry(string ChannelKey, string DeviceName, string MethodDisplay, uint SessionId, bool IsConnected = true);

        private sealed class PersistedDeviceConfig
        {
            public List<PersistedDeviceItem> Devices { get; set; } = new();
        }

        private sealed class PersistedDeviceItem
        {
            public string DeviceName { get; set; } = string.Empty;
            public string DeviceAddress { get; set; } = string.Empty;
            public string Channel { get; set; } = "BLE";
        }

        private sealed record SessionHandshakeResult(uint SessionId, ushort KeepaliveMs);
    }
}
