using UnityEngine;

namespace aisilol
{
    public class SetInspectorName : PropertyAttribute
    {
        public readonly string label;

        public SetInspectorName(string label)
        {
            this.label = label;
        }
    }
}
