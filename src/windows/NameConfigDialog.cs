using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LiveCaptionsTranslator
{
    /// <summary>
    /// Small modal prompt that asks the user to name a configuration slot.
    /// Built in code so it needs no extra XAML file.
    /// </summary>
    internal static class NameConfigDialog
    {
        public static string? Ask(Window owner, string title, string initialName)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 380,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Background = SystemColors.WindowBrush,
            };

            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new TextBlock
            {
                Text = "给这个配置起个名字，方便之后从下拉菜单切换。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });

            var box = new TextBox
            {
                Text = initialName ?? "",
                MinWidth = 340,
                Padding = new Thickness(8, 5, 8, 5),
            };
            panel.Children.Add(box);

            var hint = new TextBlock
            {
                Text = "例如：课堂常用、备用接口、公司内网",
                Opacity = 0.6,
                Margin = new Thickness(0, 6, 0, 0),
            };
            panel.Children.Add(hint);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };

            var okButton = new Button { Content = "确定", Width = 76, Height = 28, IsDefault = true };
            var cancelButton = new Button { Content = "取消", Width = 76, Height = 28, IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
            buttons.Children.Add(okButton);
            buttons.Children.Add(cancelButton);
            panel.Children.Add(buttons);

            dialog.Content = panel;

            bool? result = null;
            okButton.Click += (s, e) => { result = true; dialog.Close(); };
            dialog.Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
            // Enter confirms, Escape cancels.
            dialog.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    result = true;
                    dialog.Close();
                    e.Handled = true;
                }
            };

            if (owner != null && owner.IsLoaded)
                dialog.Owner = owner;
            dialog.ShowDialog();

            if (result != true)
                return null;
            return box.Text.Trim();
        }
    }
}
