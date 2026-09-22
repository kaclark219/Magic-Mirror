using UnityEngine;
using UnityEngine.InputSystem;

public class HandController : MonoBehaviour
{
    public RectTransform handCursor;

    public Vector2 HandScreenPosition { get; private set; }

    void Update()
    {
        if (Mouse.current == null)
            return;

        HandScreenPosition = Mouse.current.position.ReadValue();

        if (handCursor != null)
        {
            handCursor.position = HandScreenPosition;
        }
    }
}