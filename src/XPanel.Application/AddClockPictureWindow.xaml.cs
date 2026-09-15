using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SixLabors.ImageSharp.Formats.Gif;
using ImageSharpImage = SixLabors.ImageSharp.Image;
using ImageSharpBgra32 = SixLabors.ImageSharp.PixelFormats.Bgra32;
using WinForms = System.Windows.Forms;

namespace XPanel.Application
{
    public partial class AddClockPictureWindow : Window
    {
        private const int MatrixSize = 32;
        private const int CellSize = 10;

        private byte[]? _sourcePixels;
        private int _sourceWidth;
        private int _sourceHeight;
        private readonly List<GifSourceFrame> _gifFrames = new();
        private readonly DispatcherTimer _gifPreviewTimer = new();
        private int _gifFrameIndex;

        // 卡通化处理后的采样源（随强度重建）
        private byte[]? _displayPixels;
        private double _cartoonStrength;

        // 拼豆（有限调色板）模式
        private bool _beadEnabled;
        private int _beadColorCount = 16;
        private bool _beadDither;
        private bool _outlineEnabled;
        private double _outlineThreshold = 150;

        // 源图像素 -> 点阵像素的映射：matrix = source * scale + offset
        private double _scale = 1.0;
        private double _fitScale = 1.0;
        private double _offsetX;
        private double _offsetY;

        private bool _isDragging;
        private Point _lastMousePosition;

        // 绘制图层：以源图像素坐标存储，随图片平移/缩放一起移动
        private Dictionary<(int X, int Y), Color> _paintBySource = new();
        private readonly Stack<Dictionary<(int, int), Color>> _undoStack = new();
        private readonly Stack<Dictionary<(int, int), Color>> _redoStack = new();
        private bool _paintMode;
        private bool _eyedropperMode;
        private Color _selectedPaintColor = Colors.White;
        private Border? _selectedSwatch;
        private byte[]? _previewPixels;
        private int _brushSize = 1;

        private static readonly string[] BasicColors =
        {
            "#000000", "#7F7F7F", "#880015", "#ED1C24", "#FF7F27", "#FFF200", "#22B14C", "#00A2E8",
            "#3F48CC", "#A349A4", "#FFFFFF", "#C3C3C3", "#B97A57", "#FFAEC9", "#FFC90E", "#EFE4B0",
            "#B5E61D", "#99D9EA", "#7092BE", "#C8BFE7",
        };

        // 用户确认后合成的 32x32 点阵位图
        public WriteableBitmap? ResultBitmap { get; private set; }

        public AddClockPictureWindow()
        {
            InitializeComponent();
            _gifPreviewTimer.Tick += GifPreviewTimer_Tick;
            Closed += (_, _) => _gifPreviewTimer.Stop();
            BuildPalette();
            RenderPreview();
        }

        private void ChosePicture_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Chose Picture",
                Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
                CheckFileExists = true,
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                LoadSourceImage(dialog.FileName);
                FitImageToMatrix();
                ExtractBasicColorsFromImage();
                _paintBySource.Clear();
                _undoStack.Clear();
                _redoStack.Clear();
                UpdateUndoRedoState();
                RenderPreview();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to load image: {ex.Message}", "Add Picture",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (ResultBitmap == null)
            {
                return;
            }

            string? name = PromptForName();
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            name = name.Trim();
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                MessageBox.Show(this, "The name contains invalid characters.", "Add Picture",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string directory = Path.Combine(AppContext.BaseDirectory, "resources", "ClockBackground");
            Directory.CreateDirectory(directory);
            bool animatedGif = _gifFrames.Count > 1;
            string filePath = Path.Combine(directory, name + (animatedGif ? ".gif" : ".png"));
            if (File.Exists(filePath))
            {
                MessageBox.Show(this, $"A background named \"{name}\" already exists.", "Add Picture",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            try
            {
                if (animatedGif)
                {
                    SaveAnimatedGif(filePath);
                }
                else
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(ResultBitmap));
                    using var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write);
                    encoder.Save(stream);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to save image: {ex.Message}", "Add Picture",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            DialogResult = true;
            Close();
        }

        private string? PromptForName()
        {
            var window = new Window
            {
                Title = "Name Background",
                Width = 320,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(0xF8, 0xF9, 0xFA)),
            };

            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock { Text = "Enter a name for this background:", Margin = new Thickness(0, 0, 0, 8) });

