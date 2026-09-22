using UnityEngine;

public class HandController : MonoBehaviour
{
    public RectTransform handCursor;

    public Vector2 HandScreenPosition { get; private set; }

    void Update()
    {
        HandScreenPosition = Input.mousePosition;

        handCursor.position = HandScreenPosition;
    }
}