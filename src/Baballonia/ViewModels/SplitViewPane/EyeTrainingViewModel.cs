using Avalonia.Controls;
using Avalonia.Media;
using Baballonia.Contracts;
using Baballonia.Helpers;
using Baballonia.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Baballonia.ViewModels.SplitViewPane;

public partial class EyeTrainingViewModel : ViewModelBase
{
    public partial class CalibrationStep : ObservableObject
    {
        [ObservableProperty] private string _title;
        [ObservableProperty] ObservableCollection<string> _files;
        [ObservableProperty] string _selectedFile;

        private readonly string _stepName;
        private readonly IVROverlay _vrOverlay;

        public CalibrationStep(IVROverlay vrOverlay, string title, string name)
        {
            _title = title;
            _stepName = name;
            _files = new ObservableCollection<string>();
            _vrOverlay = vrOverlay;

            GetExistingFiles();

            if (_files.Count > 0)
                _selectedFile = _files[0];
        }

        public void GetExistingFiles()
        {
            _files.Clear();
            var calibrationFolder = Path.Combine(Utils.ModelTrainingDataDirectory, _stepName);
            if (!Directory.Exists(calibrationFolder))
            {
                return;
            }

            foreach (var file in Directory.GetFiles(calibrationFolder, "*.bin", SearchOption.AllDirectories))
            {
                _files.Add(file);
            }
        }

        [RelayCommand]
        private async Task ReRecord()
        {
            var res = await Task.Run(async () =>
                {
                    try
                    {
                        return await _vrOverlay.EyeTrackingCalibrationRequested(CalibrationRoutine.Routines.TutorialStep, new List<string>
                        {
                            _stepName,
                            Path.Combine(Utils.ModelTrainingDataDirectory, _stepName, $"{DateTime.Now:yyyyMMdd_HHmmss}.bin")
                        });
                    }
                    catch (Exception ex)
                    {
                        return (false, ex.Message);
                    }
                }
            );

            GetExistingFiles();
            //if (res.Item1)
            //{
            //    SelectedCalibrationTextBlock.Foreground = new SolidColorBrush(Colors.Green);
            //}
            //else
            //{
            //    SelectedCalibrationTextBlock.Foreground = new SolidColorBrush(Colors.Red);
            //    _logger.LogError(res.Item2);
            //}
        }
    }

    public ObservableCollection<CalibrationStep> CalibrationSteps { get; set; }

    private readonly IVROverlay _vrOverlay;
    private readonly ILogger<EyeTrainingViewModel> _logger;
    private readonly EyePipelineEventBus _eyePipelineEventBus;
    
    public Button RetrainButton { get; set; }

    public EyeTrainingViewModel(
        IVROverlay vrOverlay,
        ILogger<EyeTrainingViewModel> logger,
        EyePipelineEventBus eyePipelineEventBus)
    {
        _vrOverlay = vrOverlay;
        _logger = logger;
        _eyePipelineEventBus = eyePipelineEventBus;

        CalibrationSteps = new ObservableCollection<CalibrationStep>
        {
            new CalibrationStep(_vrOverlay, "Gaze", "gaze"),
            new CalibrationStep(_vrOverlay, "Blink", "blink"),
            new CalibrationStep(_vrOverlay, "Eyebrows", "brow"),
            new CalibrationStep(_vrOverlay, "Squinting", "squint"),
            new CalibrationStep(_vrOverlay, "Widening", "widen"),
        };
    }


    [RelayCommand]
    private async Task RetrainModel()
    {

        var paths = CalibrationSteps.Select(cs => cs.SelectedFile).ToList();

        var res = await Task.Run(async () =>
            {
                try
                {
                    return await _vrOverlay.EyeTrackingCalibrationRequested(CalibrationRoutine.Routines.TrainModel, paths);
                }
                catch (Exception ex)
                {
                    return (false, ex.Message);
                }
            }
        );
        if (res.Item1)
        {
            RetrainButton.Foreground = new SolidColorBrush(Colors.Green);
        }
        else
        {
            RetrainButton.Foreground = new SolidColorBrush(Colors.Red);
            _logger.LogError(res.Item2);
        }
    }

}
