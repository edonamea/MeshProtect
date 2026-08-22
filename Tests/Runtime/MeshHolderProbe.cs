using UnityEngine;

namespace MPTest
{
    /// <summary>
    /// Stands in for any third-party component that happens to hold a Mesh reference.
    ///
    /// The leak guard cannot work by listing component types: whatever list it has, the next
    /// avatar will carry a component nobody thought of. This probe is that component. It has no
    /// behaviour - the only thing that matters is that a serialised Mesh field on an unknown
    /// MonoBehaviour is enough to put the original mesh in the bundle.
    /// </summary>
    public class MeshHolderProbe : MonoBehaviour
    {
        public Mesh held;
    }
}
