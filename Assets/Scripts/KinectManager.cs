using System;
using System.Threading;
using UnityEngine;

using Microsoft.Azure.Kinect.Sensor;
using Microsoft.Azure.Kinect.BodyTracking;

using KinectJoint =
    Microsoft.Azure.Kinect.BodyTracking.Joint;


public class KinectManager : MonoBehaviour
{
    public static KinectManager Instance
    {
        get;
        private set;
    }

    public Device Device
    {
        get;
        private set;
    }

    public Calibration Calibration
    {
        get;
        private set;
    }

    private Tracker bodyTracker;

    private Thread trackingThread;

    private volatile bool trackingThreadRunning = false;
    private volatile bool shuttingDown = false;

    private readonly object errorLock = new object();
    private string pendingWorkerError = null;

    private int capturedFrameCount = 0;
    private float nextStatusLogTime = 0f;


    public int CapturedFrameCount
    {
        get
        {
            return capturedFrameCount;
        }
    }

    private readonly object bodyLock = new object();

    private bool bodyDetected = false;

    private KinectJoint rightHandJoint;
    private KinectJoint rightHandTipJoint;


    public bool BodyDetected
    {
        get
        {
            lock (bodyLock)
            {
                return bodyDetected;
            }
        }
    }


    public KinectJoint RightHandJoint
    {
        get
        {
            lock (bodyLock)
            {
                return rightHandJoint;
            }
        }
    }


    public KinectJoint RightHandTipJoint
    {
        get
        {
            lock (bodyLock)
            {
                return rightHandTipJoint;
            }
        }
    }


    // ============================================================
    // COLOR FRAME DATA
    // ============================================================

    private readonly object colorLock = new object();

    private byte[] latestColorData = null;
    private bool newColorFrameAvailable = false;


    public bool TryGetColorFrame(out byte[] colorData)
    {
        lock (colorLock)
        {
            if (!newColorFrameAvailable ||
                latestColorData == null)
            {
                colorData = null;
                return false;
            }

            colorData = latestColorData;
            newColorFrameAvailable = false;

            return true;
        }
    }

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

    private void InitializeKinect()
    {
        try
        {
            Debug.Log(
                "Opening Azure Kinect..."
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
                        ColorResolution.R1080p,

                    DepthMode =
                        DepthMode.WFOV_2x2Binned,

                    CameraFPS =
                        FPS.FPS30,

                    SynchronizedImagesOnly =
                        false
                };


            Debug.Log(
                "Starting COLOR-ONLY Azure Kinect..."
            );


            Device.StartCameras(
                config
            );


            Debug.Log(
                "Color camera started."
            );

            Calibration =
                Device.GetCalibration(
                    DepthMode.WFOV_2x2Binned,
                    ColorResolution.R1080p
                );

            Debug.Log(
                "Kinect calibration loaded."
            );

            TrackerConfiguration trackerConfig =
                TrackerConfiguration.Default;

            trackerConfig.ProcessingMode =
                TrackerProcessingMode.Cpu;

            trackerConfig.SensorOrientation =
                SensorOrientation.Default;

            trackerConfig.ModelPath =
                "/usr/bin/dnn_model_2_0_op11.onnx";

            Debug.Log(
                "Creating body tracker in CPU mode..."
            );

            bodyTracker =
                Tracker.Create(
                    Calibration,
                    trackerConfig
                );

            Debug.Log(
                "Azure Kinect Body Tracker initialized."
            );


            capturedFrameCount = 0;

            lock (bodyLock)
            {
                bodyDetected = false;
            }

            lock (colorLock)
            {
                latestColorData = null;
                newColorFrameAvailable = false;
            }

            shuttingDown = false;
            trackingThreadRunning = true;


            trackingThread =
                new Thread(
                    TrackingLoop
                );


            trackingThread.IsBackground =
                true;


            trackingThread.Name =
                "Azure Kinect Camera";


            trackingThread.Start();


            Debug.Log(
                "Kinect camera thread started."
            );
        }
        catch (Exception ex)
        {
            Debug.LogError(
                "Failed to initialize Kinect:\n" +
                ex
            );


            SafeCleanupAfterInitializationFailure();
        }
    }

    private void TrackingLoop()
    {

        TimeSpan captureTimeout =
            TimeSpan.FromSeconds(2);


        while (trackingThreadRunning)
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


                if (!trackingThreadRunning)
                {
                    break;
                }


                if (capture == null)
                {
                    continue;
                }


                Interlocked.Increment(
                    ref capturedFrameCount
                );

                if (capture.Color != null)
                {
                    byte[] frameData =
                        capture.Color.Memory.ToArray();


                    lock (colorLock)
                    {
                        latestColorData =
                            frameData;

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
                "KINECT WORKER EXCEPTION:\n" +
                error
            );
        }

        if (Time.time >= nextStatusLogTime)
        {
            nextStatusLogTime =
                Time.time + 2f;


            Debug.Log(
                "Kinect captures received: " +
                CapturedFrameCount
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
        trackingThreadRunning = false;


        Debug.Log(
            "Beginning Kinect shutdown..."
        );

        if (trackingThread != null &&
            trackingThread.IsAlive)
        {
            Debug.Log(
                "Waiting for camera worker to finish..."
            );

            bool exited =
                trackingThread.Join(5000);


            if (!exited)
            {
                Debug.LogError(
                    "Kinect camera worker did not exit " +
                    "within 5 seconds. Native cleanup " +
                    "has been skipped to avoid an " +
                    "Editor hang."
                );


                trackingThread = null;

                return;
            }
        }


        trackingThread = null;


        Debug.Log(
            "Camera worker exited."
        );


        if (bodyTracker != null)
        {
            try
            {
                Debug.Log(
                    "Shutting down body tracker..."
                );


                bodyTracker.Shutdown();
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "Body tracker shutdown warning: " +
                    ex.Message
                );
            }


            try
            {
                Debug.Log(
                    "Disposing body tracker..."
                );


                bodyTracker.Dispose();
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "Body tracker dispose warning: " +
                    ex.Message
                );
            }


            bodyTracker = null;
        }

        if (Device != null)
        {
            try
            {
                Debug.Log(
                    "Stopping Kinect cameras..."
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
                    "Disposing Kinect device..."
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

        lock (bodyLock)
        {
            bodyDetected = false;
        }


        lock (colorLock)
        {
            latestColorData = null;
            newColorFrameAvailable = false;
        }


        Debug.Log(
            "Kinect shutdown complete."
        );
    }

    private void SafeCleanupAfterInitializationFailure()
    {
        trackingThreadRunning = false;
        shuttingDown = true;

        if (bodyTracker != null)
        {
            try
            {
                bodyTracker.Shutdown();
            }
            catch
            {
            }


            try
            {
                bodyTracker.Dispose();
            }
            catch
            {
            }


            bodyTracker = null;
        }


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
            }


            Device = null;
        }


        lock (bodyLock)
        {
            bodyDetected = false;
        }


        lock (colorLock)
        {
            latestColorData = null;
            newColorFrameAvailable = false;
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