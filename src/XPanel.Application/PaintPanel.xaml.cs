using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace XPanel.Application
{
    public partial class PaintPanel : UserControl
    {
        private const int DefaultCanvasSize = 32;
        private const double PreviewSize = 320;
        private const string PaintHint = "Click or drag on the matrix to paint";
        private const int MaxDiffFillRuns = 48;

        private static readonly string[] BasicColors =
        {
            "#000000", "#7F7F7F", "#880015", "#ED1C24", "#FF7F27", "#FFF200", "#22B14C", "#00A2E8",
            "#3F48CC", "#A349A4", "#FFFFFF", "#C3C3C3", "#B97A57", "#FFAEC9", "#FFC90E", "#EFE4B0",
            "#B5E61D", "#99D9EA", "#7092BE", "#C8BFE7",
        };

        private static readonly Brush IdleBrush = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
        private static readonly Brush BusyBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00));
        private static readonly Brush ActiveBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        private static readonly Brush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));

        private int _canvasWidth = DefaultCanvasSize;
        private int _canvasHeight = DefaultCanvasSize;
        private int _cellSize;
        private byte[] _canvas = new byte[DefaultCanvasSize * DefaultCanvasSize * 3];
        private readonly Stack<byte[]> _undoStack = new();
        private readonly Stack<byte[]> _redoStack = new();
        private Color _selectedColor = Colors.White;
        private Border? _selectedSwatch;
        private int _brushSize = 1;
        private bool _eyedropperMode;

        // 当前笔画：本地立即绘制，点按 25ms 合并后作为 paint.stroke 发送。
        private readonly DispatcherTimer _strokeFlushTimer = new() { Interval = TimeSpan.FromMilliseconds(25) };
        private readonly List<(byte X, byte Y)> _pendingPoints = new();
        private bool _isPainting;
        private bool _strokeStartPending;
        private (int X, int Y) _lastPoint;
        private Color _strokeColor;
        private byte _strokeShape;
        private byte _strokeSize;

        private PaintProtocolSession? _session;
        private bool _sessionStarting;

        public PaintPanel()
        {
            InitializeComponent();
            _strokeFlushTimer.Tick += (_, _) => FlushStroke(end: false);
            PreviewArea.LostMouseCapture += (_, _) => FinishStroke();
            BuildPalette();
            ApplyCanvasLayout();
            RenderFrame();
            UpdateSessionUi();
        }

        // 由主窗口提供当前选中且已握手的设备；无可用设备时返回 null。
        internal Func<PaintConnection?>? ConnectionProvider { get; set; }

        // 会话失效（断开/重新握手/设备移除）时设备已自行退出 Paint，只需本地停止。
        internal void InvalidateIfStale(Func<string, uint?> currentSessionId)
        {
            if (_session != null && currentSessionId(_session.ChannelKey) != _session.SessionId)
            {
                DetachSession();
                SetStatus("Device disconnected · press Start to mirror again", ErrorBrush);
            }
        }

        internal void OnSelectedDeviceChanged(string channelKey)
        {
            if (_session != null && !string.Equals(_session.ChannelKey, channelKey, StringComparison.OrdinalIgnoreCase))
            {
                _ = StopSessionAsync();
            }
        }

        // 先断开会话再清本地画布，确保清空不会同步到设备。
        internal void OnTabLeft()
        {
            FinishStroke();
            _ = StopSessionAsync();
            Array.Clear(_canvas);
            _undoStack.Clear();
            _redoStack.Clear();
            UpdateUndoRedoState();
            RenderFrame();
        }

        private void ApplyCanvasLayout()
        {
            _cellSize = Math.Max(1, (int)(PreviewSize / Math.Max(_canvasWidth, _canvasHeight)));
            PreviewMatrix.LedCellSize = _cellSize;
            PreviewMatrix.LedDiameter = Math.Max(1, _cellSize * 6 / 10);
        }

        private void ResizeCanvas(int width, int height)
        {
            _canvasWidth = width;
            _canvasHeight = height;
            _canvas = new byte[width * height * 3];
            _undoStack.Clear();
            _redoStack.Clear();
            UpdateUndoRedoState();
            ApplyCanvasLayout();
            RenderFrame();
        }

        private void RenderFrame()
        {
            int pixelCount = _canvasWidth * _canvasHeight;
            var bgra = new byte[pixelCount * 4];
            for (int index = 0; index < pixelCount; index++)
            {
                bgra[index * 4] = _canvas[index * 3 + 2];
                bgra[index * 4 + 1] = _canvas[index * 3 + 1];
                bgra[index * 4 + 2] = _canvas[index * 3];
                bgra[index * 4 + 3] = 255;
            }

            var bitmap = new WriteableBitmap(_canvasWidth, _canvasHeight, 96, 96, PixelFormats.Bgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, _canvasWidth, _canvasHeight), bgra, _canvasWidth * 4, 0);
            bitmap.Freeze();
            PreviewMatrix.SourceBitmap = bitmap;
        }

        private void PreviewArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!TryGetCanvasPoint(e, out int x, out int y))
            {
                return;
            }

            if (_eyedropperMode)
            {
                PickColorAt(x, y);
                return;
            }

            PushUndoSnapshot();
            _strokeColor = _selectedColor;
            _strokeShape = SquareBrushRadio.IsChecked == true ? PaintProtocolSession.BrushSquare : PaintProtocolSession.BrushRound;
            _strokeSize = (byte)_brushSize;
            _isPainting = true;
            _strokeStartPending = true;
            _lastPoint = (x, y);
            _pendingPoints.Clear();
            _pendingPoints.Add(((byte)x, (byte)y));

            Stamp(x, y);
            RenderFrame();
            _strokeFlushTimer.Start();
            PreviewArea.CaptureMouse();
        }

        private void PreviewArea_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isPainting || !TryGetCanvasPoint(e, out int x, out int y) || (x, y) == _lastPoint)
            {
                return;
            }

            DrawSegment(_lastPoint.X, _lastPoint.Y, x, y);
            _lastPoint = (x, y);
            _pendingPoints.Add(((byte)x, (byte)y));
            if (_pendingPoints.Count >= PaintProtocolSession.MaxStrokePoints)
            {
                FlushStroke(end: false);
            }

            RenderFrame();
        }

        private void PreviewArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            FinishStroke();
        }

        private void FinishStroke()
        {
            if (!_isPainting)
            {
                return;
            }

            _isPainting = false;
            _strokeFlushTimer.Stop();
            FlushStroke(end: true);
            if (PreviewArea.IsMouseCaptured)
            {
                PreviewArea.ReleaseMouseCapture();
            }
        }

        private void FlushStroke(bool end)
        {
            if (_session == null || (_pendingPoints.Count == 0 && !end))
            {
                _pendingPoints.Clear();
                return;
            }

            // 结束包必须至少带一个点；重复末点只会在同一位置再盖一次笔刷。
            if (_pendingPoints.Count == 0)
            {
                _pendingPoints.Add(((byte)_lastPoint.X, (byte)_lastPoint.Y));
            }

            byte flags = (byte)((_strokeStartPending ? PaintProtocolSession.StrokeStart : 0) |
                                (end ? PaintProtocolSession.StrokeEnd : 0));
            _session.QueueStroke(_strokeColor, _strokeShape, _strokeSize, flags, _pendingPoints.ToArray());
            _strokeStartPending = false;
            _pendingPoints.Clear();
        }

        private bool TryGetCanvasPoint(MouseEventArgs e, out int x, out int y)
        {
            Point position = e.GetPosition(PreviewMatrix);
            x = (int)Math.Floor(position.X / _cellSize);
            y = (int)Math.Floor(position.Y / _cellSize);
            return x >= 0 && y >= 0 && x < _canvasWidth && y < _canvasHeight;
        }

        // 协议 16.8 笔刷光栅化，必须与设备端逐像素一致。
        private void Stamp(int cx, int cy)
        {
            int d = _strokeSize;
            int x0 = cx - (d - 1) / 2;
            int y0 = cy - (d - 1) / 2;
            for (int py = y0; py < y0 + d; py++)
            {
                for (int px = x0; px < x0 + d; px++)
                {
                    if (_strokeShape == PaintProtocolSession.BrushRound)
                    {
                        int u = 2 * (px - x0) - (d - 1);
                        int v = 2 * (py - y0) - (d - 1);
                        if (u * u + v * v > (d - 1) * (d - 1) + 1)
                        {
                            continue;
                        }
                    }

                    if (px >= 0 && py >= 0 && px < _canvasWidth && py < _canvasHeight)
                    {
                        int offset = (py * _canvasWidth + px) * 3;
                        _canvas[offset] = _strokeColor.R;
                        _canvas[offset + 1] = _strokeColor.G;
                        _canvas[offset + 2] = _strokeColor.B;
                    }
                }
            }
        }

        private void DrawSegment(int ax, int ay, int bx, int by)
        {
            int x = ax;
            int y = ay;
            int dx = Math.Abs(bx - ax);
            int sx = ax < bx ? 1 : -1;
            int dy = -Math.Abs(by - ay);
            int sy = ay < by ? 1 : -1;
            int err = dx + dy;
            while (true)
            {
                Stamp(x, y);
                if (x == bx && y == by)
                {
                    break;
                }

                int e2 = 2 * err;
                if (e2 >= dy)
                {
                    err += dy;
                    x += sx;
                }

                if (e2 <= dx)
                {
                    err += dx;
                    y += sy;
                }
            }
        }

        private void PushUndoSnapshot()
        {
            _undoStack.Push((byte[])_canvas.Clone());
            _redoStack.Clear();
            UpdateUndoRedoState();
        }

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            if (_session != null)
            {
                await StopSessionAsync();
                return;
            }

            PaintConnection? connection = ConnectionProvider?.Invoke();
            if (connection == null)
            {
                MessageBox.Show(Window.GetWindow(this), "请先在左侧选择一个已连接的设备。", "Paint",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _sessionStarting = true;
            UpdateSessionUi();
            SetStatus($"Connecting to {connection.DeviceName}...", BusyBrush);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                PaintProtocolSession session = await PaintProtocolSession.StartAsync(connection, cts.Token);
                session.SyncReceived += Session_SyncReceived;
                session.Ended += Session_Ended;
                _session = session;

                // 设备画布尺寸以 paint.begin 响应为准；尺寸一致时保留本地内容并用关键帧同步到设备。
                if (session.Width != _canvasWidth || session.Height != _canvasHeight)
                {
                    ResizeCanvas(session.Width, session.Height);
                }
                else if (_canvas.Any(value => value != 0))
                {
                    session.QueueKeyframe(_canvas);
                }

                SetStatus($"Mirroring on {connection.DeviceName} ({session.Width}x{session.Height})", ActiveBrush);
            }
            catch (Exception ex)
            {
                MainWindow.WriteAppLog($"paint start failed: {ex.Message}", "Paint");
                SetStatus($"Start failed: {ex.Message}", ErrorBrush);
            }
            finally
            {
                _sessionStarting = false;
                UpdateSessionUi();
            }
        }

        private async Task StopSessionAsync()
        {
            PaintProtocolSession? session = _session;
            if (session == null)
            {
                return;
            }

            FinishStroke();
            // 只解绑不释放，EndAsync 还需要用该会话发送 paint.end，完成后由其自行释放。
            DetachSession(dispose: false);
            SetStatus("Stopping...", BusyBrush);
            await session.EndAsync();
            if (_session == null)
            {
                SetStatus("Not synchronized · press Start to mirror on the device", IdleBrush);
            }
        }

        private void DetachSession(bool dispose = true)
        {
            if (_session == null)
            {
                return;
            }

            _session.SyncReceived -= Session_SyncReceived;
            _session.Ended -= Session_Ended;
            if (dispose)
            {
                _session.Dispose();
            }

            _session = null;
            UpdateSessionUi();
        }

        private void Session_Ended(object? sender, string reason)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(sender, _session))
                {
                    DetachSession();
                    SetStatus(reason, ErrorBrush);
                }
            });
        }

        // 协议 16.11.3 控制端同步处理规则。
        private void Session_SyncReceived(object? sender, PaintSyncInfo info)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(sender, _session) || _session == null)
                {
                    return;
                }

                if (info.OutOfSync)
                {
                    if (!_session.KeyframePending)
                    {
                        _session.QueueKeyframe(_canvas);
                    }

                    return;
                }

                // 仍有操作在途时本地画布领先于设备，CRC 比较无意义。
                if (info.FrameTransferActive || _isPainting || _session.HasPendingOperations ||
                    info.Seq != _session.LastSentSeq)
                {
                    return;
                }

                if (info.CanvasCrc32 != MainWindow.ComputeCrc32(_canvas))
                {
                    MainWindow.WriteAppLog($"paint CRC mismatch at seq={info.Seq}; resending keyframe", "Paint");
                    _session.QueueKeyframe(_canvas);
                }
            });
        }

        private void UpdateSessionUi()
        {
            bool active = _session != null;
            StartButton.IsEnabled = !_sessionStarting;
            StartButton.Content = active ? "Stop" : "Start";
            StartButton.Background = active ? ErrorBrush : ActiveBrush;
        }

        private void SetStatus(string text, Brush brush)
        {
            StatusText.Text = text;
            StatusDot.Fill = brush;
        }

        private void LoadPicture_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Load Picture",
                Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
                CheckFileExists = true,
            };

            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            {
                return;
            }

            byte[] pixels;
            try
            {
                pixels = LoadPictureAsCanvas(dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), $"Failed to load image: {ex.Message}", "Paint",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            PushUndoSnapshot();
            _canvas = pixels;
            RenderFrame();
            _session?.QueueKeyframe(_canvas);
        }

        // 按比例缩放并居中到黑底画布，缩小时取区域平均；透明像素叠加到黑色上。
        private byte[] LoadPictureAsCanvas(string filePath)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(filePath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            var source = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            int sourceWidth = source.PixelWidth;
            int sourceHeight = source.PixelHeight;
            var sourcePixels = new byte[sourceWidth * sourceHeight * 4];
            source.CopyPixels(sourcePixels, sourceWidth * 4, 0);

            double scale = Math.Min((double)_canvasWidth / sourceWidth, (double)_canvasHeight / sourceHeight);
            int drawWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
            int drawHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));
            int offsetX = (_canvasWidth - drawWidth) / 2;
            int offsetY = (_canvasHeight - drawHeight) / 2;

            var canvas = new byte[_canvasWidth * _canvasHeight * 3];
            for (int ty = 0; ty < drawHeight; ty++)
            {
                int sy0 = ty * sourceHeight / drawHeight;
                int sy1 = Math.Max(sy0 + 1, (ty + 1) * sourceHeight / drawHeight);
                for (int tx = 0; tx < drawWidth; tx++)
                {
                    int sx0 = tx * sourceWidth / drawWidth;
                    int sx1 = Math.Max(sx0 + 1, (tx + 1) * sourceWidth / drawWidth);
                    long sumR = 0, sumG = 0, sumB = 0;
                    int count = 0;
                    for (int sy = sy0; sy < sy1; sy++)
                    {
                        for (int sx = sx0; sx < sx1; sx++)
                        {
                            int o = (sy * sourceWidth + sx) * 4;
                            int alpha = sourcePixels[o + 3];
                            sumB += sourcePixels[o] * alpha / 255;
                            sumG += sourcePixels[o + 1] * alpha / 255;
                            sumR += sourcePixels[o + 2] * alpha / 255;
                            count++;
                        }
                    }

                    int target = ((ty + offsetY) * _canvasWidth + tx + offsetX) * 3;
                    canvas[target] = (byte)(sumR / count);
                    canvas[target + 1] = (byte)(sumG / count);
                    canvas[target + 2] = (byte)(sumB / count);
                }
            }

            return canvas;
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            FinishStroke();
            PushUndoSnapshot();
            Array.Clear(_canvas);
            RenderFrame();
            _session?.QueueFill(Colors.Black);
        }

        private void SavePicture_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save Picture",
                Filter = "PNG Image|*.png",
                FileName = $"Paint_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                AddExtension = true,
            };

            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            {
                return;
            }

            try
            {
                var bitmap = BitmapSource.Create(_canvasWidth, _canvasHeight, 96, 96, PixelFormats.Rgb24, null, _canvas, _canvasWidth * 3);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write);
                encoder.Save(stream);
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), $"Failed to save image: {ex.Message}", "Paint",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void PickColorAt(int x, int y)
        {
            int offset = (y * _canvasWidth + x) * 3;
            Color color = Color.FromRgb(_canvas[offset], _canvas[offset + 1], _canvas[offset + 2]);
            Border swatch = CreateCustomSwatch(color);
            if (CustomColorPanel.Children.Count >= 16)
            {
                CustomColorPanel.Children.RemoveAt(0);
            }
            CustomColorPanel.Children.Add(swatch);
            SelectSwatch(swatch, color);
            _eyedropperMode = false;
            EyedropperButton.Background = Brushes.Transparent;
            HintText.Text = PaintHint;
        }

        private void Eyedropper_Click(object sender, MouseButtonEventArgs e)
        {
            _eyedropperMode = !_eyedropperMode;
            EyedropperButton.Background = _eyedropperMode
                ? new SolidColorBrush(Color.FromRgb(0xBB, 0xDE, 0xFB))
                : Brushes.Transparent;
            HintText.Text = _eyedropperMode
                ? "Eyedropper: click a pixel to pick its color"
                : PaintHint;
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (_undoStack.Count == 0)
            {
                return;
            }

            _redoStack.Push((byte[])_canvas.Clone());
            byte[] before = _canvas;
            _canvas = _undoStack.Pop();
            UpdateUndoRedoState();
            RenderFrame();
            SendCanvasDiff(before, _canvas);
        }

        private void Redo_Click(object sender, RoutedEventArgs e)
        {
            if (_redoStack.Count == 0)
            {
                return;
            }

            _undoStack.Push((byte[])_canvas.Clone());
            byte[] before = _canvas;
            _canvas = _redoStack.Pop();
            UpdateUndoRedoState();
            RenderFrame();
            SendCanvasDiff(before, _canvas);
        }

        // 小改动用无需应答的 paint.fill 行段下发（与画笔同等延迟），大改动改发变化区域的关键帧。
        private void SendCanvasDiff(byte[] before, byte[] after)
        {
            if (_session == null)
            {
                return;
            }

            var runs = new List<(int X, int Y, int W, Color Color)>();
            int minX = _canvasWidth, minY = _canvasHeight, maxX = -1, maxY = -1;
            for (int y = 0; y < _canvasHeight; y++)
            {
                int x = 0;
                while (x < _canvasWidth)
                {
                    int offset = (y * _canvasWidth + x) * 3;
                    if (before[offset] == after[offset] && before[offset + 1] == after[offset + 1] && before[offset + 2] == after[offset + 2])
                    {
                        x++;
                        continue;
                    }

                    Color color = Color.FromRgb(after[offset], after[offset + 1], after[offset + 2]);
                    int start = x;
                    do
                    {
                        x++;
                        offset += 3;
                    }
                    while (x < _canvasWidth && after[offset] == color.R && after[offset + 1] == color.G && after[offset + 2] == color.B);

                    runs.Add((start, y, x - start, color));
                    minX = Math.Min(minX, start);
                    maxX = Math.Max(maxX, x - 1);
                    minY = Math.Min(minY, y);
                    maxY = y;
                }
            }

            if (runs.Count == 0)
            {
                return;
            }

            if (runs.Count <= MaxDiffFillRuns)
            {
                foreach (var run in runs)
                {
                    _session.QueueFill(run.Color, ((byte)run.X, (byte)run.Y, (byte)run.W, 1));
                }

                return;
            }

            _session.QueueKeyframe(after, (minX, minY, maxX - minX + 1, maxY - minY + 1));
        }

        private void UpdateUndoRedoState()
        {
            UndoButton.IsEnabled = _undoStack.Count > 0;
            RedoButton.IsEnabled = _redoStack.Count > 0;
        }

        private void BrushSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _brushSize = Math.Max(1, (int)Math.Round(e.NewValue));
            if (BrushSizeText != null)
            {
                BrushSizeText.Text = _brushSize.ToString();
            }
        }

        private void BuildPalette()
        {
            foreach (string hex in BasicColors)
            {
                Color color = (Color)ColorConverter.ConvertFromString(hex);
                Border swatch = CreateSwatch(color);
                BasicColorPanel.Children.Add(swatch);
                if (color == Colors.White)
                {
                    SelectSwatch(swatch, color);
                }
            }

            for (int index = 0; index < 16; index++)
            {
                CustomColorPanel.Children.Add(CreateCustomSwatch(null));
            }
        }

        private Border CreateSwatch(Color color)
        {
            var swatch = CreateSwatchBase(new SolidColorBrush(color), color);
            swatch.MouseLeftButtonDown += (_, _) => SelectSwatch(swatch, color);
            return swatch;
        }

        private Border CreateCustomSwatch(Color? color)
        {
            var swatch = CreateSwatchBase(new SolidColorBrush(color ?? Colors.White), color);
            swatch.MouseLeftButtonDown += CustomSwatch_Click;
            return swatch;
        }

        private static Border CreateSwatchBase(Brush background, object? tag)
        {
            return new Border
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(1),
                Background = background,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Tag = tag,
            };
        }

        private void CustomSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border swatch)
            {
                return;
            }

            if (swatch.Tag is Color existing && e.ClickCount == 1)
            {
                SelectSwatch(swatch, existing);
                return;
            }

            using var dialog = new WinForms.ColorDialog { FullOpen = true };
            if (swatch.Tag is Color current)
            {
                dialog.Color = System.Drawing.Color.FromArgb(current.A, current.R, current.G, current.B);
            }

            if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            {
                Color color = Color.FromArgb(dialog.Color.A, dialog.Color.R, dialog.Color.G, dialog.Color.B);
                swatch.Background = new SolidColorBrush(color);
                swatch.Tag = color;
                SelectSwatch(swatch, color);
            }
        }

        private void SelectSwatch(Border swatch, Color color)
        {
            _selectedColor = color;
            if (_selectedSwatch != null)
            {
                _selectedSwatch.BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
                _selectedSwatch.BorderThickness = new Thickness(1);
            }

            swatch.BorderBrush = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3));
            swatch.BorderThickness = new Thickness(2);
            _selectedSwatch = swatch;
        }
    }
}