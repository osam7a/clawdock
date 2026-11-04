using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ClaudeNotch
{
    // Claude's colors, light or dark, and the control styles built from them.
    class Palette
    {
        public bool Dark;
        public Brush Bg, Side, Card, Line, Text, Sub, Hover, Sel, Field, Clay, ClayHover, Off;
        public Color ScreenTop, ScreenBottom;
        public readonly FontFamily Serif = new FontFamily("Georgia, Cambria, Times New Roman");
        readonly Dictionary<string, string> hex = new Dictionary<string, string>();

        public static Palette Current()
        {
            bool dark = false;
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    if (key != null) dark = Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0;
            }
            catch { }
            return new Palette(dark);
        }

        Palette(bool dark)
        {
            Dark = dark;
            Bg = Make("BG", dark ? "#262624" : "#FAF9F5");
            Side = Make("SIDE", dark ? "#1F1E1D" : "#F0EEE6");
            Card = Make("CARD", dark ? "#30302E" : "#FFFFFF");
            Line = Make("LINE", dark ? "#3E3E3A" : "#E5E2D8");
            Text = Make("TEXT", dark ? "#F5F4EE" : "#141413");
            Sub = Make("SUB", dark ? "#A6A49B" : "#6F6E69");
            Hover = Make("HOVER", dark ? "#3A3A37" : "#ECE9E0");
            Sel = Make("SEL", dark ? "#34332F" : "#E6E2D7");
            Field = Make("FIELD", dark ? "#262624" : "#FFFFFF");
            Clay = Make("CLAY", "#D97757");
            ClayHover = Make("CLAYHOVER", dark ? "#E08A6D" : "#C6613F");
            Off = Make("OFF", dark ? "#5A5954" : "#CFCBC0");
            ScreenTop = (Color)ColorConverter.ConvertFromString(dark ? "#3A3936" : "#E9E5DA");
            ScreenBottom = (Color)ColorConverter.ConvertFromString(dark ? "#2C2B29" : "#DAD5C7");
        }

        Brush Make(string token, string color)
        {
            hex[token] = color;
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            b.Freeze();
            return b;
        }

        // Dark or light title bar in the sidebar's color (Windows 11; ignored elsewhere).
        public void ColorTitleBar(IntPtr hwnd)
        {
            try
            {
                int on = Dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, 20, ref on, 4);
                var c = ((SolidColorBrush)Side).Color;
                int caption = c.R | (c.G << 8) | (c.B << 16);
                DwmSetWindowAttribute(hwnd, 35, ref caption, 4);
                var t = ((SolidColorBrush)Text).Color;
                int text = t.R | (t.G << 8) | (t.B << 16);
                DwmSetWindowAttribute(hwnd, 36, ref text, 4);
            }
            catch { }
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public ResourceDictionary Styles()
        {
            string xaml = StylesXaml;
            foreach (var pair in hex) xaml = xaml.Replace("#" + pair.Key + "'", pair.Value + "'");
            return (ResourceDictionary)XamlReader.Parse(xaml);
        }

        const string StylesXaml = @"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
  <SolidColorBrush x:Key='Text' Color='#TEXT'/>
  <SolidColorBrush x:Key='Sub' Color='#SUB'/>
  <SolidColorBrush x:Key='Card' Color='#CARD'/>
  <SolidColorBrush x:Key='Line' Color='#LINE'/>
  <SolidColorBrush x:Key='Hover' Color='#HOVER'/>
  <SolidColorBrush x:Key='Sel' Color='#SEL'/>
  <SolidColorBrush x:Key='Field' Color='#FIELD'/>
  <SolidColorBrush x:Key='Clay' Color='#CLAY'/>
  <SolidColorBrush x:Key='ClayHover' Color='#CLAYHOVER'/>
  <SolidColorBrush x:Key='Off' Color='#OFF'/>

  <Style TargetType='Button'>
    <Setter Property='Foreground' Value='{StaticResource Text}'/>
    <Setter Property='Background' Value='{StaticResource Card}'/>
    <Setter Property='BorderBrush' Value='{StaticResource Line}'/>
    <Setter Property='Padding' Value='14,7'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Border x:Name='b' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='8' Padding='{TemplateBinding Padding}'>
          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='{StaticResource Hover}'/></Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='{StaticResource Clay}'/></Trigger>
          <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.5'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style x:Key='Primary' TargetType='Button'>
    <Setter Property='Foreground' Value='White'/>
    <Setter Property='FontWeight' Value='SemiBold'/>
    <Setter Property='Padding' Value='14,7'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Border x:Name='b' Background='{StaticResource Clay}' CornerRadius='8' Padding='{TemplateBinding Padding}' BorderThickness='1' BorderBrush='{StaticResource Clay}'>
          <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='{StaticResource ClayHover}'/><Setter TargetName='b' Property='BorderBrush' Value='{StaticResource ClayHover}'/></Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='{StaticResource Text}'/></Trigger>
          <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style x:Key='Link' TargetType='Button'>
    <Setter Property='Foreground' Value='{StaticResource Sub}'/>
    <Setter Property='FontSize' Value='12'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Button'>
        <Border x:Name='b' Background='Transparent' CornerRadius='6' Padding='6,5'><ContentPresenter/></Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter Property='Foreground' Value='{StaticResource Clay}'/></Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='b' Property='Background' Value='{StaticResource Hover}'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style x:Key='Nav' TargetType='RadioButton'>
    <Setter Property='Foreground' Value='{StaticResource Text}'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Margin' Value='0,1'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='RadioButton'>
        <Border x:Name='b' Background='Transparent' CornerRadius='8' Padding='10,8'><ContentPresenter/></Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='Background' Value='{StaticResource Hover}'/></Trigger>
          <Trigger Property='IsChecked' Value='True'><Setter TargetName='b' Property='Background' Value='{StaticResource Sel}'/><Setter Property='FontWeight' Value='SemiBold'/></Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='b' Property='Background' Value='{StaticResource Hover}'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style x:Key='Toggle' TargetType='CheckBox'>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='CheckBox'>
        <Border x:Name='track' Width='38' Height='22' CornerRadius='11' Background='{StaticResource Off}' BorderThickness='2' BorderBrush='Transparent'>
          <Ellipse x:Name='knob' Width='14' Height='14' Fill='White' HorizontalAlignment='Left' Margin='2,0,0,0'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsChecked' Value='True'>
            <Setter TargetName='track' Property='Background' Value='{StaticResource Clay}'/>
            <Setter TargetName='knob' Property='HorizontalAlignment' Value='Right'/>
            <Setter TargetName='knob' Property='Margin' Value='0,0,2,0'/>
          </Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='track' Property='BorderBrush' Value='{StaticResource Text}'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style TargetType='TextBox'>
    <Setter Property='Foreground' Value='{StaticResource Text}'/>
    <Setter Property='Background' Value='{StaticResource Field}'/>
    <Setter Property='BorderBrush' Value='{StaticResource Line}'/>
    <Setter Property='CaretBrush' Value='{StaticResource Text}'/>
    <Setter Property='SelectionBrush' Value='{StaticResource Clay}'/>
    <Setter Property='Height' Value='34'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='TextBox'>
        <Border x:Name='b' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='8'>
          <ScrollViewer x:Name='PART_ContentHost' VerticalAlignment='Center' Margin='8,0'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='{StaticResource Sub}'/></Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='{StaticResource Clay}'/></Trigger>
          <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style TargetType='PasswordBox'>
    <Setter Property='Foreground' Value='{StaticResource Text}'/>
    <Setter Property='Background' Value='{StaticResource Field}'/>
    <Setter Property='BorderBrush' Value='{StaticResource Line}'/>
    <Setter Property='CaretBrush' Value='{StaticResource Text}'/>
    <Setter Property='SelectionBrush' Value='{StaticResource Clay}'/>
    <Setter Property='Height' Value='34'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='PasswordBox'>
        <Border x:Name='b' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1' CornerRadius='8'>
          <ScrollViewer x:Name='PART_ContentHost' VerticalAlignment='Center' Margin='8,0'/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='{StaticResource Sub}'/></Trigger>
          <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='{StaticResource Clay}'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style TargetType='ComboBoxItem'>
    <Setter Property='Foreground' Value='{StaticResource Text}'/>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='ComboBoxItem'>
        <Border x:Name='b' Background='Transparent' CornerRadius='6' Padding='10,7'><ContentPresenter/></Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsHighlighted' Value='True'><Setter TargetName='b' Property='Background' Value='{StaticResource Hover}'/></Trigger>
          <Trigger Property='IsSelected' Value='True'><Setter Property='Foreground' Value='{StaticResource Clay}'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style TargetType='ComboBox'>
    <Setter Property='Foreground' Value='{StaticResource Text}'/>
    <Setter Property='Height' Value='34'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='ComboBox'>
        <Grid>
          <ToggleButton x:Name='tb' Focusable='False' ClickMode='Press' Cursor='Hand'
              IsChecked='{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}'>
            <ToggleButton.Template>
              <ControlTemplate TargetType='ToggleButton'>
                <Border x:Name='b' Background='{StaticResource Field}' BorderBrush='{StaticResource Line}' BorderThickness='1' CornerRadius='8'>
                  <TextBlock Text='&#xE70D;' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='10' Foreground='{StaticResource Sub}'
                      HorizontalAlignment='Right' VerticalAlignment='Center' Margin='0,0,12,0'/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='{StaticResource Sub}'/></Trigger>
                  <Trigger Property='IsChecked' Value='True'><Setter TargetName='b' Property='BorderBrush' Value='{StaticResource Clay}'/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate>
            </ToggleButton.Template>
          </ToggleButton>
          <ContentPresenter IsHitTestVisible='False' Content='{TemplateBinding SelectionBoxItem}' Margin='12,0,32,0'
              VerticalAlignment='Center' HorizontalAlignment='Left'/>
          <Border x:Name='ring' BorderThickness='1' BorderBrush='Transparent' CornerRadius='8' IsHitTestVisible='False'/>
          <Popup x:Name='PART_Popup' IsOpen='{TemplateBinding IsDropDownOpen}' Placement='Bottom' VerticalOffset='4'
              AllowsTransparency='True' Focusable='False' PopupAnimation='Fade'>
            <Border Background='{StaticResource Card}' BorderBrush='{StaticResource Line}' BorderThickness='1' CornerRadius='10' Padding='4'
                MinWidth='{TemplateBinding ActualWidth}' MaxHeight='320'>
              <ScrollViewer HorizontalScrollBarVisibility='Disabled' VerticalScrollBarVisibility='Auto'>
                <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Contained'/>
              </ScrollViewer>
            </Border>
          </Popup>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='ring' Property='BorderBrush' Value='{StaticResource Clay}'/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style TargetType='Slider'>
    <Setter Property='Cursor' Value='Hand'/>
    <Setter Property='FocusVisualStyle' Value='{x:Null}'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='Slider'>
        <Grid Height='22' Background='Transparent'>
          <Border Height='4' CornerRadius='2' Background='{StaticResource Off}' VerticalAlignment='Center' Margin='8,0'/>
          <Track x:Name='PART_Track'>
            <Track.DecreaseRepeatButton>
              <RepeatButton Command='Slider.DecreaseLarge' Focusable='False'>
                <RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Grid Background='Transparent'><Border Height='4' CornerRadius='2' Background='{StaticResource Clay}' Margin='8,0,0,0'/></Grid></ControlTemplate></RepeatButton.Template>
              </RepeatButton>
            </Track.DecreaseRepeatButton>
            <Track.IncreaseRepeatButton>
              <RepeatButton Command='Slider.IncreaseLarge' Focusable='False'>
                <RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template>
              </RepeatButton>
            </Track.IncreaseRepeatButton>
            <Track.Thumb>
              <Thumb>
                <Thumb.Template><ControlTemplate TargetType='Thumb'><Ellipse x:Name='e' Width='18' Height='18' Fill='White' Stroke='{StaticResource Clay}' StrokeThickness='4'/></ControlTemplate></Thumb.Template>
              </Thumb>
            </Track.Thumb>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>

  <Style TargetType='ScrollBar'>
    <Setter Property='Width' Value='10'/>
    <Setter Property='MinWidth' Value='10'/>
    <Setter Property='Template'><Setter.Value>
      <ControlTemplate TargetType='ScrollBar'>
        <Track x:Name='PART_Track' IsDirectionReversed='True'>
          <Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageUpCommand' Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>
          <Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageDownCommand' Focusable='False'><RepeatButton.Template><ControlTemplate TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>
          <Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType='Thumb'><Border CornerRadius='3' Background='{StaticResource Off}' Margin='2'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
        </Track>
      </ControlTemplate>
    </Setter.Value></Setter>
  </Style>
</ResourceDictionary>";
    }
}
