using UnityEngine;

public class HandController : MonoBehaviour
{
    public RectTransform handCursor;
    public Canvas canvas;

    public Vector2 HandScreenPosition { get; private set; }

    void Update()
    {
        HandScreenPosition = Input.mousePosition;

        if (handCursor == null || canvas == null)
            return;

        RectTransform canvasRect = canvas.GetComponent<RectTransform>();

        Vector2 localPoint;

        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            canvasRect,
            HandScreenPosition,
            canvas.renderMode == RenderMode.ScreenSpaceOverlay
                ? null
                : canvas.worldCamera,
            out localPoint
        );

        handCursor.localPosition = localPoint;
    }
}