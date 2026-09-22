using UnityEngine;

public class DwellSelectionBox : MonoBehaviour
{
    public float dwellTime = 3f;

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

            Debug.Log(
                $"{gameObject.name}: {hoverTimer:F1} / {dwellTime}"
            );

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

        Debug.Log($"{gameObject.name} SELECTED!");

        if (manager != null)
        {
            manager.OnClothingSelected(this);
        }
    }

    public void ResetSelection()
    {
        selected = false;
        hoverTimer = 0f;
    }
}