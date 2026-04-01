using Baballonia.CaptureBin.IO;
using Baballonia.Contracts;
using Baballonia.Services;
using Baballonia.Services.events;
using OpenCvSharp;
using OverlaySDK;
using OverlaySDK.Packets;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Baballonia.Desktop.Calibration;

public interface ICalibrationStep
{
    string Name { get; }
    Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct);
}

public sealed class BaseTutorialStep(string name, TimeSpan time) : PacketHandlerAdapter, ICalibrationStep
{
    public string Name { get; } = name;
    public TimeSpan TimeToRun { get; } = time;
    private TaskCompletionSource Token = new();

    public BaseTutorialStep(string name) : this(name, TimeSpan.FromSeconds(30))
    {
    }

    public async Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct)
    {
        dispatcher.RegisterHandler(this);

        dispatcher.Dispatch(new RunVariableLenghtRoutinePacket(Name, TimeToRun));
        await WaitForRoutineFinishAsync(ct);

        dispatcher.UnRegisterHandler(this);
    }

    private async Task WaitForRoutineFinishAsync(CancellationToken ct)
    {
        await Token.Task.WaitAsync(ct);
    }

    public override void OnRoutineFinishedPacket(RoutineFinishedPacket packet)
    {
        Token.SetResult();
    }
}

public abstract class PositionalAwareCaptureStep(string name, uint flags, TimeSpan time)
    : PacketHandlerAdapter, ICalibrationStep
{
    public string Name { get; } = name;
    public uint Flags { get; } = flags;

    protected PositionalBinCollector PositionalBinCollector = new(flags);
    protected TaskCompletionSource Token = new();
    protected bool ShouldCollect = false;
    protected TimeSpan TimeToTun = time;

    public abstract Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct);

    public override void OnHmdPositionalData(HmdPositionalDataPacket positionalData)
    {
        if (!ShouldCollect)
            return;
        PositionalBinCollector.UpdatePositionalData(positionalData);
    }

    public virtual void OnNewEyeFrame(EyePipelineEvents.NewTransformedFrameEvent frame)
    {
        if (!ShouldCollect)
            return;

        var images = frame.image.Split();
        PositionalBinCollector.AddFrame(images[1], images[0]);
    }

    protected void StartCollecting()
    {
        ShouldCollect = true;
    }

    protected void StopCollecting()
    {
        ShouldCollect = false;
    }

    protected async Task WaitForRoutineFinishAsync(CancellationToken ct)
    {
        await Token.Task.WaitAsync(ct);
    }

    public override void OnRoutineFinishedPacket(RoutineFinishedPacket packet)
    {
        Token.SetResult();
    }

    public void Dispose()
    {
        Token.SetCanceled();
    }
}

public abstract class BaseCaptureStep(string name, uint flags, TimeSpan time) : PacketHandlerAdapter, ICalibrationStep
{
    public string Name { get; } = name;
    public uint Flags { get; } = flags;

    protected BinCollector BinCollector = new(flags);
    protected TaskCompletionSource Token = new();
    protected bool ShouldCollect = false;
    protected TimeSpan TimeToTun = time;

    public abstract Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct);

    public virtual void OnNewEyeFrame(EyePipelineEvents.NewTransformedFrameEvent frame)
    {
        if (!ShouldCollect)
            return;

        var images = frame.image.Split();
        AddFrame(images);
    }

    public virtual Frame AddFrame(Mat[] images)
    {
        return BinCollector.AddFrame(images[1], images[0]);
    }

    protected void StartCollecting()
    {
        ShouldCollect = true;
    }

    protected void StopCollecting()
    {
        ShouldCollect = false;
    }

    protected async Task WaitForRoutineFinishAsync(CancellationToken ct)
    {
        await Token.Task.WaitAsync(ct);
    }

    public override void OnRoutineFinishedPacket(RoutineFinishedPacket packet)
    {
        Token.SetResult();
    }

    public void Dispose()
    {
        Token.SetCanceled();
    }
}

