using UnityEngine;

public class GestureUIManager : MonoBehaviour
{
    public HandController handController;
    public DwellSelectionBox[] clothingBoxes;

    private bool selectionMade = false;

    void Update()
    {
        if (handController == null || selectionMade)
            return;

        Vector2 handPosition =
            handController.HandScreenPosition;

        foreach (DwellSelectionBox box in clothingBoxes)
        {
            box.UpdateHover(handPosition);
        }
    }

    public void OnClothingSelected(DwellSelectionBox selectedBox)
    {
        selectionMade = true;

        Debug.Log($"Selected clothing: {selectedBox.gameObject.name}");

        foreach (DwellSelectionBox box in clothingBoxes)
        {
            box.gameObject.SetActive(false);
        }
    }
}