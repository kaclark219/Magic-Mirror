using UnityEngine;
using UnityEngine.InputSystem;

public class HandController : MonoBehaviour
{
    [Header("References")]
    public RectTransform handCursor;
    public MediaPipeHandTracker handTracker;

    [Header("Testing")]
    public bool useMouseFallback = false;

    [Header("Cursor")]
    [Range(0f, 1f)]
    public float smoothing = 0.35f;

    public Vector2 HandScreenPosition { get; private set; }

    public bool HasTrackingInput { get; private set; }

    private bool hasPosition = false;
    private bool cursorVisible = false;

    private void Start()
    {
        SetCursorVisible(false);
    }

    private void Update()
    {
        Vector2 targetPosition;

        if (handTracker != null &&
            handTracker.HandDetected)
        {
            Vector2 normalized =
                handTracker.IndexTipNormalized;

            targetPosition = new Vector2(
                normalized.x * Screen.width,
                normalized.y * Screen.height
            );

            HasTrackingInput = true;

            SetCursorVisible(true);
        }

        else if (useMouseFallback && Mouse.current != null)
        {
            targetPosition =
                Mouse.current.position.ReadValue();

            HasTrackingInput = true;

            SetCursorVisible(true);
        }

        else
        {
            HasTrackingInput = false;
            hasPosition = false;

            SetCursorVisible(false);

            return;
        }

        if (!hasPosition)
        {
            HandScreenPosition = targetPosition;
            hasPosition = true;
        }
        else
        {
            HandScreenPosition = Vector2.Lerp(
                HandScreenPosition,
                targetPosition,
                smoothing
            );
        }

        if (handCursor != null)
        {
            handCursor.position =
                HandScreenPosition;
        }
    }

    private void SetCursorVisible(bool visible)
    {
        if (handCursor == null)
        {
            return;
        }

        if (cursorVisible == visible)
        {
            return;
        }

        cursorVisible = visible;

        handCursor.gameObject.SetActive(visible);
    }
}