public class GazeCaptureStep(IEyePipelineEventBus bus, TimeSpan time, string fileName) : BasePositionalAwareEyeCaptureStep(bus, "gaze", fileName,
    CaptureFlags.FLAG_GOOD_DATA |
    CaptureFlags.FLAG_IN_MOVEMENT |
    CaptureFlags.FLAG_VERSION_BIT1 |
    CaptureFlags.FLAG_ROUTINE_BIT1, time)
{
    private Stopwatch _posDataTimer = new();
    private readonly TimeSpan _posDataTimeout = TimeSpan.FromSeconds(0.2);

    public GazeCaptureStep(IEyePipelineEventBus bus, string fileName) : this(bus, TimeSpan.FromSeconds(120), fileName)
    {
    }

    public override void OnHmdPositionalData(HmdPositionalDataPacket positionalData)
    {
        if (!ShouldCollect)
            return;

        PositionalBinCollector.UpdatePositionalData(positionalData);
        _posDataTimer.Restart();
    }

    public override void OnNewEyeFrame(EyePipelineEvents.NewTransformedFrameEvent frame)
    {
        if (!ShouldCollect)
            return;
        if (_posDataTimer.Elapsed <= _posDataTimeout)
        {
            var images = frame.image.Split();
            var f = PositionalBinCollector.AddFrame(images[1], images[0]);
            if (f is not null)
            {
                f.Header = f.Header with
                {
                    RoutineLeftLid = 1,
                    RoutineRightLid = 1,
                };
            }
        }
    }
}

public class BasePositionalAwareEyeCaptureStep(
    IEyePipelineEventBus eyePipelineEvent,
    string name,
    string fileName,
    uint flags,
    TimeSpan time)
    : PositionalAwareCaptureStep(name, flags, time)
{
    public override async Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct)
    {
        dispatcher.RegisterHandler(this);

        eyePipelineEvent.Subscribe<EyePipelineEvents.NewTransformedFrameEvent>(OnNewEyeFrame);

        dispatcher.Dispatch(new RunVariableLenghtRoutinePacket(Name, TimeToTun));
        StartCollecting();
        await WaitForRoutineFinishAsync(ct);

        eyePipelineEvent.Unsubscribe<EyePipelineEvents.NewTransformedFrameEvent>(OnNewEyeFrame);
        dispatcher.UnRegisterHandler(this);

        if (ct.IsCancellationRequested)
            return;

        PositionalBinCollector.WriteBin(fileName);
    }
}

public class BaseEyeCaptureStep(
    IEyePipelineEventBus eyePipelineEvent,
    string name,
    string fileName,
    uint flags,
    TimeSpan time,
    float lid = 0,
    float browRaise = 0,
    float browAngry = 0,
    float widen = 0,
    float squint = 0,
    float dilate = 0)
    : BaseCaptureStep(name, flags, time)
{
    public override async Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct)
    {
        dispatcher.RegisterHandler(this);

        eyePipelineEvent.Subscribe<EyePipelineEvents.NewTransformedFrameEvent>(OnNewEyeFrame);

        dispatcher.Dispatch(new RunVariableLenghtRoutinePacket(Name, TimeToTun));
        StartCollecting();
        await WaitForRoutineFinishAsync(ct);

        eyePipelineEvent.Unsubscribe<EyePipelineEvents.NewTransformedFrameEvent>(OnNewEyeFrame);
        dispatcher.UnRegisterHandler(this);

        if (ct.IsCancellationRequested)
            return;

        BinCollector.WriteBin(fileName);
    }

    public override Frame AddFrame(Mat[] images)
    {
        var frame = base.AddFrame(images);
        frame.Header = frame.Header with
        {
            RoutineLeftLid = lid,
            RoutineRightLid = lid,
            RoutineBrowRaise = browRaise,
            RoutineBrowAngry = browAngry,
            RoutineWiden = widen,
            RoutineSquint = squint,
            RoutineDilate = dilate,
        };
        return frame;
    }
}

public class CommandDispatchStep(string name) : ICalibrationStep
{
    public string Name { get; } = name;

    public Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct)
    {
        dispatcher.Dispatch(new RunFixedLenghtRoutinePacket(Name));
        return Task.CompletedTask;
    }
}

public class TrainerCalibrationStep(ITrainerService overlayTrainer) : ICalibrationStep
{
    public string Name => "trainer";

    public async Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct)
    {
        dispatcher.Dispatch(new RunVariableLenghtRoutinePacket(Name, TimeSpan.FromSeconds(120)));
        var onProgressHandler = (TrainerProgressReportPacket packet) => { dispatcher.Dispatch(packet); };
        overlayTrainer.OnProgress += onProgressHandler;
        await overlayTrainer.RunTraining(Path.Combine(Utils.ModelDataDirectory, "user_cal.bin"),
            Path.Combine(Utils.ModelDataDirectory, "tuned_temporal_eye_tracking_latest"));
        await overlayTrainer.WaitAsync();

        overlayTrainer.OnProgress -= onProgressHandler;
    }
}

