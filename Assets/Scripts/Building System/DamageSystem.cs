using System.Collections.Generic;

// Various damage types that can be used in the game.
public enum DamageType
{
    Melee,
    Bullet,
    Explosion,
    Generic // Generic type if none is specified
}

// Class for configuring damage multipliers for specific damage types.
// Used in TierAppearance to configure resistances.
[System.Serializable]
public class DamageModifier
{
    public DamageType type;
    public float multiplier = 1f;
}

// Structure for storing damage information with specific damage type.
[System.Serializable]
public struct DamageInfo
{
    public float amount;
    public DamageType type;
}
