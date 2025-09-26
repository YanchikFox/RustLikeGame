using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public class ConstructionHealth : MonoBehaviour
{
    public Construction owner;
    public List<ConstructionHealth> neighbors = new List<ConstructionHealth>();
    private bool isGroundedFoundation = false;

    [Header("Health & Tier")]
    public BuildingTier currentTier;
    public float currentHealth;
    public float maxHealth { get; private set; }
    private TierAppearance _currentAppearance; // Current appearance object with mesh and materials

    public void Initialize(Construction owner, BuildingTier startingTier)
    {
        this.owner = owner;
        this.owner.stability = 0;

        SetTier(startingTier);
    }

    private void SetTier(BuildingTier newTier)
    {
        if (newTier == null)
        {
            Debug.LogError("Cannot set a null tier.", gameObject);
            return;
        }

        // Find and set appearance for the current tier from the factory
        _currentAppearance = owner.sourceFactory.appearances.FirstOrDefault(a => a.tier == newTier);

        if (_currentAppearance == null)
        {
            Debug.LogError($"Appearance for tier '{newTier.name}' not found in factory '{owner.sourceFactory.name}'.", gameObject);
            return;
        }

        currentTier = newTier;
        maxHealth = _currentAppearance.maxHealth;
        currentHealth = maxHealth; // Start with full health when setting a tier

        // Update mesh from appearance
        var meshFilter = GetComponent<MeshFilter>();
        var meshRenderer = GetComponent<Renderer>();

        if (meshFilter != null && _currentAppearance.mesh != null)
        {
            meshFilter.mesh = _currentAppearance.mesh;
        }
        if (meshRenderer != null && _currentAppearance.materials != null && _currentAppearance.materials.Length > 0)
        {
            meshRenderer.materials = _currentAppearance.materials;
        }
    }

    /// <summary>
    /// Apply damage to the object considering damage resistances.
    /// </summary>
    public void TakeDamage(List<DamageInfo> damageInfos)
    {
        if (_currentAppearance == null || damageInfos == null) return;

        float totalDamage = 0;

        foreach (var damageInfo in damageInfos)
        {
            // Find damage modifier for current damage type
            DamageModifier modifier = _currentAppearance.damageModifiers?.FirstOrDefault(m => m.type == damageInfo.type);
            
            float multiplier = modifier?.multiplier ?? 1f; // If no modifier found, use default (x1)
            float finalDamage = damageInfo.amount * multiplier;
            totalDamage += finalDamage;
            
            Debug.Log($"[Health] Calculated {finalDamage} ({damageInfo.amount} * {multiplier}x) {damageInfo.type} damage.");
        }

        currentHealth -= totalDamage;
        Debug.Log($"[Health] Dealt a total of {totalDamage} damage to {gameObject.name}. Health is now {currentHealth}/{maxHealth}");

        if (currentHealth <= 0)
        {
            currentHealth = 0;
            // TODO: Handle object destruction logic (drop items, notify neighbors)
            Destroy(gameObject);
        }
    }

    /// <summary>
    /// Restore health by consuming appropriate repair resources.
    /// </summary>
    /// <param name="amountToHeal">Amount of health to restore.</param>
    /// <param name="inventoryManager">Inventory manager for resource consumption.</param>
    public void Repair(float amountToHeal, InventoryManager inventoryManager)
    {
        if (currentHealth >= maxHealth) return;

        // Find appearance to calculate repair costs
        if (_currentAppearance == null)
        {
            Debug.LogError($"[Health] Appearance for tier '{currentTier.name}' not found. Cannot determine repair cost.", gameObject);
            return;
        }

        // Don't heal more than the maximum health
        float actualHealAmount = Mathf.Min(amountToHeal, maxHealth - currentHealth);
        if (actualHealAmount <= 0) return;

        // Calculate proportional repair cost
        var repairCost = new List<ResourceCost>();
        float healRatio = actualHealAmount / maxHealth;

        if (_currentAppearance.fullRepairCost == null || _currentAppearance.fullRepairCost.Count == 0)
        {
            Debug.LogWarning($"[Health] No repair cost defined for tier '{currentTier.tierName}'. Repairing for free.");
            currentHealth = Mathf.Min(currentHealth + actualHealAmount, maxHealth);
            return;
        }

        foreach (var cost in _currentAppearance.fullRepairCost)
        {
            int requiredAmount = Mathf.Max(1, Mathf.CeilToInt(cost.amount * healRatio));
            repairCost.Add(new ResourceCost { resourceItem = cost.resourceItem, amount = requiredAmount });
        }

        // Check and consume resources
        if (inventoryManager.ConsumeItems(repairCost))
        {
            currentHealth = Mathf.Min(currentHealth + actualHealAmount, maxHealth);
            Debug.Log($"[Health] Repaired {gameObject.name} for {actualHealAmount}. Health is now {currentHealth}/{maxHealth}");
        }
        else
        {
            Debug.Log($"[Health] Not enough resources to repair {gameObject.name}.");
        }
    }

    public void Upgrade(InventoryManager inventoryManager)
    {
        if (currentTier == null || currentTier.nextTier == null)
        {
            Debug.Log("This object is at its maximum tier.");
            return;
        }

        // Find appearance to calculate upgrade costs
        if (_currentAppearance == null)
        {
            Debug.LogError($"[Health] Appearance for tier '{currentTier.name}' not found. Cannot determine upgrade cost.", gameObject);
            return;
        }

        BuildingTier nextTier = currentTier.nextTier;

        // Check and consume resources from the cached appearance data
        if (inventoryManager.ConsumeItems(_currentAppearance.upgradeCost))
        {
            Debug.Log($"Upgrading from {currentTier.tierName} to {nextTier.tierName}");
            SetTier(nextTier);
        }
        else
        {
            Debug.Log("Not enough resources to upgrade.");
        }
    }

    /// <summary>
    /// Check if this object can be upgraded at the current moment.
    /// </summary>
    public bool CanUpgrade()
    {
        // TODO: Add other conditions (e.g., not in combat)
        return true;
    }

    /// <summary>
    /// Check if this object can be repaired at the current moment.
    /// </summary>
    public bool CanRepair()
    {
        // TODO: Add other conditions (e.g., not in cooldown)
        return true;
    }

    // Call this from Base.AfterPlace()
    public void SetAsGroundedFoundation()
    {
        isGroundedFoundation = true;
        owner.stability = 1.0f;
        Debug.Log($"[Stability] {gameObject.name} set as grounded foundation with 100% stability.");
        RecalculateStabilityForNeighbors();
    }

    public void RecalculateStability()
    {
        if (isGroundedFoundation)
        {
            owner.stability = 1.0f;
            return;
        }

        float highestNeighborStability = 0f;
        foreach (var neighbor in neighbors.Where(n => n != null))
        {
            // Simple decay model: stability is 80% of the best supporting neighbor
            float potentialStability = neighbor.owner.stability * 0.8f;
            if (potentialStability > highestNeighborStability)
            {
                highestNeighborStability = potentialStability;
            }
        }

        if (Mathf.Abs(owner.stability - highestNeighborStability) > 0.01f)
        {
            owner.stability = highestNeighborStability;
            Debug.Log($"[Stability] {gameObject.name} stability updated to {owner.stability * 100}%.");

            if (owner.stability < 0.1f)
            {
                Destroy(gameObject, 0.5f); // Destroy if stability is too low
            }
            else
            {
                RecalculateStabilityForNeighbors();
            }
        }
    }

    public void RecalculateStabilityForNeighbors()
    {
        foreach (var neighbor in neighbors.Where(n => n != null))
        {
            // Use Invoke to prevent deep recursion and potential stack overflow on large structures
            neighbor.Invoke(nameof(RecalculateStability), 0.1f);
        }
    }

    void OnDestroy()
    {
        // When this block is destroyed, notify its neighbors so they can recalculate their stability
        foreach (var neighbor in neighbors.Where(n => n != null))
        {
            neighbor.neighbors.Remove(this);
            neighbor.Invoke(nameof(RecalculateStability), 0.1f);
        }
        Debug.Log($"[Stability] {gameObject.name} was destroyed, notifying neighbors.");
    }
}
