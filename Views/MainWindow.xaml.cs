using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameTrainer.ViewModels;

namespace GameTrainer.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (sender is TextBox tb)
            {
                var be = tb.GetBindingExpression(TextBox.TextProperty);
                be?.UpdateSource();
            }
            if (DataContext is MainViewModel vm)
            {
                vm.TriggerImmediateSearch();
            }
        }
    }
}
