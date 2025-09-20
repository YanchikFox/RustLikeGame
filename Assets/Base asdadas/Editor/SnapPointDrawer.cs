using UnityEditor;
using UnityEngine;

// Рисует удобный инспектор для SnapPoint, позволяя выбирать теги через выпадающий список
[CustomPropertyDrawer(typeof(SnapPoint))]
public class SnapPointDrawer : PropertyDrawer
{
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        EditorGUI.BeginProperty(position, label, property);

        var idProp = property.FindPropertyRelative("id");
        var posProp = property.FindPropertyRelative("localPosition");
        var eulerProp = property.FindPropertyRelative("localEulerAngles");
        var tagsProp = property.FindPropertyRelative("allowedNeighborTags");

        float lineH = EditorGUIUtility.singleLineHeight;
        float spacing = 2f;
        Rect rect = new Rect(position.x, position.y, position.width, lineH);

        // ID
        EditorGUI.PropertyField(rect, idProp);

        // Position
        rect.y += lineH + spacing;
        EditorGUI.PropertyField(rect, posProp);

        // Rotation
        rect.y += lineH + spacing;
        EditorGUI.PropertyField(rect, eulerProp);

        // Tags array
        rect.y += lineH + spacing;
        EditorGUI.LabelField(rect, "Allowed Neighbor Tags");

        for (int i = 0; i < tagsProp.arraySize; i++)
        {
            rect.y += lineH + spacing;
            var element = tagsProp.GetArrayElementAtIndex(i);
            element.stringValue = EditorGUI.TagField(rect, element.stringValue);
        }

        // Add/Remove buttons
        rect.y += lineH + spacing;
        if (GUI.Button(new Rect(rect.x, rect.y, 20, lineH), "+"))
            tagsProp.arraySize++;
        if (GUI.Button(new Rect(rect.x + 25, rect.y, 20, lineH), "-"))
            if (tagsProp.arraySize > 0)
                tagsProp.arraySize--;

        EditorGUI.EndProperty();
    }

    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        var tagsProp = property.FindPropertyRelative("allowedNeighborTags");
        int tagCount = tagsProp.arraySize;
        // Lines: id, pos, rot, label, each tag, buttons
        int lines = 4 + tagCount + 1;
        return lines * (EditorGUIUtility.singleLineHeight + 2f);
    }
}
