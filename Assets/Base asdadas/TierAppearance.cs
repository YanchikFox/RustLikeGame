using UnityEngine;
using System.Collections.Generic;

[System.Serializable]
public class TierAppearance
{
    public BuildingTier tier;
    public float maxHealth = 200f;
    public List<DamageModifier> damageModifiers;
    
    [Header("Costs")]
    [Tooltip("��������� ��������� �� ���������� ������, ������������� � BuildingTier.")]
    public List<ResourceCost> upgradeCost;
    [Tooltip("��������� ������� ������� ������� � 0 �� ������������� �������� ��� ����� ������.")]
    public List<ResourceCost> fullRepairCost;

    [Header("Visuals")]
    public Mesh mesh;
    public Material[] materials;
}
