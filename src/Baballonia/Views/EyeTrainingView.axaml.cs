using Avalonia.Controls;
using Avalonia.Interactivity;
using Baballonia.ViewModels.SplitViewPane;

namespace Baballonia.Views;

public partial class EyeTrainingView : ViewBase
{
    public EyeTrainingView()
    {
        InitializeComponent();
        
        Loaded += (_, _) =>
        {
            if (DataContext is not EyeTrainingViewModel vm) return;

            vm.RetrainButton = this.Find<Button>("RetrainButton")!;
        };
    }


    private async void RefreshFiles(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EyeTrainingViewModel vm) return;
        RefreshFilesText.IsEnabled = false;

        for (int i = 0; i < vm.CalibrationSteps.Count; i++)
        {
            vm.CalibrationSteps[i].GetExistingFiles();
        }

        RefreshFilesText.IsEnabled = true;
    }
}
