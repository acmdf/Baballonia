using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Baballonia.Contracts;
using babble_model.Net.Sys;
using Microsoft.Extensions.Logging;
using OverlaySDK.Packets;

namespace Baballonia.Desktop.Calibration;

public partial class RustTrainerService : ITrainerService
{
    private readonly object _lock = new();

    public event Action<TrainerProgressReportPacket>? OnProgress;

    static event Action<TrainerProgressReportPacket>? GlobalProgress;
    static TaskCompletionSource<bool>? tcs;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void HandleProgress(TrainingDataCallback data)
    {
        Console.WriteLine($"Recieved {data.callback_type}: {data.low}/{data.high} ({data.loss})");
        TrainerProgressReportPacket progress;
        if (data.callback_type == CallbackType.Batch)
        {
            progress = new TrainerProgressReportPacket("Batch", data.low, data.high, data.loss);
        } else if (data.callback_type == CallbackType.Epoch)
        {
            progress = new TrainerProgressReportPacket("Epoch", data.low, data.high, data.loss);
        }
        else
        {
            if (tcs != null)
            {
                tcs.TrySetResult(true);
            } else
            {
                Console.WriteLine("tcs is null when trying to set result");
            }
            return;
        }

        GlobalProgress?.Invoke(progress);
    }

    unsafe void CallTrainer(string usercalbinPath, string outputfilePath)
    {
        var userCalBytes = System.Text.Encoding.UTF8.GetBytes(usercalbinPath);
        fixed (byte* userCalBytesPtr = userCalBytes)
        {
            var outputFileBytes = System.Text.Encoding.UTF8.GetBytes(outputfilePath);
            fixed (byte* outputFileBytesPtr = outputFileBytes)
            {
                NativeMethods.trainModel(userCalBytesPtr, outputFileBytesPtr, &HandleProgress);
            }
        }
    }

    public async Task RunTraining(string usercalbinPath, string outputfilePath)
    {
        if (!File.Exists(usercalbinPath))
            throw new FileNotFoundException(usercalbinPath + " not found");


        lock (_lock)
        {
            tcs = new TaskCompletionSource<bool>();
            GlobalProgress += OnProgress;
        }
        await Task.Run(() => CallTrainer(usercalbinPath, outputfilePath));
    }

    public Task WaitAsync()
    {
        return tcs != null ? tcs.Task : Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_lock)
        {
        }
    }
}