public class EyeCaptureStepFactory(IEyePipelineEventBus eyePipelineEvent)
{
    public BaseEyeCaptureStep Create(string name, string fileName, uint flags, TimeSpan time,
        float lid = 0,
        float browRaise = 0,
        float browAngry = 0,
        float widen = 0,
        float squint = 0,
        float dilate = 0) =>
        new(eyePipelineEvent, name, fileName, flags, time, lid, browRaise, browAngry, widen, squint, dilate);
}

public class MergeBinsStep(params string[] binNames) : ICalibrationStep
{
    public string Name => "bin_merger";

    public Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct)
    {
        MergeBins("user_cal.bin", binNames);
        return Task.CompletedTask;
    }

    private static void MergeBins(string result, params string[] inputs)
    {
        var resultPath = Path.Combine(Utils.ModelDataDirectory, result);
        CaptureBin.IO.CaptureBin.Concatenate(resultPath, inputs);
    }
}

public class EyeCalibration(
    EyeCaptureStepFactory eyeCaptureStepFactory,
    ITrainerService trainer,
    IEyePipelineEventBus eyePipelineEventBus)
{
    public IEnumerable<ICalibrationStep> GetCalibrationStep(string stepName, string fileName)
    {
        return stepName switch
        {
            "gaze" => new List<ICalibrationStep>
            {
                new BaseTutorialStep("gazetutorial"),
                new GazeCaptureStep(eyePipelineEventBus, fileName),
                new CommandDispatchStep("close")
            },
            "blink" => new List<ICalibrationStep>
            {
                new BaseTutorialStep("blinktutorial", TimeSpan.FromSeconds(10)),
                eyeCaptureStepFactory.Create(stepName, fileName,
                    CaptureFlags.FLAG_GOOD_DATA |
                    CaptureFlags.FLAG_IN_MOVEMENT |
                    CaptureFlags.FLAG_VERSION_BIT1,
                    TimeSpan.FromSeconds(20)
                ),
                new CommandDispatchStep("close")
            },
            "widen" => new List<ICalibrationStep>
            {
                new BaseTutorialStep("widentutorial", TimeSpan.FromSeconds(10)),
                eyeCaptureStepFactory.Create(stepName,fileName,
                    CaptureFlags.FLAG_GOOD_DATA | CaptureFlags.FLAG_VERSION_BIT1, TimeSpan.FromSeconds(20), widen: 1, lid: 1),
                new CommandDispatchStep("close")
            },
            "squint" => new List<ICalibrationStep>
            {
                new BaseTutorialStep("squinttutorial", TimeSpan.FromSeconds(10)),
                eyeCaptureStepFactory.Create(stepName,fileName,
                    CaptureFlags.FLAG_GOOD_DATA | CaptureFlags.FLAG_VERSION_BIT1, TimeSpan.FromSeconds(20), squint: 1, lid: 1),
                new CommandDispatchStep("close")
            },
            "brow" => new List<ICalibrationStep>
            {
                new BaseTutorialStep("browtutorial", TimeSpan.FromSeconds(10)),
                eyeCaptureStepFactory.Create(stepName, fileName,
                    CaptureFlags.FLAG_GOOD_DATA | CaptureFlags.FLAG_VERSION_BIT1, TimeSpan.FromSeconds(20), browAngry: 1, lid: 1),
                new CommandDispatchStep("close")
            }
        };
    }

    public IEnumerable<ICalibrationStep> TrainCalibration(List<string> args)
    {
        return new List<ICalibrationStep>
        {
            new MergeBinsStep(args.ToArray()),
            new TrainerCalibrationStep(trainer),
            new CommandDispatchStep("close")
        };
    }

    public IEnumerable<ICalibrationStep> BasicAllCalibration()
    {
        List<ICalibrationStep> steps =
        [
            new BaseTutorialStep("gazetutorial"),
            new GazeCaptureStep(eyePipelineEventBus, Path.Combine(Utils.ModelDataDirectory, "gaze.bin")),
            new BaseTutorialStep("blinktutorial", TimeSpan.FromSeconds(10)),
            eyeCaptureStepFactory.Create("blink", Path.Combine(Utils.ModelDataDirectory, "blink.bin"),
                CaptureFlags.FLAG_GOOD_DATA |
                CaptureFlags.FLAG_IN_MOVEMENT |
                CaptureFlags.FLAG_VERSION_BIT1,
                TimeSpan.FromSeconds(20), lid: 0
            ),

             new BaseTutorialStep("widentutorial", TimeSpan.FromSeconds(10)),
             eyeCaptureStepFactory.Create("widen", Path.Combine(Utils.ModelDataDirectory, "widen.bin"),
                 CaptureFlags.FLAG_GOOD_DATA | CaptureFlags.FLAG_VERSION_BIT1, TimeSpan.FromSeconds(20), widen: 1, lid: 1),
             
             new BaseTutorialStep("squinttutorial", TimeSpan.FromSeconds(10)),
             eyeCaptureStepFactory.Create("squint", Path.Combine(Utils.ModelDataDirectory, "squint.bin"),
                 CaptureFlags.FLAG_GOOD_DATA | CaptureFlags.FLAG_VERSION_BIT1, TimeSpan.FromSeconds(20), squint: 1, lid: 1),
             
             new BaseTutorialStep("browtutorial", TimeSpan.FromSeconds(10)),
             eyeCaptureStepFactory.Create("brow", Path.Combine(Utils.ModelDataDirectory, "brow.bin"),
                 CaptureFlags.FLAG_GOOD_DATA | CaptureFlags.FLAG_VERSION_BIT1, TimeSpan.FromSeconds(20), browAngry: 1, lid: 1),
            // steps.Add(new BaseTutorialStep("covergencetutorial"));
            // steps.Add(_eyeCaptureStepFactory.Create("covergence",
            //     CaptureFlags.FLAG_GOOD_DATA | CaptureFlags.FLAG_WHATEVER_NOT_IMPLEMENTED));

            new MergeBinsStep(Path.Combine(Utils.ModelDataDirectory, "gaze.bin"), Path.Combine(Utils.ModelDataDirectory, "blink.bin"), Path.Combine(Utils.ModelDataDirectory, "widen.bin"), Path.Combine(Utils.ModelDataDirectory, "squint.bin"), Path.Combine(Utils.ModelDataDirectory, "brow.bin")),
            //new MergeBinsStep(Path.Combine(Utils.ModelDataDirectory, "gaze.bin"), Path.Combine(Utils.ModelDataDirectory, "blink.bin")),
            new TrainerCalibrationStep(trainer),
            new CommandDispatchStep("close")

        ];

        return steps;
    }

    public IEnumerable<ICalibrationStep> BasicAllCalibrationQuick()
    {
        List<ICalibrationStep> steps =
        [
            new BaseTutorialStep("gazetutorialshort", TimeSpan.FromSeconds(5)),
            new GazeCaptureStep(eyePipelineEventBus, Path.Combine(Utils.ModelDataDirectory, "gaze.bin")),
            new BaseTutorialStep("blinktutorial", TimeSpan.FromSeconds(4)),
            eyeCaptureStepFactory.Create("blink", Path.Combine(Utils.ModelDataDirectory, "blink.bin"),
                CaptureFlags.FLAG_GOOD_DATA |
                CaptureFlags.FLAG_IN_MOVEMENT |
                CaptureFlags.FLAG_VERSION_BIT1 |
                CaptureFlags.FLAG_ROUTINE_BIT1,
                TimeSpan.FromSeconds(20)
            ),

            new BaseTutorialStep("widentutorial", TimeSpan.FromSeconds(4)),
            eyeCaptureStepFactory.Create("widen", Path.Combine(Utils.ModelDataDirectory, "widen.bin"),
                CaptureFlags.FLAG_GOOD_DATA | CaptureFlags.FLAG_VERSION_BIT1, TimeSpan.FromSeconds(20)),
            
            new BaseTutorialStep("squinttutorial", TimeSpan.FromSeconds(4)),
            eyeCaptureStepFactory.Create("squint", Path.Combine(Utils.ModelDataDirectory, "squint.bin"),
                CaptureFlags.FLAG_GOOD_DATA | CaptureFlags.FLAG_VERSION_BIT1, TimeSpan.FromSeconds(20)),
            
            new BaseTutorialStep("browtutorial", TimeSpan.FromSeconds(4)),
            eyeCaptureStepFactory.Create("brow", Path.Combine(Utils.ModelDataDirectory, "brow.bin"),
                CaptureFlags.FLAG_GOOD_DATA | CaptureFlags.FLAG_VERSION_BIT1, TimeSpan.FromSeconds(20)),

            new MergeBinsStep(Path.Combine(Utils.ModelDataDirectory, "gaze.bin"), Path.Combine(Utils.ModelDataDirectory, "blink.bin"), Path.Combine(Utils.ModelDataDirectory, "widen.bin"), Path.Combine(Utils.ModelDataDirectory, "squint.bin"), Path.Combine(Utils.ModelDataDirectory, "brow.bin")),
            //new MergeBinsStep(Path.Combine(Utils.ModelDataDirectory, "gaze.bin"), Path.Combine(Utils.ModelDataDirectory, "blink.bin")),
            new TrainerCalibrationStep(trainer),
            new CommandDispatchStep("close")

        ];

        return steps;
    }
}
