using System.Collections.Generic;
using Mediapipe;
using Mediapipe.Tasks.Core;
using Mediapipe.Tasks.Vision.Core;
using Mediapipe.Tasks.Vision.PoseLandmarker;
using Mediapipe.Tasks.Components.Containers;
using Mediapipe.Unity.Experimental;
using UnityEngine;

public class MediaPipeBodyTracker : MonoBehaviour
{
    [Header("Input")]
    [SerializeField]
    private KinectCameraViewer cameraViewer;

    [Header("Tracking")]
    [SerializeField]
    [Range(0f, 1f)]
    private float minPoseDetectionConfidence = 0.5f;

    [SerializeField]
    [Range(0f, 1f)]
    private float minPosePresenceConfidence = 0.5f;

    [SerializeField]
    [Range(0f, 1f)]
    private float minTrackingConfidence = 0.5f;

    [Header("Performance")]
    [SerializeField]
    [Min(1)]
    private int processEveryNFrames = 2;

    public bool BodyDetected { get; private set; }

    public IReadOnlyList<Vector3> Landmarks { get; private set; }

    public IReadOnlyList<Vector3> WorldLandmarks { get; private set; }

    // Landmarks back-projected into Kinect color camera space, in meters:
    // X right, Y down, Z forward. Vector3.zero when no metric depth
    public IReadOnlyList<Vector3> CameraSpaceLandmarks { get; private set; }

    // Depth in meters used per landmark, or float.NaN
    public IReadOnlyList<float> LandmarkDepthsMeters { get; private set; }

    // True when the last detection pass fused depth.
    public bool DepthFusionEnabled { get; private set; }

    private PoseLandmarker poseLandmarker;
    private PoseLandmarkerResult result;
    private TextureFrame textureFrame;

    private long timestampMilliseconds = 0;
    private int frameCounter = 0;
    private bool initialized = false;

    private Vector3[] normalizedLandmarks = new Vector3[33];
    private Vector3[] worldLandmarks = new Vector3[33];
    private Vector3[] cameraSpaceLandmarks = new Vector3[33];
    private float[] landmarkDepthsMeters = new float[33];

    private const string ModelPath = "pose_landmarker_lite.bytes";

    private void Start()
    {
        CameraSpaceLandmarks = cameraSpaceLandmarks;
        LandmarkDepthsMeters = landmarkDepthsMeters;

        ResetMetricLandmarks();

        InitializeMediaPipe();
    }

    private void InitializeMediaPipe()
    {
        if (cameraViewer == null)
        {
            Debug.LogError("MediaPipeBodyTracker: KinectCameraViewer is not assigned.");
            enabled = false;
            return;
        }
        try
        {
            string modelPath = System.IO.Path.Combine(
                Application.streamingAssetsPath,
                ModelPath
            );
            Debug.Log($"MediaPipe pose model path: {modelPath}");

            if (!System.IO.File.Exists(modelPath))
            {
                Debug.LogError($"MediaPipe pose model was not found at:\n{modelPath}");
                enabled = false;
                return;
            }

            Debug.Log("Initializing MediaPipe Pose Landmarker...");

            var baseOptions = new BaseOptions(
                BaseOptions.Delegate.CPU,
                modelAssetPath: modelPath
            );

            var options = new PoseLandmarkerOptions(
                baseOptions,
                runningMode: RunningMode.VIDEO,
                numPoses: 1,
                minPoseDetectionConfidence: minPoseDetectionConfidence,
                minPosePresenceConfidence: minPosePresenceConfidence,
                minTrackingConfidence: minTrackingConfidence,
                outputSegmentationMasks: false
            );

            poseLandmarker = PoseLandmarker.CreateFromOptions(options, null);

            result = PoseLandmarkerResult.Alloc(1, false);

            textureFrame = new TextureFrame(
                KinectManager.ColorWidth,
                KinectManager.ColorHeight,
                TextureFormat.RGBA32
            );

            Landmarks = normalizedLandmarks;
            WorldLandmarks = worldLandmarks;

            timestampMilliseconds = 0;
            initialized = true;

            Debug.Log("MediaPipe Pose Landmarker initialized successfully.");

        }
        catch (System.Exception ex)
        {
            Debug.LogError("Failed to initialize MediaPipe Pose Landmarker:\n" + ex);
            initialized = false;
            enabled = false;
        }
    }


    private void Update()
    {
        if (!initialized || poseLandmarker == null || textureFrame == null || cameraViewer == null)
        {
            return;
        }

        Texture2D sourceTexture = cameraViewer.ColorTexture;

        if (sourceTexture == null)
        {
            BodyDetected = false;
            return;
        }

        frameCounter++;

        if (frameCounter % processEveryNFrames != 0)
        {
            return;
        }

        DetectBody(sourceTexture);
    }

