using UnityEngine;
using UnityEngine.Events;

public class DwellSelectionBox : MonoBehaviour
{
    public float dwellTime = 3f;

    [SerializeField] private bool debugLog = false;

    public UnityEvent onDwellComplete;

    private float hoverTimer = 0f;
    private bool selected = false;

    public GestureUIManager manager;

    public void UpdateHover(Vector2 handScreenPosition)
    {
        RectTransform rect = GetComponent<RectTransform>();

        bool isHovering =
            RectTransformUtility.RectangleContainsScreenPoint(
                rect,
                handScreenPosition,
                null
            );

        if (isHovering)
        {
            hoverTimer += Time.deltaTime;

            if (debugLog)
            {
                Debug.Log(
                    $"{gameObject.name}: {hoverTimer:F1} / {dwellTime}"
                );
            }

            if (hoverTimer >= dwellTime && !selected)
            {
                Select();
            }
        }
        else
        {
            hoverTimer = 0f;
        }
    }

    private void Select()
    {
        selected = true;

        // Fires before the manager callback so arrow buttons (which have no
        // manager assigned) still get their CarouselController call.
        onDwellComplete?.Invoke();

        if (manager != null)
        {
            manager.OnClothingSelected(this);
        }

        // Re-arm so the component can fire again. 
        selected = false;
        hoverTimer = 0f;
    }

    public void ResetSelection()
    {
        selected = false;
        hoverTimer = 0f;
    }
}
