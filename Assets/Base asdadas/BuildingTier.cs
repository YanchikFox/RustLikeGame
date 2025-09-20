using UnityEngine;
using System.Collections.Generic;

[System.Serializable]
public class ResourceCost
{
    public Item resourceItem; // ????? ?????? ????? (ScriptableObject)
    public int amount;        // ??????? ?????
}

[CreateAssetMenu(fileName = "New Building Tier", menuName = "Construction/Building Tier")]
public class BuildingTier : ScriptableObject
{
    [Header("Tier Info")]
    public string tierName = "Wood";
    
    [Header("Upgrade Path")]
    public BuildingTier nextTier; // Следующий уровень для улучшения
}
