using Baballonia.Helpers;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Baballonia.Contracts;

public interface IVROverlay : IDisposable
{
    public Task<(bool success, string status)> EyeTrackingCalibrationRequested(CalibrationRoutine.Routines calibrationRoutine, List<string> args);
}
