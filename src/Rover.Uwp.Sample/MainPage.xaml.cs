using System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Rover.Uwp.Sample
{
    public sealed partial class MainPage : Page
    {
        public MainPage()
        {
            this.InitializeComponent();
            UpdateColorPreview();

            // Subscribe after InitializeComponent so events don't fire before
            // all named elements are available.
            RedSlider.ValueChanged += Slider_ValueChanged;
            GreenSlider.ValueChanged += Slider_ValueChanged;
            BlueSlider.ValueChanged += Slider_ValueChanged;
        }

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
    }
}
