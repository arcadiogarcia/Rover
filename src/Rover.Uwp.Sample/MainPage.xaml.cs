using System;
using Windows.UI;
using Windows.UI.Input.Inking;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Rover.Uwp.Sample
{
    public sealed partial class MainPage : Page
    {
        public MainPage()
        {
            try
            {
                this.InitializeComponent();
            }
            catch (Exception ex)
            {
                var path = System.IO.Path.Combine(
                    Windows.Storage.ApplicationData.Current.LocalFolder.Path,
                    "mainpage-crash.log");
                System.IO.File.WriteAllText(path,
                    $"{DateTimeOffset.Now:o} InitializeComponent FAILED:\r\n{ex}\r\n");
                throw;
            }

            UpdateColorPreview();

            // Subscribe after InitializeComponent so events don't fire before
            // all named elements are available.
            RedSlider.ValueChanged += Slider_ValueChanged;
            GreenSlider.ValueChanged += Slider_ValueChanged;
            BlueSlider.ValueChanged += Slider_ValueChanged;

            // Track text changes for char count
            TestTextBox.TextChanged += TextBox_TextChanged;
            MultiLineTextBox.TextChanged += TextBox_TextChanged;

            // Configure InkCanvas to accept all input types (pen, mouse, touch)
            TestInkCanvas.InkPresenter.InputDeviceTypes =
                Windows.UI.Core.CoreInputDeviceTypes.Pen |
                Windows.UI.Core.CoreInputDeviceTypes.Mouse |
                Windows.UI.Core.CoreInputDeviceTypes.Touch;

            // Default ink attributes — use a bright color visible on both dark and light themes
            var drawingAttrs = new InkDrawingAttributes
            {
                Color = Colors.DeepSkyBlue,
                Size = new Windows.Foundation.Size(4, 4),
                IgnorePressure = false,
                FitToCurve = true
            };
            TestInkCanvas.InkPresenter.UpdateDefaultDrawingAttributes(drawingAttrs);

            // Track stroke changes
            TestInkCanvas.InkPresenter.StrokesCollected += InkPresenter_StrokesCollected;
            TestInkCanvas.InkPresenter.StrokesErased += InkPresenter_StrokesErased;
        }

        #region Color Picker

        private void PresetColor_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string hex)
            {
                byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                byte b = Convert.ToByte(hex.Substring(4, 2), 16);

                RedSlider.Value = r;
                GreenSlider.Value = g;
                BlueSlider.Value = b;
            }
        }

        private void Slider_ValueChanged(object sender,
            Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            UpdateColorPreview();
        }

        private void UpdateColorPreview()
        {
            if (PreviewBrush == null) return;

            byte r = (byte)RedSlider.Value;
            byte g = (byte)GreenSlider.Value;
            byte b = (byte)BlueSlider.Value;

            PreviewBrush.Color = Color.FromArgb(255, r, g, b);
            HexLabel.Text = $"#{r:X2}{g:X2}{b:X2}";
            RedValue.Text = r.ToString();
            GreenValue.Text = g.ToString();
            BlueValue.Text = b.ToString();
        }

        #endregion

        #region Text Input

        private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            int total = (TestTextBox.Text?.Length ?? 0) + (MultiLineTextBox.Text?.Length ?? 0);
            CharCountLabel.Text = $"Chars: {total}";
        }

        private void ClearText_Click(object sender, RoutedEventArgs e)
        {
            TestTextBox.Text = "";
            MultiLineTextBox.Text = "";
        }

        #endregion

        #region Ink Canvas

        private void ClearInk_Click(object sender, RoutedEventArgs e)
        {
            TestInkCanvas.InkPresenter.StrokeContainer.Clear();
            StrokeCountLabel.Text = "Strokes: 0";
        }

        private void InkMode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string mode)
            {
                var attrs = TestInkCanvas.InkPresenter.CopyDefaultDrawingAttributes();
                switch (mode)
                {
                    case "pen":
                        attrs.Size = new Windows.Foundation.Size(4, 4);
                        attrs.Color = Colors.Black;
                        attrs.DrawAsHighlighter = false;
                        TestInkCanvas.InkPresenter.InputProcessingConfiguration.Mode = InkInputProcessingMode.Inking;
                        break;
                    case "eraser":
                        TestInkCanvas.InkPresenter.InputProcessingConfiguration.Mode = InkInputProcessingMode.Erasing;
                        return;
                    case "highlighter":
                        attrs.Size = new Windows.Foundation.Size(16, 8);
                        attrs.Color = Colors.Yellow;
                        attrs.DrawAsHighlighter = true;
                        TestInkCanvas.InkPresenter.InputProcessingConfiguration.Mode = InkInputProcessingMode.Inking;
                        break;
                }
                TestInkCanvas.InkPresenter.UpdateDefaultDrawingAttributes(attrs);
            }
        }

        private void InkPresenter_StrokesCollected(InkPresenter sender, InkStrokesCollectedEventArgs args)
        {
            UpdateStrokeCount();
        }

        private void InkPresenter_StrokesErased(InkPresenter sender, InkStrokesErasedEventArgs args)
        {
            UpdateStrokeCount();
        }

        private void UpdateStrokeCount()
        {
            int count = TestInkCanvas.InkPresenter.StrokeContainer.GetStrokes().Count;
            StrokeCountLabel.Text = $"Strokes: {count}";
        }

        #endregion
    }
}
