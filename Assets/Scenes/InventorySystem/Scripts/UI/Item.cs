using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

public enum ItemType {
    BuildingBlock,
    Tool,
    BuildingMaterial // ????????? ??? ????????
}

public enum ActionType {
    Dig,
    Mine
}

public enum ToolType
{
    None,
    Hammer
}

// ????? ???????????? ??? ????? ????????
public enum ResourceType
{
    None,
    Wood,
    Stone,
    Metal
}

[CreateAssetMenu(menuName = "Scriptable object/Item")]
public class Item : ScriptableObject {

    [Header("Only gameplay")]
    public TileBase tile;
    public ItemType type;
    public ResourceType resourceType; // ???? ??? ???????? ???? ???????
    public ActionType actionType;
    public ToolType toolType;
    public Vector2Int range = new Vector2Int(5, 4);
    public List<DamageInfo> damageTypes;

    [Header("Only UI")]
    public bool stackable = true;

    [Header("Both")]
    public Sprite image;

}
