using System.Windows;
using System.Windows.Controls;
using WixSharp.UI.WPF;

namespace AlliePack
{
    // CustomDialogBase.Init() is sealed, so setup happens via the Loaded event.
    // WixSharp calls Init() first (which sets ManagedFormHost), then adds the dialog
    // to the WPF tree, at which point Loaded fires and ManagedFormHost is valid.
    internal class AddToPathDialog : CustomDialogBase
    {
        public AddToPathDialog()
        {
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            bool addToPath = ReadSession() != "0";

            var panel = new StackPanel { Margin = new Thickness(38, 20, 38, 0) };

            panel.Children.Add(new TextBlock
            {
                Text = "Installation Options",
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10),
            });

            panel.Children.Add(new TextBlock
            {
                Text = "Choose additional options for this installation.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 15),
            });

            var checkBox = new CheckBox
            {
                Content = "Add to PATH (makes the command available from any terminal)",
                IsChecked = addToPath,
            };

            // Write to session immediately on change so the value is always current,
            // regardless of when GoNext fires relative to our handler.
            checkBox.Checked   += (s, _) => WriteSession("1");
            checkBox.Unchecked += (s, _) => WriteSession("0");

            panel.Children.Add(checkBox);
            SetUserContent(panel);
        }

        private string ReadSession()
        {
            try { return ((WpfDialogHost)ManagedFormHost).MsiRuntime.MsiSession["ADDTOPATH"] ?? "1"; }
            catch { return "1"; }
        }

        private void WriteSession(string value)
        {
            try { ((WpfDialogHost)ManagedFormHost).MsiRuntime.MsiSession["ADDTOPATH"] = value; }
            catch { }
        }
    }
}
