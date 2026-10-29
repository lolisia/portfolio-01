using System;
using UnityEngine;
using UnityEditor;

namespace aisilol.Editor
{
    [CustomPropertyDrawer(typeof (SetInspectorName))]
    public class SetInspectorNamePropertyDrawer : PropertyDrawer
    {
        private static bool IsInsideArray(SerializedProperty property)
        {
            return property.propertyPath.Contains(".Array.data[");
        }
        
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            try
            {
                if (attribute is SetInspectorName propertyAttribute)
                {
                    if (!IsInsideArray(property))
                        label.text = propertyAttribute.label;
                }
                    
                EditorGUI.PropertyField(position, property, label);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }
    }
}
