using UnityEngine;
using System.Collections.Generic;

[System.Serializable]
public class TierAppearance
{
    public BuildingTier tier;
    public float maxHealth = 200f;
    public List<DamageModifier> damageModifiers;
    
    [Header("Costs")]
    [Tooltip("Стоимость улучшения до СЛЕДУЮЩЕГО уровня, определенного в BuildingTier.")]
    public List<ResourceCost> upgradeCost;
    [Tooltip("Стоимость ПОЛНОГО ремонта объекта с 0 до максимального здоровья для этого уровня.")]
    public List<ResourceCost> fullRepairCost;

    [Header("Visuals")]
    public Mesh mesh;
    public Material[] materials;
}
