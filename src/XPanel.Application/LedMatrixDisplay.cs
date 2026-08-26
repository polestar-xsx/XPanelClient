using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace XPanel.Application
{
    public sealed class LedMatrixDisplay : Image
    {
        public static readonly DependencyProperty SourceBitmapProperty = DependencyProperty.Register(
            nameof(SourceBitmap),
            typeof(BitmapSource),
            typeof(LedMatrixDisplay),
            new PropertyMetadata(null, OnRenderingInputChanged));

        public static readonly DependencyProperty LedCellSizeProperty = DependencyProperty.Register(
            nameof(LedCellSize),
            typeof(int),
            typeof(LedMatrixDisplay),
            new PropertyMetadata(10, OnRenderingInputChanged));

        public static readonly DependencyProperty LedDiameterProperty = DependencyProperty.Register(
            nameof(LedDiameter),
            typeof(int),
            typeof(LedMatrixDisplay),
            new PropertyMetadata(6, OnRenderingInputChanged));

        public BitmapSource? SourceBitmap
        {
            get => (BitmapSource?)GetValue(SourceBitmapProperty);
            set => SetValue(SourceBitmapProperty, value);
        }

        public int LedCellSize
        {
            get => (int)GetValue(LedCellSizeProperty);
            set => SetValue(LedCellSizeProperty, value);
        }

        public int LedDiameter
        {
            get => (int)GetValue(LedDiameterProperty);
            set => SetValue(LedDiameterProperty, value);
        }

        public BitmapSource? RenderedBitmap { get; private set; }

        public LedMatrixDisplay()
        {
            Stretch = Stretch.None;
            SnapsToDevicePixels = true;
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        }

        private static void OnRenderingInputChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
        {
            ((LedMatrixDisplay)dependencyObject).RenderMatrix();
        }

        private void RenderMatrix()
        {
            if (SourceBitmap == null || LedCellSize < 1 || LedDiameter < 1 || LedDiameter > LedCellSize)
            {
                Source = null;
                RenderedBitmap = null;
                return;
            }

            int sourceStride = SourceBitmap.PixelWidth * 4;
            var sourcePixels = new byte[SourceBitmap.PixelHeight * sourceStride];
            SourceBitmap.CopyPixels(sourcePixels, sourceStride, 0);

            int outputWidth = SourceBitmap.PixelWidth * LedCellSize;
            int outputHeight = SourceBitmap.PixelHeight * LedCellSize;
            int outputStride = outputWidth * 4;
            var outputPixels = new byte[outputHeight * outputStride];
            Fill(outputPixels, 0x0A, 0x0A, 0x0A);

            double radius = LedDiameter / 2.0;
            double radiusSquared = radius * radius;
            for (int y = 0; y < SourceBitmap.PixelHeight; y++)
            {
                for (int x = 0; x < SourceBitmap.PixelWidth; x++)
                {
                    int sourceOffset = y * sourceStride + x * 4;
                    byte blue = sourcePixels[sourceOffset];
                    byte green = sourcePixels[sourceOffset + 1];
                    byte red = sourcePixels[sourceOffset + 2];
                    byte maximumChannel = Math.Max(red, Math.Max(green, blue));
                    double baseWeight = 1.0 - maximumChannel / 255.0;
                    red = BlendLedColor(red, baseWeight);
                    green = BlendLedColor(green, baseWeight);
                    blue = BlendLedColor(blue, baseWeight);

                    for (int cellY = 0; cellY < LedCellSize; cellY++)
                    {
                        double offsetY = cellY + 0.5 - LedCellSize / 2.0;
                        for (int cellX = 0; cellX < LedCellSize; cellX++)
                        {
                            double offsetX = cellX + 0.5 - LedCellSize / 2.0;
                            if (offsetX * offsetX + offsetY * offsetY > radiusSquared)
                            {
                                continue;
                            }

                            int targetX = x * LedCellSize + cellX;
                            int targetY = y * LedCellSize + cellY;
                            int targetOffset = targetY * outputStride + targetX * 4;
                            outputPixels[targetOffset] = blue;
                            outputPixels[targetOffset + 1] = green;
                            outputPixels[targetOffset + 2] = red;
                            outputPixels[targetOffset + 3] = 255;
                        }
                    }
                }
            }

            var output = new WriteableBitmap(outputWidth, outputHeight, 96, 96, PixelFormats.Bgra32, null);
            output.WritePixels(new Int32Rect(0, 0, outputWidth, outputHeight), outputPixels, outputStride, 0);
            output.Freeze();
            Source = output;
            RenderedBitmap = output;
            Width = outputWidth;
            Height = outputHeight;
        }

        private static byte BlendLedColor(byte sourceColor, double baseWeight)
        {
            const byte ledBaseColor = 0x3A;
            return (byte)Math.Round(sourceColor + ledBaseColor * baseWeight);
        }

        private static void Fill(byte[] pixels, byte red, byte green, byte blue)
        {
            for (int index = 0; index < pixels.Length; index += 4)
            {
                pixels[index] = blue;
                pixels[index + 1] = green;
                pixels[index + 2] = red;
                pixels[index + 3] = 255;
            }
        }
    }
}