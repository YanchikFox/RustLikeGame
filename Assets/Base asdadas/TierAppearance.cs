using UnityEngine;
using System.Collections.Generic;

[System.Serializable]
public class TierAppearance
{
    public BuildingTier tier;
    public float maxHealth = 200f;
    public List<DamageModifier> damageModifiers;
    
    [Header("Costs")]
    [Tooltip("Cost for upgrading to the next tier, configured in BuildingTier.")]
    public List<ResourceCost> upgradeCost;
    [Tooltip("Cost for full repair from 0 to maximum health for this tier.")]
    public List<ResourceCost> fullRepairCost;

    [Header("Visuals")]
    public Mesh mesh;
    public Material[] materials;
}
