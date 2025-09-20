using UnityEngine;

[System.Serializable]
public class SnapPoint
{
    [Tooltip("������������� �����, ������������ ��� ������������� � �������� ������")] 
    public string id;

    [Tooltip("��������� ������� ����� �������� � ����������� �������")] 
    public Vector3 localPosition;

    [Tooltip("��������� ���������� (Euler) ����� �������� � ����������� �������")] 
    public Vector3 localEulerAngles;

    [Tooltip("������ ����� ��������, ������� ����� ������������ � ���� �����")] 
    public string[] allowedNeighborTags;

    // ������� ������������� ���������� ��������
    public Quaternion LocalRotation => Quaternion.Euler(localEulerAngles);
}
