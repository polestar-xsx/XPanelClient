using System;
using System.Windows;
using System.IO;
using System.Drawing;
using System.Windows.Controls;
using System.Windows.Media;
using System.Collections.Generic;
using System.Linq;
using WinForms = System.Windows.Forms;

namespace XPanel.Application
{
    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        private WinForms.NotifyIcon _trayIcon;
        private bool _isShuttingDown = false;
        private readonly Dictionary<string, ConnectedDeviceEntry> _connectedDevices = new();

        public MainWindow()
        {
            InitializeComponent();
            InitializeTrayIcon();
            // 启动时隐藏窗口到系统托盘
            this.Visibility = Visibility.Hidden;
            this.WindowState = WindowState.Minimized;
            
            // 绑定标签页切换事件
            LeftTabControl.SelectionChanged += (s, e) => UpdateTabContent();
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
                string channelKey = $"BLE:{address}";

                _connectedDevices[channelKey] = new ConnectedDeviceEntry(
                    channelKey,
                    addDeviceWindow.ConnectedDeviceName,
                    methodDisplay,
                    addDeviceWindow.ConnectedSessionId);

                RefreshConnectedDeviceUi();
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
            RefreshConnectedDeviceUi();
        }

        private void RefreshConnectedDeviceUi()
        {
            ConnectedDevicesPanel.Children.Clear();

            if (_connectedDevices.Count == 0)
            {
                NoConnectedDevicesText.Visibility = Visibility.Visible;
                ConnectedDevicesPanel.Visibility = Visibility.Collapsed;

                ConnectionStatusDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 152, 0));
                ConnectionStateText.Text = "Disconnected";
                ConnectionDeviceText.Text = "No connection";
                ConnectionMethodText.Text = "Waiting";
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

            var latest = _connectedDevices.Values.Last();

            ConnectionStatusDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80));
            ConnectionStateText.Text = "Connected";
            ConnectionDeviceText.Text = latest.DeviceName;
            ConnectionMethodText.Text = latest.MethodDisplay;
        }

        private sealed record ConnectedDeviceEntry(string ChannelKey, string DeviceName, string MethodDisplay, uint SessionId);
    }
}