            var textBox = new TextBox { FontSize = 14, Padding = new Thickness(6, 4, 6, 4) };
            panel.Children.Add(textBox);

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0),
            };
            var okButton = new Button { Content = "OK", Width = 72, Height = 28, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancelButton = new Button { Content = "Cancel", Width = 72, Height = 28, IsCancel = true };
            okButton.Click += (_, _) => window.DialogResult = true;
            buttonPanel.Children.Add(okButton);
            buttonPanel.Children.Add(cancelButton);
            panel.Children.Add(buttonPanel);

            window.Content = panel;
            textBox.Loaded += (_, _) => textBox.Focus();

            return window.ShowDialog() == true ? textBox.Text : null;
        }

        private void LoadSourceImage(string filePath)
        {
            _gifPreviewTimer.Stop();
            _gifFrames.Clear();
            _gifFrameIndex = 0;

            if (string.Equals(Path.GetExtension(filePath), ".gif", StringComparison.OrdinalIgnoreCase))
            {
                LoadGifFrames(filePath);
                if (_gifFrames.Count > 0)
                {
                    SetGifFrame(0);
                    if (_gifFrames.Count > 1)
                    {
                        HintText.Text = $"GIF · {_gifFrames.Count} frames · Scroll to zoom · Drag to move";
                        StartGifPreviewTimer();
                    }

                    return;
                }
            }

            HintText.Text = "Scroll to zoom · Drag to move";
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(filePath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            _sourceWidth = converted.PixelWidth;
            _sourceHeight = converted.PixelHeight;
            _sourcePixels = new byte[_sourceHeight * _sourceWidth * 4];
            converted.CopyPixels(_sourcePixels, _sourceWidth * 4, 0);
            RebuildDisplayPixels();
        }

        private void LoadGifFrames(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            foreach (BitmapFrame frame in decoder.Frames)
            {
                var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                int width = converted.PixelWidth;
                int height = converted.PixelHeight;
                var pixels = new byte[width * height * 4];
                converted.CopyPixels(pixels, width * 4, 0);
                _gifFrames.Add(new GifSourceFrame(pixels, width, height, GetGifFrameDelay(frame.Metadata as BitmapMetadata)));
            }
        }

        private static int GetGifFrameDelay(BitmapMetadata? metadata)
        {
            try
            {
                object? value = metadata?.GetQuery("/grctlext/Delay");
                int delayCentiseconds = value == null ? 10 : Convert.ToInt32(value);
                return Math.Max(20, delayCentiseconds * 10);
            }
            catch
            {
                return 100;
            }
        }

        private void SetGifFrame(int frameIndex)
        {
            GifSourceFrame frame = _gifFrames[frameIndex];
            _gifFrameIndex = frameIndex;
            _sourcePixels = frame.Pixels;
            _sourceWidth = frame.Width;
            _sourceHeight = frame.Height;
            RebuildDisplayPixels();
        }

        private void StartGifPreviewTimer()
        {
            if (_gifFrames.Count < 2)
            {
                return;
            }

            _gifPreviewTimer.Interval = TimeSpan.FromMilliseconds(_gifFrames[_gifFrameIndex].DelayMilliseconds);
            _gifPreviewTimer.Start();
        }

        private void GifPreviewTimer_Tick(object? sender, EventArgs e)
        {
            if (_gifFrames.Count < 2)
            {
                _gifPreviewTimer.Stop();
                return;
            }

            int nextFrame = (_gifFrameIndex + 1) % _gifFrames.Count;
            SetGifFrame(nextFrame);
            RenderPreview();
            _gifPreviewTimer.Interval = TimeSpan.FromMilliseconds(_gifFrames[nextFrame].DelayMilliseconds);
        }

        private void SaveAnimatedGif(string filePath)
        {
            _gifPreviewTimer.Stop();
            int previewFrameIndex = _gifFrameIndex;
            SixLabors.ImageSharp.Image<ImageSharpBgra32>? animation = null;
            try
            {
                for (int index = 0; index < _gifFrames.Count; index++)
                {
                    SetGifFrame(index);
                    RenderPreview();
                    if (_previewPixels == null)
                    {
                        throw new InvalidOperationException("GIF frame rendering failed.");
                    }

                    using var renderedFrame = ImageSharpImage.LoadPixelData<ImageSharpBgra32>(
                        _previewPixels,
                        MatrixSize,
                        MatrixSize);
                    SixLabors.ImageSharp.MetadataExtensions.GetGifMetadata(renderedFrame.Frames.RootFrame.Metadata).FrameDelay =
                        Math.Max(2, _gifFrames[index].DelayMilliseconds / 10);
                    SixLabors.ImageSharp.MetadataExtensions.GetGifMetadata(renderedFrame.Frames.RootFrame.Metadata).DisposalMethod =
                        GifDisposalMethod.RestoreToBackground;

                    if (animation == null)
                    {
                        animation = renderedFrame.Clone();
                        SixLabors.ImageSharp.MetadataExtensions.GetGifMetadata(animation.Metadata).RepeatCount = 0;
                    }
                    else
                    {
                        animation.Frames.AddFrame(renderedFrame.Frames.RootFrame);
                    }
                }

                if (animation == null)
                {
                    throw new InvalidOperationException("GIF contains no frames.");
                }

                var encoder = new GifEncoder { ColorTableMode = GifColorTableMode.Global };
                using (var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write))
                {
                    animation.Save(stream, encoder);
                }

                using var verified = ImageSharpImage.Load(filePath);
                if (verified.Frames.Count != _gifFrames.Count)
                {
                    File.Delete(filePath);
                    throw new InvalidDataException(
                        $"GIF verification failed: expected {_gifFrames.Count} frames, decoded {verified.Frames.Count}.");
                }
            }
            finally
            {
                animation?.Dispose();
                SetGifFrame(Math.Min(previewFrameIndex, _gifFrames.Count - 1));
                RenderPreview();
                StartGifPreviewTimer();
            }
        }

        private void RebuildDisplayPixels()
        {
            _displayPixels = _sourcePixels == null
                ? null
                : ApplyCartoon(_sourcePixels, _sourceWidth, _sourceHeight, _cartoonStrength);
        }

        // 卡通化：边缘柔化模糊 + 色调分离 + 饱和度增强，强度 0~1
        private static byte[] ApplyCartoon(byte[] src, int width, int height, double strength)
        {
            var buffer = (byte[])src.Clone();
            if (strength <= 0.001)
            {
                return buffer;
            }

            int passes = (int)Math.Round(strength * 2);
            for (int i = 0; i < passes; i++)
            {
                buffer = BoxBlur3x3(buffer, width, height);
            }

            int levels = Math.Clamp((int)Math.Round(8 - strength * 5), 3, 8);
            double step = 255.0 / (levels - 1);
            double saturation = 1.0 + strength * 0.6;

            for (int o = 0; o < buffer.Length; o += 4)
            {
                double b = buffer[o];
                double g = buffer[o + 1];
                double r = buffer[o + 2];
                double gray = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                r = gray + (r - gray) * saturation;
                g = gray + (g - gray) * saturation;
                b = gray + (b - gray) * saturation;
                buffer[o] = Posterize(b, step);
                buffer[o + 1] = Posterize(g, step);
                buffer[o + 2] = Posterize(r, step);
            }

            return buffer;
        }

        private static byte Posterize(double value, double step)
        {
            return (byte)Math.Clamp(Math.Round(value / step) * step, 0.0, 255.0);
        }

        private static byte[] BoxBlur3x3(byte[] src, int width, int height)
        {
            var dst = new byte[src.Length];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int sumB = 0, sumG = 0, sumR = 0, count = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= height)
                        {
                            continue;
                        }

                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx;
                            if (nx < 0 || nx >= width)
                            {
                                continue;
                            }

                            int o = (ny * width + nx) * 4;
                            sumB += src[o];
                            sumG += src[o + 1];
                            sumR += src[o + 2];
                            count++;
                        }
                    }

                    int offset = (y * width + x) * 4;
                    dst[offset] = (byte)(sumB / count);
                    dst[offset + 1] = (byte)(sumG / count);
                    dst[offset + 2] = (byte)(sumR / count);
                    dst[offset + 3] = src[offset + 3];
                }
            }

            return dst;
        }

        private void FitImageToMatrix()
        {
            if (_sourceWidth <= 0 || _sourceHeight <= 0)
            {
                return;
            }

            _scale = Math.Min((double)MatrixSize / _sourceWidth, (double)MatrixSize / _sourceHeight);
            _fitScale = _scale;
            _offsetX = (MatrixSize - _sourceWidth * _scale) / 2.0;
            _offsetY = (MatrixSize - _sourceHeight * _scale) / 2.0;
        }

        private void RenderPreview()
        {
            int stride = MatrixSize * 4;
            var pixels = new byte[MatrixSize * stride];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i + 3] = 255; // 黑底，alpha 不透明
            }

            if (_scale > 0 && _sourcePixels != null)
            {
                var mask = new bool[MatrixSize * MatrixSize];
                for (int my = 0; my < MatrixSize; my++)
                {
                    for (int mx = 0; mx < MatrixSize; mx++)
                    {
                        // 该点阵格覆盖的源图区域，用面积覆盖率加权平均，得到更好的像素风格缩放
                        double srcXMin = (mx - _offsetX) / _scale;
                        double srcXMax = (mx + 1 - _offsetX) / _scale;
                        double srcYMin = (my - _offsetY) / _scale;
                        double srcYMax = (my + 1 - _offsetY) / _scale;

                        if (SampleSourceArea(srcXMin, srcXMax, srcYMin, srcYMax, out byte r, out byte g, out byte b))
                        {
                            int targetOffset = my * stride + mx * 4;
                            pixels[targetOffset] = b;
                            pixels[targetOffset + 1] = g;
                            pixels[targetOffset + 2] = r;
                            pixels[targetOffset + 3] = 255;
                            mask[my * MatrixSize + mx] = true;
                        }
                    }
                }

                if (_beadEnabled)
                {
                    ApplyBeadQuantization(pixels, stride, mask);
                }

                if (_outlineEnabled)
                {
                    ApplyOutline(pixels, stride, mask);
                }
            }

            // 绘制图层：将每个笔迹（源图坐标）正向映射到唯一点阵格，随图片平移/缩放且始终为单格
            if (_scale > 0)
            {
                foreach (var mark in _paintBySource)
                {
                    int mx = (int)Math.Floor((mark.Key.X + 0.5) * _scale + _offsetX);
                    int my = (int)Math.Floor((mark.Key.Y + 0.5) * _scale + _offsetY);
                    if (mx < 0 || my < 0 || mx >= MatrixSize || my >= MatrixSize)
                    {
                        continue;
                    }

                    Color color = mark.Value;
                    int targetOffset = my * stride + mx * 4;
                    pixels[targetOffset] = color.B;
                    pixels[targetOffset + 1] = color.G;
                    pixels[targetOffset + 2] = color.R;
                    pixels[targetOffset + 3] = 255;
                }
            }

            var bitmap = new WriteableBitmap(MatrixSize, MatrixSize, 96, 96, PixelFormats.Bgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, MatrixSize, MatrixSize), pixels, stride, 0);
            bitmap.Freeze();

            _previewPixels = pixels;
            ResultBitmap = bitmap;
            PreviewMatrix.SourceBitmap = bitmap;
        }

        // sRGB <-> 线性空间查表，保证按面积平均时颜色/亮度正确
        private static readonly double[] SrgbToLinear = BuildSrgbToLinear();

        private static double[] BuildSrgbToLinear()
        {
            var table = new double[256];
            for (int i = 0; i < 256; i++)
            {
                double c = i / 255.0;
                table[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }

            return table;
        }

        private static byte LinearToSrgb(double value)
        {
            value = Math.Clamp(value, 0.0, 1.0);
            double c = value <= 0.0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1.0 / 2.4) - 0.055;
            return (byte)Math.Round(Math.Clamp(c, 0.0, 1.0) * 255.0);
        }

        // 对源图区域做面积覆盖率加权、gamma 正确的平均，得到像素风格缩放
        private bool SampleSourceArea(double xMin, double xMax, double yMin, double yMax, out byte r, out byte g, out byte b)
        {
            r = g = b = 0;
            var source = _displayPixels;
            if (source == null)
            {
                return false;
            }

            int sxStart = (int)Math.Floor(xMin);
            int sxEnd = (int)Math.Ceiling(xMax) - 1;
            int syStart = (int)Math.Floor(yMin);
            int syEnd = (int)Math.Ceiling(yMax) - 1;

            double sumR = 0, sumG = 0, sumB = 0, sumW = 0;
            for (int sy = syStart; sy <= syEnd; sy++)
            {
                if (sy < 0 || sy >= _sourceHeight)
                {
                    continue;
                }

                double coverY = Math.Min(yMax, sy + 1) - Math.Max(yMin, sy);
                if (coverY <= 0)
                {
                    continue;
                }

                for (int sx = sxStart; sx <= sxEnd; sx++)
                {
                    if (sx < 0 || sx >= _sourceWidth)
                    {
                        continue;
                    }

                    double coverX = Math.Min(xMax, sx + 1) - Math.Max(xMin, sx);
                    if (coverX <= 0)
                    {
                        continue;
                    }

                    int offset = (sy * _sourceWidth + sx) * 4;
                    double alpha = source[offset + 3] / 255.0;
                    double weight = coverX * coverY * alpha;
                    if (weight <= 0)
                    {
                        continue;
                    }

                    sumB += SrgbToLinear[source[offset]] * weight;
                    sumG += SrgbToLinear[source[offset + 1]] * weight;
                    sumR += SrgbToLinear[source[offset + 2]] * weight;
                    sumW += weight;
                }
            }

            if (sumW <= 0)
            {
                return false;
            }

            r = LinearToSrgb(sumR / sumW);
            g = LinearToSrgb(sumG / sumW);
            b = LinearToSrgb(sumB / sumW);
            return true;
        }

        // 拼豆化：自动取 N 色调色板 + CIEDE2000 就近配色 +（可选）Floyd–Steinberg 抖动
        private void ApplyBeadQuantization(byte[] pixels, int stride, bool[] mask)
        {
            var samples = new List<(byte R, byte G, byte B)>();
            for (int i = 0; i < mask.Length; i++)
            {
                if (!mask[i])
                {
                    continue;
                }

                int o = (i / MatrixSize) * stride + (i % MatrixSize) * 4;
                samples.Add((pixels[o + 2], pixels[o + 1], pixels[o]));
            }

            if (samples.Count == 0)
            {
                return;
            }

            var palette = MedianCut(samples, Math.Clamp(_beadColorCount, 2, 64));
            if (palette.Count == 0)
            {
                return;
            }

            var paletteLab = palette.Select(c => RgbToLab(c.R, c.G, c.B)).ToArray();

            if (!_beadDither)
            {
                for (int i = 0; i < mask.Length; i++)
                {
                    if (!mask[i])
                    {
                        continue;
                    }

                    int o = (i / MatrixSize) * stride + (i % MatrixSize) * 4;
                    int idx = NearestPaletteIndex(pixels[o + 2], pixels[o + 1], pixels[o], paletteLab);
                    var c = palette[idx];
                    pixels[o] = c.B;
                    pixels[o + 1] = c.G;
                    pixels[o + 2] = c.R;
                }

                return;
            }

            // Floyd–Steinberg：在浮点缓冲上扩散量化误差
            var work = new double[MatrixSize * MatrixSize * 3];
            for (int i = 0; i < mask.Length; i++)
            {
                int o = (i / MatrixSize) * stride + (i % MatrixSize) * 4;
                work[i * 3] = pixels[o + 2];
                work[i * 3 + 1] = pixels[o + 1];
                work[i * 3 + 2] = pixels[o];
            }

            for (int y = 0; y < MatrixSize; y++)
            {
                for (int x = 0; x < MatrixSize; x++)
                {
                    int cell = y * MatrixSize + x;
                    if (!mask[cell])
                    {
                        continue;
                    }

                    double or = work[cell * 3];
                    double og = work[cell * 3 + 1];
                    double ob = work[cell * 3 + 2];
                    int idx = NearestPaletteIndex(
                        (byte)Math.Clamp(or, 0, 255),
                        (byte)Math.Clamp(og, 0, 255),
                        (byte)Math.Clamp(ob, 0, 255),
                        paletteLab);
                    var c = palette[idx];

                    int o = y * stride + x * 4;
                    pixels[o] = c.B;
                    pixels[o + 1] = c.G;
                    pixels[o + 2] = c.R;

                    double er = or - c.R;
                    double eg = og - c.G;
                    double eb = ob - c.B;
                    DiffuseError(work, mask, x + 1, y, er, eg, eb, 7.0 / 16.0);
                    DiffuseError(work, mask, x - 1, y + 1, er, eg, eb, 3.0 / 16.0);
                    DiffuseError(work, mask, x, y + 1, er, eg, eb, 5.0 / 16.0);
                    DiffuseError(work, mask, x + 1, y + 1, er, eg, eb, 1.0 / 16.0);
                }
            }
        }

        private static void DiffuseError(double[] work, bool[] mask, int x, int y, double er, double eg, double eb, double factor)
        {
            if (x < 0 || y < 0 || x >= MatrixSize || y >= MatrixSize)
            {
                return;
            }

            int cell = y * MatrixSize + x;
            if (!mask[cell])
            {
                return;
            }

            work[cell * 3] += er * factor;
            work[cell * 3 + 1] += eg * factor;
            work[cell * 3 + 2] += eb * factor;
        }

        private static int NearestPaletteIndex(byte r, byte g, byte b, (double L, double A, double B)[] paletteLab)
        {
            var lab = RgbToLab(r, g, b);
            int best = 0;
            double bestDist = double.MaxValue;
            for (int i = 0; i < paletteLab.Length; i++)
            {
                double d = Ciede2000(lab, paletteLab[i]);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }

            return best;
        }

        // 中位切分生成调色板
        private static List<(byte R, byte G, byte B)> MedianCut(List<(byte R, byte G, byte B)> colors, int count)
        {
            var buckets = new List<List<(byte R, byte G, byte B)>> { colors };

            while (buckets.Count < count)
            {
                int targetIndex = -1;
                int maxRange = -1;
                int splitChannel = 0;
                for (int i = 0; i < buckets.Count; i++)
                {
                    if (buckets[i].Count < 2)
                    {
                        continue;
                    }

                    ChannelRanges(buckets[i], out int rangeR, out int rangeG, out int rangeB);
                    int localMax = Math.Max(rangeR, Math.Max(rangeG, rangeB));
                    if (localMax > maxRange)
                    {
                        maxRange = localMax;
                        targetIndex = i;
                        splitChannel = rangeR >= rangeG && rangeR >= rangeB ? 0 : (rangeG >= rangeB ? 1 : 2);
                    }
                }

                if (targetIndex < 0)
                {
                    break;
                }

                var bucket = buckets[targetIndex];
                bucket.Sort((a, b) => splitChannel switch
                {
                    0 => a.R.CompareTo(b.R),
                    1 => a.G.CompareTo(b.G),
                    _ => a.B.CompareTo(b.B),
                });

                int median = bucket.Count / 2;
                var left = bucket.GetRange(0, median);
                var right = bucket.GetRange(median, bucket.Count - median);
                buckets[targetIndex] = left;
                buckets.Add(right);
            }

            var palette = new List<(byte R, byte G, byte B)>();
            foreach (var bucket in buckets)
            {
                if (bucket.Count == 0)
                {
                    continue;
                }

                long sr = 0, sg = 0, sb = 0;
                foreach (var c in bucket)
                {
                    sr += c.R;
                    sg += c.G;
                    sb += c.B;
                }

                palette.Add(((byte)(sr / bucket.Count), (byte)(sg / bucket.Count), (byte)(sb / bucket.Count)));
            }

            return palette;
        }

        private static void ChannelRanges(List<(byte R, byte G, byte B)> bucket, out int rangeR, out int rangeG, out int rangeB)
        {
            int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
            foreach (var c in bucket)
            {
                minR = Math.Min(minR, c.R); maxR = Math.Max(maxR, c.R);
                minG = Math.Min(minG, c.G); maxG = Math.Max(maxG, c.G);
                minB = Math.Min(minB, c.B); maxB = Math.Max(maxB, c.B);
            }

            rangeR = maxR - minR;
            rangeG = maxG - minG;
            rangeB = maxB - minB;
        }

        private static (double L, double A, double B) RgbToLab(byte r, byte g, byte b)
        {
            double rl = SrgbToLinear[r];
            double gl = SrgbToLinear[g];
            double bl = SrgbToLinear[b];

            // 线性 RGB -> XYZ (D65)
            double x = rl * 0.4124564 + gl * 0.3575761 + bl * 0.1804375;
            double y = rl * 0.2126729 + gl * 0.7151522 + bl * 0.0721750;
            double z = rl * 0.0193339 + gl * 0.1191920 + bl * 0.9503041;

            x /= 0.95047;
            z /= 1.08883;

            double fx = LabF(x);
            double fy = LabF(y);
            double fz = LabF(z);

            return (116.0 * fy - 16.0, 500.0 * (fx - fy), 200.0 * (fy - fz));
        }

        private static double LabF(double t)
        {
            return t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116.0;
        }

        private static double Ciede2000((double L, double A, double B) c1, (double L, double A, double B) c2)
        {
            double kL = 1.0, kC = 1.0, kH = 1.0;

            double c1Ab = Math.Sqrt(c1.A * c1.A + c1.B * c1.B);
            double c2Ab = Math.Sqrt(c2.A * c2.A + c2.B * c2.B);
            double avgC = (c1Ab + c2Ab) / 2.0;

            double g = 0.5 * (1.0 - Math.Sqrt(Math.Pow(avgC, 7) / (Math.Pow(avgC, 7) + Math.Pow(25.0, 7))));
            double a1p = (1.0 + g) * c1.A;
            double a2p = (1.0 + g) * c2.A;

            double c1p = Math.Sqrt(a1p * a1p + c1.B * c1.B);
            double c2p = Math.Sqrt(a2p * a2p + c2.B * c2.B);

            double h1p = HuePrime(c1.B, a1p);
            double h2p = HuePrime(c2.B, a2p);

            double dLp = c2.L - c1.L;
            double dCp = c2p - c1p;

            double dhp;
            if (c1p * c2p == 0)
            {
                dhp = 0;
            }
            else if (Math.Abs(h2p - h1p) <= 180)
            {
                dhp = h2p - h1p;
            }
            else if (h2p - h1p > 180)
            {
                dhp = h2p - h1p - 360;
            }
            else
            {
                dhp = h2p - h1p + 360;
            }

            double dHp = 2.0 * Math.Sqrt(c1p * c2p) * Math.Sin(DegToRad(dhp) / 2.0);

            double avgLp = (c1.L + c2.L) / 2.0;
            double avgCp = (c1p + c2p) / 2.0;

            double avghp;
            if (c1p * c2p == 0)
            {
                avghp = h1p + h2p;
            }
            else if (Math.Abs(h1p - h2p) <= 180)
            {
                avghp = (h1p + h2p) / 2.0;
            }
            else if (h1p + h2p < 360)
            {
                avghp = (h1p + h2p + 360) / 2.0;
            }
            else
            {
                avghp = (h1p + h2p - 360) / 2.0;
            }

            double t = 1.0
                - 0.17 * Math.Cos(DegToRad(avghp - 30))
                + 0.24 * Math.Cos(DegToRad(2 * avghp))
                + 0.32 * Math.Cos(DegToRad(3 * avghp + 6))
                - 0.20 * Math.Cos(DegToRad(4 * avghp - 63));

            double dTheta = 30.0 * Math.Exp(-Math.Pow((avghp - 275) / 25.0, 2));
            double rc = 2.0 * Math.Sqrt(Math.Pow(avgCp, 7) / (Math.Pow(avgCp, 7) + Math.Pow(25.0, 7)));
            double sl = 1.0 + 0.015 * Math.Pow(avgLp - 50, 2) / Math.Sqrt(20 + Math.Pow(avgLp - 50, 2));
            double sc = 1.0 + 0.045 * avgCp;
            double sh = 1.0 + 0.015 * avgCp * t;
            double rt = -Math.Sin(DegToRad(2 * dTheta)) * rc;

            double termL = dLp / (kL * sl);
            double termC = dCp / (kC * sc);
            double termH = dHp / (kH * sh);

            return Math.Sqrt(termL * termL + termC * termC + termH * termH + rt * termC * termH);
        }

        private static double HuePrime(double b, double ap)
        {
            if (ap == 0 && b == 0)
            {
                return 0;
            }

            double angle = RadToDeg(Math.Atan2(b, ap));
            return angle < 0 ? angle + 360 : angle;
        }

        private static double DegToRad(double deg) => deg * Math.PI / 180.0;

        private static double RadToDeg(double rad) => rad * 180.0 / Math.PI;

        // 黑色描边：Sobel 边缘检测 + 非极大值抑制细化，仅最强边缘处涂黑
        private void ApplyOutline(byte[] pixels, int stride, bool[] mask)
        {
            var lum = new double[MatrixSize * MatrixSize];
            for (int i = 0; i < lum.Length; i++)
            {
                int o = (i / MatrixSize) * stride + (i % MatrixSize) * 4;
                lum[i] = 0.2126 * pixels[o + 2] + 0.7152 * pixels[o + 1] + 0.0722 * pixels[o];
            }

            var mag = new double[MatrixSize * MatrixSize];
            var dir = new double[MatrixSize * MatrixSize];
            for (int y = 1; y < MatrixSize - 1; y++)
            {
                for (int x = 1; x < MatrixSize - 1; x++)
                {
                    int cell = y * MatrixSize + x;
                    if (!mask[cell])
                    {
                        continue;
                    }

                    double tl = lum[(y - 1) * MatrixSize + (x - 1)];
                    double t = lum[(y - 1) * MatrixSize + x];
                    double tr = lum[(y - 1) * MatrixSize + (x + 1)];
                    double l = lum[y * MatrixSize + (x - 1)];
                    double rr = lum[y * MatrixSize + (x + 1)];
                    double bl = lum[(y + 1) * MatrixSize + (x - 1)];
                    double bb = lum[(y + 1) * MatrixSize + x];
                    double br = lum[(y + 1) * MatrixSize + (x + 1)];

                    double gx = -tl - 2 * l - bl + tr + 2 * rr + br;
                    double gy = -tl - 2 * t - tr + bl + 2 * bb + br;
                    mag[cell] = Math.Sqrt(gx * gx + gy * gy);
                    double angle = Math.Atan2(gy, gx) * 180.0 / Math.PI;
                    if (angle < 0)
                    {
                        angle += 180;
                    }

                    dir[cell] = angle;
                }
            }

            // 非极大值抑制：只保留沿梯度方向的局部最大值，得到 1 格宽的细线
            var edge = new bool[MatrixSize * MatrixSize];
            for (int y = 1; y < MatrixSize - 1; y++)
            {
                for (int x = 1; x < MatrixSize - 1; x++)
                {
                    int cell = y * MatrixSize + x;
                    if (!mask[cell] || mag[cell] <= _outlineThreshold)
                    {
                        continue;
                    }

                    double angle = dir[cell];
                    int dx1, dy1, dx2, dy2;
                    if (angle < 22.5 || angle >= 157.5)
                    {
                        dx1 = -1; dy1 = 0; dx2 = 1; dy2 = 0;
                    }
                    else if (angle < 67.5)
                    {
                        dx1 = 1; dy1 = -1; dx2 = -1; dy2 = 1;
                    }
                    else if (angle < 112.5)
                    {
                        dx1 = 0; dy1 = -1; dx2 = 0; dy2 = 1;
                    }
                    else
                    {
                        dx1 = -1; dy1 = -1; dx2 = 1; dy2 = 1;
                    }

                    double m1 = mag[(y + dy1) * MatrixSize + (x + dx1)];
                    double m2 = mag[(y + dy2) * MatrixSize + (x + dx2)];
                    if (mag[cell] >= m1 && mag[cell] >= m2)
                    {
                        edge[cell] = true;
                    }
                }
            }

            for (int i = 0; i < edge.Length; i++)
            {
                if (!edge[i])
                {
                    continue;
                }

                int o = (i / MatrixSize) * stride + (i % MatrixSize) * 4;
                pixels[o] = 0;
                pixels[o + 1] = 0;
                pixels[o + 2] = 0;
                pixels[o + 3] = 255;
            }
        }

        private void PreviewArea_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_sourcePixels == null)
            {
                return;
            }

            Point position = e.GetPosition(PreviewArea);
            double cursorX = position.X / CellSize;
            double cursorY = position.Y / CellSize;

            double factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
            double minScale = Math.Min(0.05, _fitScale);
            double newScale = Math.Clamp(_scale * factor, minScale, 50.0);

            // 保持光标下的图像内容位置不变
            _offsetX = cursorX - (cursorX - _offsetX) * (newScale / _scale);
            _offsetY = cursorY - (cursorY - _offsetY) * (newScale / _scale);
            _scale = newScale;

            RenderPreview();
        }

        private void PreviewArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_eyedropperMode)
            {
                PickColorAt(e.GetPosition(PreviewArea));
                return;
            }

            if (_paintMode)
            {
                // 每次落笔前记录快照用于撤销
                _undoStack.Push(new Dictionary<(int, int), Color>(_paintBySource));
                _redoStack.Clear();
                UpdateUndoRedoState();

                PaintPixelAt(e.GetPosition(PreviewArea));
                PreviewArea.CaptureMouse();
                _isDragging = true;
                return;
            }

            if (_sourcePixels == null)
            {
                return;
            }

            _isDragging = true;
            _lastMousePosition = e.GetPosition(PreviewArea);
            PreviewArea.CaptureMouse();
        }

        private void PreviewArea_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging)
            {
                return;
            }

            if (_paintMode)
            {
                PaintPixelAt(e.GetPosition(PreviewArea));
                return;
            }

            Point current = e.GetPosition(PreviewArea);
            _offsetX += (current.X - _lastMousePosition.X) / CellSize;
            _offsetY += (current.Y - _lastMousePosition.Y) / CellSize;
            _lastMousePosition = current;

            RenderPreview();
        }

        private void PreviewArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging)
            {
                return;
            }

            _isDragging = false;
            PreviewArea.ReleaseMouseCapture();
        }

        private void PaintPixelAt(Point position)
        {
            int mx = (int)Math.Floor(position.X / CellSize);
            int my = (int)Math.Floor(position.Y / CellSize);
            if (mx < 0 || my < 0 || mx >= MatrixSize || my >= MatrixSize)
            {
                return;
            }

            int start = -(_brushSize / 2);
            for (int by = 0; by < _brushSize; by++)
            {
                for (int bx = 0; bx < _brushSize; bx++)
                {
                    int cx = mx + start + bx;
                    int cy = my + start + by;
                    if (cx < 0 || cy < 0 || cx >= MatrixSize || cy >= MatrixSize)
                    {
                        continue;
                    }

                    // 记录该点阵格中心对应的源图像素，渲染时正向映射回唯一点阵格
                    int sx = (int)Math.Floor((cx + 0.5 - _offsetX) / _scale);
                    int sy = (int)Math.Floor((cy + 0.5 - _offsetY) / _scale);
                    _paintBySource[(sx, sy)] = _selectedPaintColor;
                }
            }

            RenderPreview();
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (_undoStack.Count == 0)
            {
                return;
            }

            _redoStack.Push(new Dictionary<(int, int), Color>(_paintBySource));
            _paintBySource = _undoStack.Pop();
            RenderPreview();
            UpdateUndoRedoState();
        }

        private void Redo_Click(object sender, RoutedEventArgs e)
        {
            if (_redoStack.Count == 0)
            {
                return;
            }

            _undoStack.Push(new Dictionary<(int, int), Color>(_paintBySource));
            _paintBySource = _redoStack.Pop();
            RenderPreview();
            UpdateUndoRedoState();
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

        private void CartoonStrengthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _cartoonStrength = Math.Clamp(e.NewValue / 10.0, 0.0, 1.0);
            if (CartoonStrengthText != null)
            {
                CartoonStrengthText.Text = ((int)Math.Round(e.NewValue)).ToString();
            }

            RebuildDisplayPixels();
            RenderPreview();
        }

        private void BeadEnabled_Changed(object sender, RoutedEventArgs e)
        {
            _beadEnabled = BeadEnabledCheck.IsChecked == true;
            RenderPreview();
        }

        private void BeadColorSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _beadColorCount = Math.Clamp((int)Math.Round(e.NewValue), 2, 64);
            if (BeadColorText != null)
            {
                BeadColorText.Text = _beadColorCount.ToString();
            }

            if (_beadEnabled)
            {
                RenderPreview();
            }
        }

        private void BeadDither_Changed(object sender, RoutedEventArgs e)
        {
            _beadDither = BeadDitherCheck.IsChecked == true;
            if (_beadEnabled)
            {
                RenderPreview();
            }
        }

        private void Outline_Changed(object sender, RoutedEventArgs e)
        {
            _outlineEnabled = OutlineCheck.IsChecked == true;
            RenderPreview();
        }

        private void OutlineThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            _outlineThreshold = e.NewValue;
            if (OutlineThresholdText != null)
            {
                OutlineThresholdText.Text = ((int)Math.Round(e.NewValue)).ToString();
            }

            if (_outlineEnabled)
            {
                RenderPreview();
            }
        }

        private void Paint_Click(object sender, RoutedEventArgs e)
        {
            _paintMode = !_paintMode;
            if (_paintMode)
            {
                _eyedropperMode = false;
                EyedropperButton.Background = Brushes.Transparent;
            }

            PaintButton.Opacity = _paintMode ? 0.7 : 1.0;
            UpdatePreviewCursorAndHint();
        }

        private void Eyedropper_Click(object sender, MouseButtonEventArgs e)
        {
            _eyedropperMode = !_eyedropperMode;
            if (_eyedropperMode)
            {
                _paintMode = false;
                PaintButton.Opacity = 1.0;
            }

            EyedropperButton.Background = _eyedropperMode
                ? new SolidColorBrush(Color.FromRgb(0xBB, 0xDE, 0xFB))
                : Brushes.Transparent;
            UpdatePreviewCursorAndHint();
        }

        private void UpdatePreviewCursorAndHint()
        {
            if (_paintMode)
            {
                PreviewArea.Cursor = Cursors.Cross;
                HintText.Text = "Paint mode: click pixels to fill with the selected color";
            }
            else if (_eyedropperMode)
            {
                PreviewArea.Cursor = Cursors.Cross;
                HintText.Text = "Eyedropper: click a pixel to pick its color";
            }
            else
            {
                PreviewArea.Cursor = Cursors.SizeAll;
                HintText.Text = "Scroll to zoom · Drag to move";
            }
        }

        private void PickColorAt(Point position)
        {
            if (_previewPixels == null)
            {
                return;
            }

            int mx = (int)Math.Floor(position.X / CellSize);
            int my = (int)Math.Floor(position.Y / CellSize);
            if (mx < 0 || my < 0 || mx >= MatrixSize || my >= MatrixSize)
            {
                return;
            }

            int offset = my * MatrixSize * 4 + mx * 4;
            var picked = Color.FromRgb(_previewPixels[offset + 2], _previewPixels[offset + 1], _previewPixels[offset]);
            _selectedPaintColor = picked;

            // 将吸取的颜色放入第一个空白自定义色板并选中
            var target = CustomColorPanel.Children.OfType<Border>().FirstOrDefault(b => b.Tag is not Color);
            if (target != null)
            {
                target.Background = new SolidColorBrush(picked);
                target.Tag = picked;
                SelectSwatch(target, picked);
            }
        }

        private void ExtractBasicColorsFromImage()
        {
            BasicColorPanel.Children.Clear();
            _selectedSwatch = null;
            if (_sourcePixels == null)
            {
                return;
            }

            // 按 4bit/通道量化后统计出现频率最高的颜色
            var counts = new Dictionary<int, int>();
            for (int i = 0; i < _sourcePixels.Length; i += 4)
            {
                if (_sourcePixels[i + 3] < 8)
                {
                    continue;
                }

                int r = _sourcePixels[i + 2] >> 4;
                int g = _sourcePixels[i + 1] >> 4;
                int b = _sourcePixels[i] >> 4;
                int key = (r << 8) | (g << 4) | b;
                counts[key] = counts.TryGetValue(key, out int value) ? value + 1 : 1;
            }

            foreach (var pair in counts.OrderByDescending(kv => kv.Value).Take(24))
            {
                byte r = (byte)(((pair.Key >> 8) & 0xF) * 17);
                byte g = (byte)(((pair.Key >> 4) & 0xF) * 17);
                byte b = (byte)((pair.Key & 0xF) * 17);
                BasicColorPanel.Children.Add(CreateSwatch(Color.FromRgb(r, g, b)));
            }
        }

        private void BuildPalette()
        {
            foreach (var hex in BasicColors)
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                BasicColorPanel.Children.Add(CreateSwatch(color));
            }

            // 两排空白色板供用户自定义
            for (int i = 0; i < 16; i++)
            {
                CustomColorPanel.Children.Add(CreateCustomSwatch());
            }
        }

        private Border CreateSwatch(Color color)
        {
            var swatch = new Border
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(1),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Tag = color,
            };
            swatch.MouseLeftButtonDown += (_, _) => SelectSwatch(swatch, color);
            return swatch;
        }

        private Border CreateCustomSwatch()
        {
            var swatch = new Border
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(1),
                Background = new SolidColorBrush(Colors.White),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Tag = null,
            };
            swatch.MouseLeftButtonDown += CustomSwatch_Click;
            return swatch;
        }

        private void CustomSwatch_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border swatch)
            {
                return;
            }

            // 已有颜色且单击：直接选中；空白或双击：打开取色器设置
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
                var picked = Color.FromArgb(dialog.Color.A, dialog.Color.R, dialog.Color.G, dialog.Color.B);
                swatch.Background = new SolidColorBrush(picked);
                swatch.Tag = picked;
                SelectSwatch(swatch, picked);
            }
        }

        private void SelectSwatch(Border swatch, Color color)
        {
            _selectedPaintColor = color;

            if (_selectedSwatch != null)
            {
                _selectedSwatch.BorderBrush = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
                _selectedSwatch.BorderThickness = new Thickness(1);
            }

            swatch.BorderBrush = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3));
            swatch.BorderThickness = new Thickness(2);
            _selectedSwatch = swatch;
        }

        private sealed record GifSourceFrame(byte[] Pixels, int Width, int Height, int DelayMilliseconds);
    }
}
