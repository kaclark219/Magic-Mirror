using System;
using System.Threading;
using UnityEngine;

using Microsoft.Azure.Kinect.Sensor;


public class KinectManager : MonoBehaviour
{
    public static KinectManager Instance
    {
        get;
        private set;
    }
    private const int SensorColorWidth = 1280;
    private const int SensorColorHeight = 720;
    private const int ColorBytesPerPixel = 4;

    public const int ColorWidth = SensorColorHeight;
    public const int ColorHeight = SensorColorWidth;

    public enum PortraitRotation
    {
        Clockwise90,
        CounterClockwise90
    }

    [Header("Portrait Camera")]
    [SerializeField]
    [Tooltip("Rotation applied to the captured image. Stop Play mode before changing this setting.")]
    private PortraitRotation portraitRotation = PortraitRotation.Clockwise90;

    private bool rotateClockwise;
    public Device Device
    {
        get;
        private set;
    }

    private Thread cameraThread;

    private volatile bool cameraThreadRunning = false;
    private volatile bool shuttingDown = false;

    private readonly object errorLock = new object();

    private string pendingWorkerError = null;

    private readonly object colorLock = new object();

    private byte[] latestColorData = null;
    private byte[] displayedColorData = null;

    private bool newColorFrameAvailable = false;

