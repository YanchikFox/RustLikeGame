using UnityEngine;

/// <summary>
/// Класс-контейнер для хранения текущего состояния ввода игрока.
/// </summary>
public class PlayerInput
{
    public Vector2 Move { get; set; }
    public Vector2 Look { get; set; }
    public bool Sprint { get; set; }
    public bool Jump { get; set; }
    public bool Crouch { get; set; }
    public bool Zoom { get; set; }
    public bool PlaceObject { get; set; }
    public float Scroll { get; set; }
    public int HotbarDigit { get; set; } = -1; // -1 означает, что ни одна цифра не нажата
}

