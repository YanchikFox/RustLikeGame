using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Zenject;

public class InventoryManager : MonoBehaviour
{
    public int maxStackedItems = 4;
    public InventorySlot[] inventorySlots;
    public GameObject inventoryItemPrefab;

    int selectedSlot = -1;

    private PlayerInput _playerInput;

    [Inject]
    public void Construct(PlayerInput playerInput)
    {
        _playerInput = playerInput;
    }

    /// <summary>
    /// ��������� ������� � ��������� ��������� ��������� �� ���������.
    /// </summary>
    /// <returns>True, ���� ��� ������� ���� ������� � �������. False � ��������� ������.</returns>
    public bool ConsumeItems(List<ResourceCost> costs)
    {
        // ��� 1: ���������, ���������� �� ���� ��������, ��������� ����� �����
        if (!HasItems(costs))
        {
            // ��������� �� ������ ������ ����� �������� �����, ���� �����
            Debug.LogWarning("[InventoryManager] ConsumeItems failed because HasItems returned false.");
            return false;
        }

        // ��� 2: ���� ��� ������� ����, ������� ��
        foreach (var cost in costs)
        {
            RemoveItems(cost.resourceItem, cost.amount);
        }

        return true;
    }

    /// <summary>
    /// ���������, ���������� �� � ������ �������� ��� �������� ���������, �� �������� ��.
    /// </summary>
    /// <returns>True, ���� ���� �������� ����������. False � ��������� ������.</returns>
    public bool HasItems(List<ResourceCost> costs)
    {
        if (costs == null || costs.Count == 0)
        {
            return true; // ���� ��������� �� ����������, �������, ��� ������� ����.
        }
        
        foreach (var cost in costs)
        {
            if (GetTotalItemCount(cost.resourceItem) < cost.amount)
            {
                return false; // ���� ���� �� ������ ������� �� �������, ���������� false.
            }
        }

        return true; // ���� �������� ����������.
    }

    /// <summary>
    /// �������� ����� ���������� ����������� �������� � ���������.
    /// </summary>
    /// <returns>����� ���������� ���������.</returns>
    private int GetTotalItemCount(Item item)
    {
        int total = 0;

        for (int i = 0; i < inventorySlots.Length; i++)
        {
            InventorySlot slot = inventorySlots[i];
            InventoryItem itemInSlot = slot.GetInventoryItem();

            if (itemInSlot != null && itemInSlot.item == item)
            {
                total += itemInSlot.count;
            }
        }

        return total;
    }

    /// <summary>
    /// ������� ��������� ���������� ��������� �� ���������.
    /// </summary>
    private void RemoveItems(Item item, int amountToRemove)
    {
        for (int i = inventorySlots.Length - 1; i >= 0; i--)
        {
            if (amountToRemove <= 0) break;

            InventorySlot slot = inventorySlots[i];
            InventoryItem itemInSlot = slot.GetInventoryItem();

            if (itemInSlot != null && itemInSlot.item == item)
            {
                int amountInSlot = itemInSlot.count;
                int amountToTake = Mathf.Min(amountToRemove, amountInSlot);

                itemInSlot.count -= amountToTake;
                amountToRemove -= amountToTake;

                if (itemInSlot.count <= 0)
                {
                    Destroy(itemInSlot.gameObject);
                }
                else
                {
                    itemInSlot.RefreshCount();
                }
            }
        }
    }

    private void Start()
    {
        ChangeSelectedSlot(0);
    }

    private void Update()
    {
        // ????????? ?????? ????? ? ???????
        if (_playerInput.HotbarDigit != -1)
        {
            // ????????, ??? ????? ????? ? ???????? ???????
            if (_playerInput.HotbarDigit < inventorySlots.Length)
            {
                ChangeSelectedSlot(_playerInput.HotbarDigit);
            }
        }
    }

    void ChangeSelectedSlot(int newValue) {
        if (selectedSlot >= 0) {
            inventorySlots[selectedSlot].Deselect();
        }

        inventorySlots[newValue].Select();
        selectedSlot = newValue;
    }

    public bool AddItem(Item item) {

        // Check if any slot has the same item with count lower than max
        for (int i = 0; i < inventorySlots.Length; i++) {
            InventorySlot slot = inventorySlots[i];
            InventoryItem itemInSlot = slot.GetInventoryItem();
            if (itemInSlot != null &&
                itemInSlot.item == item &&
                itemInSlot.count < maxStackedItems &&
                itemInSlot.item.stackable == true) {

                itemInSlot.count++;
                itemInSlot.RefreshCount();
                return true;
            }
        }

        // Find any empty slot
        for (int i = 0; i < inventorySlots.Length; i++) {
            InventorySlot slot = inventorySlots[i];
            InventoryItem itemInSlot = slot.GetInventoryItem();
            if (itemInSlot == null) {
                SpawnNewItem(item, slot);
                return true;
            }
        }

        return false;
    }

    void SpawnNewItem(Item item, InventorySlot slot) {
        GameObject newItemGo = Instantiate(inventoryItemPrefab, slot.transform);
        InventoryItem inventoryItem = newItemGo.GetComponent<InventoryItem>();
        inventoryItem.InitialiseItem(item);
    }

    public Item GetSelectedItem(bool use) {
        InventorySlot slot = inventorySlots[selectedSlot];
        InventoryItem itemInSlot = slot.GetInventoryItem();
        if (itemInSlot != null) {
            Item item = itemInSlot.item;
            if (use == true) {
                itemInSlot.count--;
                if (itemInSlot.count <= 0) {
                    Destroy(itemInSlot.gameObject);
                } else {
                    itemInSlot.RefreshCount();
                }
            }

            return item;
        }

        return null;
    }

}
