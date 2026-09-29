using UnityEngine;
using UnityEngine.UI;

public class KinectCameraViewer : MonoBehaviour
{
    private Texture2D colorTexture;
    private RawImage targetImage;

    private const int ColorWidth = 1920;
    private const int ColorHeight = 1080;

    private void Start()
    {
        targetImage = GetComponent<RawImage>();

        if (targetImage == null)
        {
            Debug.LogError(
                "KinectCameraViewer must be attached to a UI RawImage."
            );

            enabled = false;
            return;
        }

        colorTexture = new Texture2D(
            ColorWidth,
            ColorHeight,
            TextureFormat.BGRA32,
            false
        );

        colorTexture.filterMode =
            FilterMode.Bilinear;

        targetImage.texture =
            colorTexture;


        Debug.Log(
            "KinectCameraViewer initialized."
        );
    }

    private void Update()
    {
        if (colorTexture == null)
        {
            return;
        }


        KinectManager manager =
            KinectManager.Instance;


        if (manager == null)
        {
            return;
        }


        if (!manager.TryGetColorFrame(
                out byte[] colorData))
        {
            return;
        }


        if (colorData == null)
        {
            return;
        }

        colorTexture.LoadRawTextureData(
            colorData
        );

        colorTexture.Apply(false);
    }

    private void OnDestroy()
    {

        if (targetImage != null)
        {
            targetImage.texture = null;
        }


        if (colorTexture != null)
        {
            Destroy(colorTexture);
            colorTexture = null;
        }
    }
}