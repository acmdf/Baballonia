using System;
using Baballonia.Services.events;
using Baballonia.Services.Inference.Enums;
using babble_model.Net.Sys;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace Baballonia.Services.Inference;

public class EyeProcessingPipeline : DefaultProcessingPipeline, IDisposable
{
    private readonly ILogger<EyeProcessingPipeline> _logger;
    private readonly IEyePipelineEventBus _eyePipelineEventBus;
    private readonly FastCorruptionDetector.FastCorruptionDetector _fastCorruptionDetector = new();
    private readonly ImageCollector _imageCollector = new();
    private bool UseRustPipeline = false;

    public EyeProcessingPipeline(ILogger<EyeProcessingPipeline> logger, IEyePipelineEventBus eyePipelineEventBus)
    {
        _logger = logger;
        _eyePipelineEventBus = eyePipelineEventBus;
    }

    public bool StabilizeEyes { get; set; } = true;

    public unsafe string? LoadInference(string modelPath)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(modelPath);
        fixed (byte* ptr = bytes)
        {
            var output = NativeMethods.loadModel(ptr);

            if (output.is_error) {
                var errorMsg = new string((sbyte*)output.value.error_message);

                NativeMethods.freeModelOutputResult(output);

                return errorMsg;
            }

            UseRustPipeline = true;

            return null;
        }
    }

    unsafe float[]? RunInference(Mat collected)
    {
        var res = NativeMethods.infer(collected.ExtractChannel(0).DataPointer, collected.ExtractChannel(1).DataPointer);

        if (res.is_error)
        {
            string errorMsg = new String((sbyte*)res.value.error_message);
            Console.WriteLine($"Inference error: {errorMsg}");
            NativeMethods.freeModelOutputResult(res);
            return null;
        }

        var output = res.value.model_output;

        float[] inferenceResult = { output.pitch_l, output.yaw_l, output.blink_l, output.eyebrow_l, output.eyewide_l, output.pitch_r, output.yaw_r, output.blink_r, output.eyebrow_r, output.eyewide_r };

        NativeMethods.freeModelOutputResult(res);

        return inferenceResult;
    }

    public float[]? RunUpdate()
    {
        var frame = VideoSource?.GetFrame(ColorType.Gray8);
        if(frame == null)
            return null;

        if (_fastCorruptionDetector.IsCorrupted(frame).isCorrupted)
            return null;

        _eyePipelineEventBus.Publish(new EyePipelineEvents.NewFrameEvent(frame));

        var transformed = ImageTransformer?.Apply(frame);
        if(transformed == null)
            return null;

        _eyePipelineEventBus.Publish(new EyePipelineEvents.NewTransformedFrameEvent(transformed));

        var collected = _imageCollector.Apply(transformed);
        transformed.Dispose();
        if (collected == null)
            return null;

        float[]? inferenceResult;

        if (UseRustPipeline)
        {
            inferenceResult = RunInference(collected);
        }
        else
        {
            if (InferenceService == null)
                return null;

            ImageConverter?.Convert(collected, InferenceService.GetInputTensor());

            inferenceResult = InferenceService?.Run();
        }

        if (inferenceResult == null)
            return null;

        if (inferenceResult.Length == 6)
        {
            // Older model with only look and blink, without eyebrow and eye wide.
            // We need to convert it to the new format by adding default values for the missing expressions.
            inferenceResult = new float[]
            {
                inferenceResult[0], // left pitch
                inferenceResult[1], // left yaw
                inferenceResult[2], // left lid
                0,                   // left eyebrow (default value)
                0,                   // left eye wide (default value)
                inferenceResult[3], // right pitch
                inferenceResult[4], // right yaw
                inferenceResult[5], // right lid
                0,                   // right eyebrow (default value)
                0                    // right eye wide (default value)
            };
        }

        if (Filter != null)
        {
            inferenceResult = Filter.Filter(inferenceResult);
        }

        ProcessExpressions(ref inferenceResult);

        _eyePipelineEventBus.Publish(new EyePipelineEvents.NewFilteredResultEvent(inferenceResult));

        frame.Dispose();
        transformed.Dispose();

        return inferenceResult;
    }

    private bool ProcessExpressions(ref float[] arKitExpressions)
    {
        if (arKitExpressions.Length < Utils.EyeRawExpressions)
            return false;

        const float mulV = 2.0f;
        const float mulY = 2.0f;

        var leftPitch = arKitExpressions[0] * mulY - mulY / 2;
        var leftYaw = arKitExpressions[1] * mulV - mulV / 2;
        var leftLid = 1 - arKitExpressions[2];
        var leftEyebrow = arKitExpressions[3];
        var leftEyewide = arKitExpressions[4];

        var rightPitch = arKitExpressions[5] * mulY - mulY / 2;
        var rightYaw = arKitExpressions[6] * mulV - mulV / 2;
        var rightLid = 1 - arKitExpressions[7];
        var rightEyebrow = arKitExpressions[8];
        var rightEyewide = arKitExpressions[9];

        var eyeY = (leftPitch * leftLid + rightPitch * rightLid) / (leftLid + rightLid);

        var leftEyeYawCorrected = rightYaw * (1 - leftLid) + leftYaw * leftLid;
        var rightEyeYawCorrected = leftYaw * (1 - rightLid) + rightYaw * rightLid;

        if (StabilizeEyes)
        {
            var rawConvergence = (rightEyeYawCorrected - leftEyeYawCorrected) / 2.0f;
            var convergence = Math.Max(rawConvergence, 0.0f); // We clamp the value here to avoid accidental divergence, as the model sometimes decides that's a thing

            var averagedYaw = (rightEyeYawCorrected + leftEyeYawCorrected) / 2.0f;

            leftEyeYawCorrected = averagedYaw - convergence;
            rightEyeYawCorrected = averagedYaw + convergence;
        }

        // [left pitch, left yaw, left lid...
        float[] convertedExpressions = new float[Utils.EyeRawExpressions];

        convertedExpressions[0] = rightEyeYawCorrected; // left pitch
        convertedExpressions[1] = eyeY;                   // left yaw
        convertedExpressions[2] = rightLid;               // left lid
        convertedExpressions[3] = leftEyebrow;            // left eyebrow
        convertedExpressions[4] = leftEyewide;            // left eye wide
        convertedExpressions[5] = leftEyeYawCorrected;  // right pitch
        convertedExpressions[6] = eyeY;                   // right yaw
        convertedExpressions[7] = leftLid;                // right lid
        convertedExpressions[8] = leftEyebrow;            // right eyebrow
        convertedExpressions[9] = leftEyewide;            // right eye wide

        arKitExpressions = convertedExpressions;

        return true;
    }


    public void Dispose()
    {
        TryDisposeObject(VideoSource);
        TryDisposeObject(ImageTransformer);
        TryDisposeObject(ImageConverter);
        TryDisposeObject(InferenceService);
        TryDisposeObject(Filter);
        TryDisposeObject(_fastCorruptionDetector);
        TryDisposeObject(_imageCollector);
    }

    private void TryDisposeObject(object? obj)
    {
        (obj as IDisposable)?.Dispose();
    }
}
