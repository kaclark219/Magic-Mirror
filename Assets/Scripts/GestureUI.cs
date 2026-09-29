using System.Collections.Generic;
using UnityEngine;

public class GestureUIManager : MonoBehaviour
{
    public HandController handController;

    // The clothing items. Dwell completion here is a final selection.
    public DwellSelectionBox[] clothingBoxes;
    [SerializeField] private List<DwellSelectionBox> carouselButtons =
        new List<DwellSelectionBox>();

    [SerializeField] private CarouselController carousel;

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

        foreach (DwellSelectionBox button in carouselButtons)
        {
            button.UpdateHover(handPosition);
        }
    }


    public void OnPreviousClicked()
    {
        if (selectionMade)
            return;

        carousel?.Previous();
    }

    public void OnNextClicked()
    {
        if (selectionMade)
            return;

        carousel?.Next();
    }

    public void OnClothingSelected(DwellSelectionBox selectedBox)
    {
        selectionMade = true;

        Debug.Log($"Selected clothing: {selectedBox.gameObject.name}");

        foreach (DwellSelectionBox box in clothingBoxes)
        {
            box.gameObject.SetActive(false);
        }

        foreach (DwellSelectionBox button in carouselButtons)
        {
            button.gameObject.SetActive(false);
        }
    }
}
