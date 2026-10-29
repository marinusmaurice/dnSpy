using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WpfEvents {
	public partial class MainWindow : Window {
		public MainWindow() => InitializeComponent();

		void Window_Loaded(object sender, RoutedEventArgs e) { }
		void OkButton_Click(object sender, RoutedEventArgs e) { }
		void OkButton_MouseEnter(object sender, MouseEventArgs e) { }
		void Input_TextChanged(object sender, TextChangedEventArgs e) { }
		void ItemButton_Click(object sender, RoutedEventArgs e) { }
		void StyledButton_Click(object sender, RoutedEventArgs e) { }
		void Border_MouseDown(object sender, MouseButtonEventArgs e) { }
	}
}