    private void Awake()
    {
        if (Instance != null &&
            Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }


    private void Start()
    {
        InitializeKinect();
    }

    public bool TryGetColorFrame(
        out byte[] colorData)
    {
        lock (colorLock)
        {
            if (!newColorFrameAvailable ||
                latestColorData == null)
            {
                colorData = null;
                return false;
            }

            byte[] reusableBuffer =
                displayedColorData;

            displayedColorData =
                latestColorData;

            latestColorData =
                reusableBuffer;


            colorData =
                displayedColorData;


            newColorFrameAvailable =
                false;


            return true;
        }
    }

    private void InitializeKinect()
    {
        try
        {
            Debug.Log(
                "Opening Azure Kinect RGB camera..."
            );

            Device =
                Device.Open(0);


            Debug.Log(
                "Azure Kinect opened."
            );

            DeviceConfiguration config =
                new DeviceConfiguration
                {
                    ColorFormat =
                        ImageFormat.ColorBGRA32,

                    ColorResolution =
                        ColorResolution.R720p,

                    DepthMode =
                        DepthMode.Off,

                    CameraFPS =
                        FPS.FPS30,

                    SynchronizedImagesOnly =
                        false
                };

            Debug.Log(
                "Starting Azure Kinect RGB camera..."
            );


            Device.StartCameras(
                config
            );


            Debug.Log(
                "Azure Kinect RGB camera started."
            );

            lock (colorLock)
            {
                latestColorData = null;
                displayedColorData = null;

                newColorFrameAvailable =
                    false;
            }

            shuttingDown = false;
            rotateClockwise = portraitRotation == PortraitRotation.Clockwise90;
            cameraThreadRunning = true;


            cameraThread =
                new Thread(
                    CameraLoop
                );


            cameraThread.IsBackground =
                true;


            cameraThread.Name =
                "Azure Kinect RGB Camera";


            cameraThread.Start();


            Debug.Log(
                "Azure Kinect camera thread started."
            );
        }
        catch (Exception ex)
        {
            Debug.LogError(
                "Failed to initialize Azure Kinect:\n" +
                ex
            );


            SafeCleanupAfterInitializationFailure();
        }
    }

    private void CameraLoop()
    {
        byte[] captureColorData = null;

        TimeSpan captureTimeout =
            TimeSpan.FromSeconds(2);


        while (cameraThreadRunning)
        {
            Capture capture = null;

            try
            {
                try
                {
                    capture =
                        Device.GetCapture(
                            captureTimeout
                        );
                }
                catch (TimeoutException)
                {

                    continue;
                }


                if (!cameraThreadRunning)
                {
                    break;
                }


                if (capture == null)
                {
                    continue;
                }

                for (int skipped = 0; skipped < 8 && cameraThreadRunning; skipped++)
                {
                    Capture newerCapture = null;


                    try
                    {
                        newerCapture =
                            Device.GetCapture(
                                TimeSpan.Zero
                            );
                    }
                    catch (TimeoutException)
                    {
                        break;
                    }


                    if (newerCapture == null)
                    {
                        break;
                    }


                    if (newerCapture.Color == null)
                    {
                        newerCapture.Dispose();
                        continue;
                    }


                    Capture olderCapture =
                        capture;


                    capture =
                        newerCapture;


                    olderCapture.Dispose();
                }


                if (!cameraThreadRunning)
                {
                    break;
                }


                if (capture.Color != null)
                {
                    var colorMemory =
                        capture.Color.Memory;

                    if (captureColorData == null ||
                        captureColorData.Length !=
                        colorMemory.Length)
                    {
                        captureColorData =
                            new byte[
                                colorMemory.Length
                            ];
                    }


                    RotateColorFrame(
                        colorMemory.Span,
                        captureColorData,
                        rotateClockwise
                    );

                    lock (colorLock)
                    {
                        byte[] reusableBuffer =
                            latestColorData;


                        latestColorData =
                            captureColorData;


                        captureColorData =
                            reusableBuffer;


                        newColorFrameAvailable =
                            true;
                    }
                }
            }
            catch (Exception ex)
            {
                if (shuttingDown)
                {
                    break;
                }

                lock (errorLock)
                {
                    if (pendingWorkerError == null)
                    {
                        pendingWorkerError =
                            ex.ToString();


                        if (string.IsNullOrWhiteSpace(
                                pendingWorkerError))
                        {
                            pendingWorkerError =
                                "Exception type: " +
                                ex.GetType().FullName;
                        }
                    }
                }

                Thread.Sleep(10);
            }
            finally
            {
                if (capture != null)
                {
                    capture.Dispose();
                    capture = null;
                }
            }
        }
    }

    private static void RotateColorFrame(ReadOnlySpan<byte> source, byte[] destination, bool clockwise)
    {
        for (int y = 0; y < SensorColorHeight; y++)
        {
            int sourceOffset = y * SensorColorWidth * ColorBytesPerPixel;
            int destinationX = clockwise ? SensorColorHeight - 1 - y : y;
            int destinationY = clockwise ? 0 : SensorColorWidth - 1;
            int destinationOffset =
                (destinationY * ColorWidth + destinationX) * ColorBytesPerPixel;
            int destinationStep =
                (clockwise ? ColorWidth : -ColorWidth) * ColorBytesPerPixel;

            for (int x = 0; x < SensorColorWidth; x++)
            {
                destination[destinationOffset] = source[sourceOffset];
                destination[destinationOffset + 1] = source[sourceOffset + 1];
                destination[destinationOffset + 2] = source[sourceOffset + 2];
                destination[destinationOffset + 3] = source[sourceOffset + 3];

                sourceOffset += ColorBytesPerPixel;
                destinationOffset += destinationStep;
            }
        }
    }

    private void Update()
    {

        string error = null;


        lock (errorLock)
        {
            if (pendingWorkerError != null)
            {
                error =
                    pendingWorkerError;


                pendingWorkerError =
                    null;
            }
        }


        if (!string.IsNullOrEmpty(error))
        {
            Debug.LogError(
                "AZURE KINECT CAMERA ERROR:\n" +
                error
            );
        }
    }

    private void ShutdownKinect()
    {
        if (shuttingDown)
        {
            return;
        }


        shuttingDown = true;
        cameraThreadRunning = false;


        Debug.Log(
            "Beginning Azure Kinect shutdown..."
        );

        if (cameraThread != null &&
            cameraThread.IsAlive)
        {
            Debug.Log(
                "Waiting for camera worker to finish..."
            );

            bool exited =
                cameraThread.Join(5000);


            if (!exited)
            {
                Debug.LogError(
                    "Azure Kinect camera worker did not " +
                    "exit within 5 seconds. Native cleanup " +
                    "was skipped to avoid an Editor hang."
                );


                cameraThread = null;

                return;
            }
        }


        cameraThread = null;


        Debug.Log(
            "Camera worker exited."
        );


        if (Device != null)
        {
            try
            {
                Debug.Log(
                    "Stopping Azure Kinect camera..."
                );


                Device.StopCameras();
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "Camera stop warning: " +
                    ex.Message
                );
            }

            try
            {
                Debug.Log(
                    "Disposing Azure Kinect device..."
                );


                Device.Dispose();
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "Device dispose warning: " +
                    ex.Message
                );
            }


            Device = null;
        }

        lock (colorLock)
        {
            latestColorData = null;
            displayedColorData = null;

            newColorFrameAvailable =
                false;
        }


        Debug.Log(
            "Azure Kinect shutdown complete."
        );
    }
    private void SafeCleanupAfterInitializationFailure()
    {
        cameraThreadRunning = false;
        shuttingDown = true;


        if (Device != null)
        {
            try
            {
                Device.StopCameras();
            }
            catch
            {
            }


            try
            {
                Device.Dispose();
            }
            catch
            {
                // Ignore cleanup failures here.
            }


            Device = null;
        }


        lock (colorLock)
        {
            latestColorData = null;
            displayedColorData = null;

            newColorFrameAvailable =
                false;
        }
    }

    private void OnDestroy()
    {
        ShutdownKinect();


        if (Instance == this)
        {
            Instance = null;
        }
    }
}