    private void DetectBody(Texture2D sourceTexture)
    {
        try
        {
            textureFrame.ReadTextureOnCPU(sourceTexture, false, false);

            using Image image = textureFrame.BuildCPUImage();

            long newTimestamp = (long)(Time.realtimeSinceStartupAsDouble * 1000.0);
            if (newTimestamp <= timestampMilliseconds)
            {
                newTimestamp = timestampMilliseconds + 1;
            }
            timestampMilliseconds = newTimestamp;

            var imageProcessingOptions = new ImageProcessingOptions();

            bool detected = poseLandmarker.TryDetectForVideo(
                image,
                timestampMilliseconds,
                imageProcessingOptions,
                ref result
            );

            if (!detected || result.poseLandmarks == null || result.poseLandmarks.Count == 0)
            {
                BodyDetected = false;
                ResetMetricLandmarks();
                return;
            }

            var pose = result.poseLandmarks[0];

            KinectManager kinectManager = KinectManager.Instance;

            bool depthEnabled = kinectManager != null && kinectManager.enableDepth;

            float colorFx = depthEnabled ? kinectManager.ColorFx : 0f;
            float colorFy = depthEnabled ? kinectManager.ColorFy : 0f;
            float colorCx = depthEnabled ? kinectManager.ColorCx : 0f;
            float colorCy = depthEnabled ? kinectManager.ColorCy : 0f;

            // All zero when the calibration could not be read.
            bool depthFusionActive = colorFx > 0f && colorFy > 0f;
            DepthFusionEnabled = depthFusionActive;

            if (pose.landmarks != null && pose.landmarks.Count >= 33)
            {
                for (int i = 0; i < 33; i++)
                {
                    var lm = pose.landmarks[i];
                    normalizedLandmarks[i] = new Vector3(lm.x, lm.y, lm.z);

                    if (!depthFusionActive)
                    {
                        cameraSpaceLandmarks[i] = Vector3.zero;
                        landmarkDepthsMeters[i] = float.NaN;
                        continue;
                    }

                    int pixelX = Mathf.Clamp(
                        Mathf.RoundToInt(lm.x * KinectManager.ColorWidth),
                        0,
                        KinectManager.ColorWidth - 1
                    );

                    int pixelY = Mathf.Clamp(
                        Mathf.RoundToInt(lm.y * KinectManager.ColorHeight),
                        0,
                        KinectManager.ColorHeight - 1
                    );

                    float zMeters;
                    kinectManager.TryGetDepthAtColorPixel(pixelX, pixelY, out zMeters);

                    // DEPTH SMOOTHING
                    zMeters = SmoothDepth(i, zMeters);
                    // END DEPTH SMOOTHING

                    if (zMeters > 0f)
                    {
                        cameraSpaceLandmarks[i] = new Vector3(
                            (pixelX - colorCx) / colorFx * zMeters,
                            (pixelY - colorCy) / colorFy * zMeters,
                            zMeters
                        );

                        landmarkDepthsMeters[i] = zMeters;
                    }
                    else
                    {
                        cameraSpaceLandmarks[i] = Vector3.zero;
                        landmarkDepthsMeters[i] = float.NaN;
                    }
                }
            }
            else
            {
                BodyDetected = false;
                ResetMetricLandmarks();
                return;
            }

            if (result.poseWorldLandmarks != null && result.poseWorldLandmarks.Count > 0)
            {
                var world = result.poseWorldLandmarks[0];
                if (world.landmarks != null && world.landmarks.Count >= 33)
                {
                    for (int i = 0; i < 33; i++)
                    {
                        var lm = world.landmarks[i];
                        worldLandmarks[i] = new Vector3(lm.x, lm.y, lm.z);
                    }
                }
            }

            BodyDetected = true;
        }
        catch (System.Exception ex)
        {
            BodyDetected = false;
            ResetMetricLandmarks();
            Debug.LogError("MediaPipe pose detection failed:\n" + ex);
        }
    }

    private void ResetMetricLandmarks()
    {
        for (int i = 0; i < 33; i++)
        {
            cameraSpaceLandmarks[i] = Vector3.zero;
            landmarkDepthsMeters[i] = float.NaN;
        }
    }

    // DEPTH SMOOTHING
    private const float DepthSmoothingAlpha = 0.5f;
    private const float DepthHoldSeconds = 0.5f;

    private readonly float[] smoothedDepths = new float[33];
    private readonly float[] lastValidDepthTimes = new float[33];

    // Exponential moving average on valid depth, reusing the last valid value
    // for up to DepthHoldSeconds. Delete this block and the SmoothDepth call
    // in DetectBody to remove temporal smoothing.
    private float SmoothDepth(int index, float measuredMeters)
    {
        float now = Time.unscaledTime;

        if (measuredMeters > 0f)
        {
            smoothedDepths[index] = lastValidDepthTimes[index] > 0f
                ? smoothedDepths[index] + (measuredMeters - smoothedDepths[index]) * DepthSmoothingAlpha
                : measuredMeters;

            lastValidDepthTimes[index] = now;

            return smoothedDepths[index];
        }

        if (lastValidDepthTimes[index] > 0f && now - lastValidDepthTimes[index] <= DepthHoldSeconds)
        {
            return smoothedDepths[index];
        }

        return float.NaN;
    }
    // END DEPTH SMOOTHING

    private void OnDestroy()
    {
        initialized = false;

        if (textureFrame != null)
        {
            textureFrame.Dispose();
            textureFrame = null;
        }

        if (poseLandmarker != null)
        {
            poseLandmarker.Close();
            poseLandmarker = null;
        }
    }
}